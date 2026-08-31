using FileReport.Application.Comparisons;
using FileReport.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
namespace FileReport.Api;

[Authorize]
public sealed class JobHub(ComparisonService service) : Hub
{
    public async Task<object> Subscribe(Guid jobId)
    {
        try
        {
            var owner = Transport.Owner(Context.User!);
            await service.Get(owner, jobId, Context.ConnectionAborted);
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(jobId), Context.ConnectionAborted);
            return Transport.Job(await service.Get(owner, jobId, Context.ConnectionAborted));
        }
        catch (RequestException) { throw new HubException("Comparison not found."); }
    }
    public Task Unsubscribe(Guid jobId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(jobId));
    public static string Group(Guid id) => $"comparison:{id:N}";
}
public sealed class NotificationDispatcher(IDbContextFactory<FileReportDbContext> factory, IHubContext<JobHub> hub,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var rows = await db.Notifications.Where(x => x.SentAtUtc == null).OrderBy(x => x.CreatedAtUtc).Take(100).ToArrayAsync(ct);
                foreach (var row in rows)
                {
                    var ev = JsonData.Read<JobEvent>(row.Payload);
                    await hub.Clients.Group(JobHub.Group(row.JobId)).SendAsync("JobUpdated.v1", ev, ct);
                    row.SentAtUtc = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { logger.LogWarning("Notification dispatch unavailable; HTTP snapshots remain authoritative."); }
            await Task.Delay(1000, ct);
        }
    }
}
