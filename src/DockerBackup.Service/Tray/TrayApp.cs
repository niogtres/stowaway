using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using DockerBackup.Service.Hosting;

namespace DockerBackup.Service.Tray;

/// <summary>
/// Notification-area companion, started per user at logon (<c>DockerBackup.Service.exe --tray</c>). Shows the
/// protection status, opens the UI, and pops a balloon when backups start failing. Talks to the local service
/// with the user's Windows credentials, so it shows only what that user may see.
/// </summary>
public static class TrayApp
{
    public static void Run()
    {
        using var mutex = new Mutex(true, @"Local\DockerBackupTray", out var firstInstance);
        if (!firstInstance)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.Run(new TrayContext(ReadPort()));
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static int ReadPort()
    {
        try
        {
            var settings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(settings));
            return doc.RootElement.GetProperty("DockerBackup").GetProperty("Port").GetInt32();
        }
        catch (Exception)
        {
            return ServiceOptions.DefaultPort;
        }
    }

    private sealed class TrayContext : System.Windows.Forms.ApplicationContext
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
        private readonly string _baseUrl;
        private readonly System.Windows.Forms.NotifyIcon _icon;
        private readonly System.Windows.Forms.ToolStripMenuItem _status;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly HttpClient _http;
        private int _lastFailing = -1;

        public TrayContext(int port)
        {
            _baseUrl = $"https://localhost:{port}";
            _http = new HttpClient(new HttpClientHandler { UseDefaultCredentials = true }) { Timeout = TimeSpan.FromSeconds(15) };

            _status = new System.Windows.Forms.ToolStripMenuItem("Checking…") { Enabled = false };
            var open = new System.Windows.Forms.ToolStripMenuItem("Open DockerBackup", null, (_, _) => Open("/"))
            {
                Font = new System.Drawing.Font(System.Windows.Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold),
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.AddRange([
                _status,
                new System.Windows.Forms.ToolStripSeparator(),
                open,
                new System.Windows.Forms.ToolStripMenuItem("Activity", null, (_, _) => Open("/activity")),
                new System.Windows.Forms.ToolStripMenuItem("Snapshots", null, (_, _) => Open("/snapshots")),
                new System.Windows.Forms.ToolStripSeparator(),
                new System.Windows.Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
            ]);

            _icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
                Text = "DockerBackup",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.DoubleClick += (_, _) => Open("/");

            _timer = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
            _timer.Tick += async (_, _) => await PollAsync();
            _timer.Start();
            _ = PollAsync();
        }

        private async Task PollAsync()
        {
            try
            {
                var inventory = await _http.GetFromJsonAsync<JsonElement>($"{_baseUrl}/api/inventory");
                var reachable = inventory.GetProperty("engineReachable").GetBoolean();
                if (inventory.GetProperty("snapshot") is not { ValueKind: JsonValueKind.Object } snapshot)
                {
                    SetStatus(reachable ? "Discovering…" : "Docker isn't running");
                    return;
                }

                var totals = snapshot.GetProperty("totals");
                var score = totals.GetProperty("protectionScore") is { ValueKind: JsonValueKind.Number } s ? s.GetInt32() : (int?)null;
                var failing = totals.GetProperty("failingItems").GetInt32();
                var stale = totals.GetProperty("staleItems").GetInt32();

                var text = !reachable ? "Docker isn't running"
                    : failing > 0 ? $"{failing} backup{(failing == 1 ? "" : "s")} failing"
                    : stale > 0 ? $"{stale} app{(stale == 1 ? "" : "s")} overdue"
                    : score is { } value ? $"{value}% protected"
                    : "Measuring…";
                SetStatus(text);

                if (_lastFailing >= 0 && failing > _lastFailing)
                {
                    _icon.ShowBalloonTip(10_000, "Backups failing", $"{failing} backup{(failing == 1 ? " is" : "s are")} failing. Open DockerBackup for details.", System.Windows.Forms.ToolTipIcon.Warning);
                }

                _lastFailing = failing;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden)
            {
                SetStatus("No access (needs an administrator account)");
            }
            catch (Exception)
            {
                SetStatus("The DockerBackup service isn't reachable");
            }
        }

        private void SetStatus(string text)
        {
            _status.Text = text;
            var tooltip = $"DockerBackup: {text}";
            _icon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
        }

        private void Open(string path) =>
            Process.Start(new ProcessStartInfo(_baseUrl + path) { UseShellExecute = true });

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            _http.Dispose();
            base.ExitThreadCore();
        }
    }
}

/// <summary>Run by the installer on uninstall (<c>--uninstall-cleanup</c>): undoes what the service set up on the machine.</summary>
public static class UninstallCleanup
{
    public static int Run()
    {
        var removed = HttpsCertificate.RemoveTrusted();
        RemoteFirewall.Remove();
        try
        {
            if (EventLog.SourceExists(ServiceOptions.ServiceName))
            {
                EventLog.DeleteEventSource(ServiceOptions.ServiceName);
            }
        }
        catch (Exception)
        {
            // Best effort: uninstall must not fail because of the event source.
        }

        Console.WriteLine($"Removed {removed} trusted certificate(s).");
        return 0;
    }
}
