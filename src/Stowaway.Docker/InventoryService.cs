using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Stowaway.Core.Engine;
using Stowaway.Core.Inventory;
using Stowaway.Core.Plans;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stowaway.Docker;

public sealed class InventoryOptions
{
    public const string SectionName = "Stowaway:Inventory";

    /// <summary>Wait after a trigger so bursts of engine events (e.g. <c>compose up</c>) cause a single rescan.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Safety-net rescan in case an event was missed.</summary>
    public TimeSpan RescanInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often volume and bind-mount sizes are re-measured.</summary>
    public TimeSpan SizeInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Time budget for measuring a single bind-mount path.</summary>
    public TimeSpan BindSizeBudget { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Keeps the inventory current: scans on start, on engine events (debounced) and periodically; measures
/// sizes in a slower loop. Only publishes a new snapshot when something actually changed.
/// </summary>
public sealed class InventoryService(
    DockerEngineMonitor engine,
    IOptions<InventoryOptions> options,
    TimeProvider time,
    ILogger<InventoryService> logger,
    IProtectionSource? protection = null) : BackgroundService, IInventoryProvider
{
    private static readonly JsonSerializerOptions FingerprintJson = new() { WriteIndented = false };

    private readonly InventoryOptions _options = options.Value;
    private readonly Channel<bool> _scanTriggers = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<bool> _sizeTriggers = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentDictionary<string, long> _volumeSizes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _pathSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private RawInventory? _lastRaw;
    private InventorySnapshot? _current;
    private string? _fingerprint;
    private long _version;

    public InventorySnapshot? Current => _current;

    public event Action<InventorySnapshot>? Changed;

    public void RequestRefresh() => _scanTriggers.Writer.TryWrite(true);

    private void RequestSizes() => _sizeTriggers.Writer.TryWrite(true);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        engine.StatusChanged += OnEngineStatusChanged;
        stoppingToken.Register(() => engine.StatusChanged -= OnEngineStatusChanged);
        if (protection is not null)
        {
            protection.Changed += Rebuild;
            stoppingToken.Register(() => protection.Changed -= Rebuild);
        }

        RequestRefresh();
        return Task.WhenAll(
            ScanLoopAsync(stoppingToken),
            PeriodicAsync(_options.RescanInterval, RequestRefresh, stoppingToken),
            EventLoopAsync(stoppingToken),
            SizeLoopAsync(stoppingToken),
            PeriodicAsync(_options.SizeInterval, RequestSizes, stoppingToken));
    }

    private void OnEngineStatusChanged(EngineStatus status)
    {
        if (status.Reachable)
        {
            RequestRefresh();
            RequestSizes();
        }
    }

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        await foreach (var _ in _scanTriggers.Reader.ReadAllAsync(ct))
        {
            await Task.Delay(_options.Debounce, time, ct);
            _scanTriggers.Reader.TryRead(out var _);

            if (engine.ActiveEndpoint is not { } endpoint)
            {
                continue;
            }

            try
            {
                var raw = await new DockerInventoryReader(endpoint).ReadAsync(ct);
                var isFirst = _lastRaw is null;
                lock (_gate)
                {
                    _lastRaw = raw;
                }

                Rebuild();
                if (isFirst || HasUnmeasuredItems(raw))
                {
                    RequestSizes();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Inventory scan failed");
            }
        }
    }

    private async Task EventLoopAsync(CancellationToken ct)
    {
        var parameters = new ContainerEventsParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["type"] = new Dictionary<string, bool> { ["container"] = true, ["volume"] = true },
            },
        };

