using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FileReport.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace FileReport.Infrastructure.Email;

public sealed class EmailDispatcher(IDbContextFactory<FileReportDbContext> factory, IConfiguration config,
    IHttpClientFactory clients, ILogger<EmailDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await DispatchOne(ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { logger.LogWarning("Email dispatcher dependency unavailable."); }
            await Task.Delay(2000, ct);
        }
    }
    public async Task DispatchOne(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var row = await db.Emails.FromSqlInterpolated($"""
            SELECT * FROM "Emails" WHERE "State" IN ('Pending','Sending') AND "AvailableAtUtc" <= {now}
            AND ("LeaseUntilUtc" IS NULL OR "LeaseUntilUtc" < {now})
            ORDER BY "CreatedAtUtc" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).FirstOrDefaultAsync(ct);
        if (row == null) return;
        if (row.FirstSendAtUtc < now.AddHours(-23) || row.Attempts >= 20)
        {
            row.State = "Unknown"; row.ErrorCode = "ReconciliationWindowExpired";
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return;
        }
        row.State = "Sending"; row.FirstSendAtUtc ??= now; row.LeaseUntilUtc = now.AddSeconds(60);
        row.Claim = Guid.NewGuid(); row.Attempts++;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        string state = "Sending"; string? error = null, providerId = null;
        try
        {
            if (config["Email:Mode"] == "Fake") { state = "Accepted"; providerId = $"fake-{row.Id:N}"; }
            else if (string.IsNullOrWhiteSpace(config["Email:ApiKey"])) { state = "Failed"; error = "ProviderNotConfigured"; }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Email:ApiKey"]);
                request.Headers.Add("Idempotency-Key", $"filereport-{row.Id:N}");
                request.Content = new StringContent(row.Payload, Encoding.UTF8, "application/json");
                using var client = clients.CreateClient("Resend");
                using var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    providerId = data.RootElement.GetProperty("id").GetString(); state = "Accepted";
                }
                else if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict))
                { state = "Failed"; error = "ProviderRejected"; }
                else error = "ProviderTemporarilyUnavailable";
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { error = "ProviderOutcomeUncertain"; }
        await db.Emails.Where(x => x.Id == row.Id && x.Claim == row.Claim).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.State, state).SetProperty(x => x.ErrorCode, error)
                .SetProperty(x => x.ProviderId, providerId).SetProperty(x => x.LeaseUntilUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.AvailableAtUtc, DateTimeOffset.UtcNow.AddMinutes(1)), ct);
    }
}
