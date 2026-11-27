using Stowaway.Core.Backups;
using Stowaway.Core.Plans;
using Stowaway.Docker.Backups;
using Stowaway.Docker.Plans;
using Stowaway.Service.Notifications;

using Stowaway.Service.Access;

namespace Stowaway.Service.Api;

public sealed record PlanRunSummary(Guid Id, RunStatus Status, string AppName, DateTimeOffset QueuedAt, DateTimeOffset? FinishedAt, string? Error);

public sealed record PlanView(
    Guid Id,
    string Name,
    bool Enabled,
    PlanSelection Selection,
    IReadOnlyList<string> AppIds,
    IReadOnlyList<string> ExcludedAppIds,
    bool IncludeOrphanedVolumes,
    PlanStrategy Strategy,
    Guid DestinationId,
    string Cron,
    RetentionPolicy Retention,
    bool VerifyRestores,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextRunAt,
    double IntervalHours,
    IReadOnlyList<string> CoveredItemIds,
    IReadOnlyList<PlanRunSummary> RecentRuns);

public sealed record PreviewBody(string Cron);

public sealed record TestResult(bool Ok, string? Error);

public static class PlanEndpoints
{
    public static RouteGroupBuilder MapPlanEndpoints(this RouteGroupBuilder api)
    {
        var plans = api.MapGroup("/plans");

        plans.MapGet("/", async (PlanService service, IBackupRunStore runs, TimeProvider time, CancellationToken ct) =>
        {
            var recent = await runs.ListRecentAsync(500, ct);
            return (await service.ListAsync(ct)).Select(p => ToView(p, service, recent, time.GetUtcNow()));
        });

        plans.MapPost("/", (PlanInput body, PlanService service, CancellationToken ct) =>
            Guard(async () => Results.Created($"/api/plans", await service.CreateAsync(body, ct))));

        plans.MapPut("/{id:guid}", (Guid id, PlanInput body, PlanService service, CancellationToken ct) =>
            Guard(async () => Results.Ok(await service.UpdateAsync(id, body, ct))));

        plans.MapDelete("/{id:guid}", async (Guid id, PlanService service, CancellationToken ct) =>
            await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        plans.MapPost("/{id:guid}/run", (Guid id, PlanService service, CancellationToken ct) =>
            Guard(async () => Results.Accepted(value: new { queued = await service.RunNowAsync(id, ct) })))
            .AllowWithoutFreshCode();

        plans.MapPost("/preview", (PreviewBody body, PlanService service) => service.Preview(body.Cron ?? string.Empty))
            .AllowWithoutFreshCode();

        api.MapPost("/destinations/{id:guid}/maintenance", (Guid id, BackupService backups, CancellationToken ct) =>
            Guard(async () => await backups.EnqueueMaintenanceAsync(id, "manual", ct) is { } run
                ? Results.Accepted(value: run)
                : Results.Conflict(new { detail = "Maintenance for this destination is already queued." })))
            .AllowWithoutFreshCode();

        var channels = api.MapGroup("/notifications");

        channels.MapGet("/", (NotificationService service, CancellationToken ct) => service.ListAsync(ct));

        channels.MapPost("/", (ChannelInput body, NotificationService service, CancellationToken ct) =>
            Guard(async () => Results.Created("/api/notifications", await service.CreateAsync(body, ct))));

        channels.MapPut("/{id:guid}", (Guid id, ChannelInput body, NotificationService service, CancellationToken ct) =>
            Guard(async () => Results.Ok(await service.UpdateAsync(id, body, ct))));

        channels.MapDelete("/{id:guid}", async (Guid id, NotificationService service, CancellationToken ct) =>
            await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        channels.MapPost("/{id:guid}/test", (Guid id, NotificationService service, CancellationToken ct) =>
            Guard(async () =>
            {
                var error = await service.TestAsync(id, ct);
                return Results.Ok(new TestResult(error is null, error));
            }))
            .AllowWithoutFreshCode();

        return api;
    }

    private static PlanView ToView(BackupPlan p, PlanService service, IReadOnlyList<BackupRun> recent, DateTimeOffset now)
    {
        var anchor = p.LastTriggeredAt ?? p.CreatedAt;
        var next = p.Enabled ? Schedule.Next(p.Cron, anchor > now ? anchor : now, 1, TimeZoneInfo.Local).FirstOrDefault() : (DateTimeOffset?)null;
        return new PlanView(
            p.Id, p.Name, p.Enabled, p.Selection, p.AppIds, p.ExcludedAppIds, p.IncludeOrphanedVolumes, p.Strategy, p.DestinationId,
            p.Cron, p.Retention, p.VerifyRestores, p.CreatedAt,
            next == default(DateTimeOffset) ? null : next,
            Schedule.TypicalInterval(p.Cron, now, TimeZoneInfo.Local).TotalHours,
            service.Items(PlanService.AsEnabled(p)).Select(i => i.Id).ToList(),
            recent.Where(r => r.PlanId == p.Id).Take(10)
                .Select(r => new PlanRunSummary(r.Id, r.Status, r.AppName, r.QueuedAt, r.FinishedAt, r.Error)).ToList());
    }

    /// <summary>Maps domain errors to HTTP: validation → 400, missing → 404.</summary>
    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ValidationException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Request could not be completed");
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }
}
