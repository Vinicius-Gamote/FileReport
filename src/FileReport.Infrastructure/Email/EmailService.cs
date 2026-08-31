using FileReport.Application.Comparisons;
using FileReport.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
namespace FileReport.Infrastructure.Email;

public sealed class EmailService(IDbContextFactory<FileReportDbContext> factory, IJobRepository jobs, IConfiguration config) : IEmailService
{
    public async Task<EmailStatus> Request(Guid ownerId, Guid jobId, string key, CancellationToken ct)
    {
        ComparisonService.Key(key);
        var doc = await jobs.Get(jobId, ownerId, ct);
        if (doc.Report == null) throw new RequestException("ReportNotReady", "Only successful reports can be emailed.", 409);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(79132517)", ct);
        var prior = await db.Emails.SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.RequestKey == key, ct);
        if (prior != null)
        {
            if (prior.JobId != jobId) throw new RequestException("IdempotencyConflict", "This key belongs to another request.", 409);
            return Status(prior);
        }
        var now = DateTimeOffset.UtcNow;
        if (await db.Emails.CountAsync(x => x.OwnerId == ownerId && x.CreatedAtUtc > now.AddDays(-1), ct) >= 10 ||
            await db.Emails.CountAsync(x => x.CreatedAtUtc > now.AddHours(-1), ct) >= 1000)
            throw new RequestException("EmailRateLimit", "The email request limit is reached.", 429);
        var email = await db.Users.Where(x => x.Id == ownerId).Select(x => x.Email).SingleAsync(ct);
        var counts = doc.Report.Counts;
        var reportBase = config["Email:ReportBaseUrl"] ?? throw new InvalidOperationException("Email:ReportBaseUrl is required.");
        var payload = new
        {
            from = config["Email:From"],
            to = new[] { email },
            subject = "Your FileReport comparison",
            text = $"Comparison completed. Added: {counts.Added}; Removed: {counts.Removed}; Changed: {counts.Changed}; Unchanged: {counts.Unchanged}. Sign in to view: {reportBase.TrimEnd('/')}/?job={jobId}"
        };
        var row = new EmailRow
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            JobId = jobId,
            RequestKey = key,
            Recipient = email,
            Payload = JsonData.Write(payload),
            CreatedAtUtc = now,
            AvailableAtUtc = now
        };
        db.Emails.Add(row); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Status(row);
    }
    public async Task<EmailStatus> Get(Guid ownerId, Guid deliveryId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return Status(await db.Emails.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deliveryId && x.OwnerId == ownerId, ct)
            ?? throw new RequestException("NotFound", "Delivery not found.", 404));
    }
    internal static EmailStatus Status(EmailRow row) => new(row.Id, row.State, row.Recipient, row.ErrorCode, row.CreatedAtUtc);
}