        while (!ct.IsCancellationRequested)
        {
            if (engine.ActiveEndpoint is not { } endpoint)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), time, ct);
                continue;
            }

            try
            {
                using var client = new DockerClientBuilder().WithEndpoint(endpoint).WithTimeout(Timeout.InfiniteTimeSpan).Build();
                var progress = new Progress<Message>(OnEngineEvent);
                logger.LogDebug("Subscribed to engine events at {Endpoint}", endpoint);
                await client.System.MonitorEventsAsync(parameters, progress, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Engine event stream ended; resubscribing");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), time, ct);
        }
    }

    private void OnEngineEvent(Message message)
    {
        // exec_* and health probes are noise for the inventory, except health status changes.
        var action = message.Action ?? string.Empty;
        if (action.StartsWith("exec_", StringComparison.Ordinal) || action is "top" or "attach" or "resize" or "archive-path")
        {
            return;
        }

        RequestRefresh();
        if (message.Type == "volume" && action is "create" or "destroy" || message.Type == "container" && action is "create" or "destroy")
        {
            RequestSizes();
        }
    }

    private async Task SizeLoopAsync(CancellationToken ct)
    {
        await foreach (var _ in _sizeTriggers.Reader.ReadAllAsync(ct))
        {
            await Task.Delay(TimeSpan.FromSeconds(5), time, ct);
            _sizeTriggers.Reader.TryRead(out var _);

            if (engine.ActiveEndpoint is not { } endpoint || _lastRaw is null)
            {
                continue;
            }

            try
            {
                await MeasureAsync(endpoint, _lastRaw, ct);
                Rebuild();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Measuring data sizes failed");
            }
        }
    }

    private async Task MeasureAsync(Uri endpoint, RawInventory raw, CancellationToken ct)
    {
        var volumes = await new DockerInventoryReader(endpoint).ReadVolumeSizesAsync(ct);
        foreach (var stale in _volumeSizes.Keys.Except(volumes.Keys).ToList())
        {
            _volumeSizes.TryRemove(stale, out _);
        }

        foreach (var (name, size) in volumes)
        {
            _volumeSizes[name] = size;
        }

        Rebuild(); // publish volume sizes before the slower host-path walk

        var paths = BindPaths(raw).ToList();
        foreach (var path in paths)
        {
            var size = await Task.Run(() => HostPathSizer.Measure(path, _options.BindSizeBudget, ct: ct), ct);
            if (size is { } bytes)
            {
                _pathSizes[path] = bytes;
            }
            else
            {
                logger.LogInformation("Could not size {Path} within budget (missing, inaccessible or very large)", path);
            }
        }

        foreach (var stale in _pathSizes.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).ToList())
        {
            _pathSizes.TryRemove(stale, out _);
        }
    }

    /// <summary>Windows paths of bind mounts that are backed up by default.</summary>
    /// <summary>Host folders that belong to an app (inside its project folder): the only ones worth measuring.</summary>
    private static IEnumerable<string> BindPaths(RawInventory raw) =>
        InventoryBuilder.WithoutOwnResources(raw).Containers
            .SelectMany(c => c.Mounts.Select(m => (Mount: m, ProjectDir: InventoryBuilder.ProjectDir(c))))
            .Where(x => x.Mount.Type == "bind" && HostPaths.BindExclusionReason(x.Mount.Source, x.Mount.Destination, x.ProjectDir) is null)
            .Select(x => HostPaths.ToWindowsPath(x.Mount.Source))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private bool HasUnmeasuredItems(RawInventory raw) =>
        raw.Volumes.Any(v => !_volumeSizes.ContainsKey(v.Name)) || BindPaths(raw).Any(p => !_pathSizes.ContainsKey(p));

    private void Rebuild()
    {
        InventorySnapshot? published = null;
        lock (_gate)
        {
            if (_lastRaw is null)
            {
                return;
            }

            var sizes = new SizeIndex(
                new Dictionary<string, long>(_volumeSizes, StringComparer.Ordinal),
                new Dictionary<string, long>(_pathSizes, StringComparer.OrdinalIgnoreCase));
            var now = time.GetUtcNow();
            var candidate = InventoryBuilder.Build(_lastRaw, sizes, _version + 1, now, protection?.CreateResolver(now), Platforms.Of(engine.Current.OsType));
            var fingerprint = JsonSerializer.Serialize(candidate with { Version = 0, CapturedAt = default }, FingerprintJson);
            if (fingerprint == _fingerprint)
            {
                return;
            }

            _version++;
            _fingerprint = fingerprint;
            _current = candidate;
            published = candidate;
        }

        logger.LogDebug("Inventory v{Version}: {Apps} apps, {Volumes} volumes", published.Version, published.Totals.Apps, published.Totals.Volumes);
        Changed?.Invoke(published);
    }

    private async Task PeriodicAsync(TimeSpan interval, Action action, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            action();
        }
    }
}
