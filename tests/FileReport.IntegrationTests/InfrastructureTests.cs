using System.Text;
using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Domain;
using FileReport.Domain.Comparisons;
using FileReport.Infrastructure;
using FileReport.Infrastructure.Email;
using FileReport.Infrastructure.Messaging;
using FileReport.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace FileReport.IntegrationTests;

public sealed class InfrastructureFactAttribute : FactAttribute
{
    public InfrastructureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_INFRASTRUCTURE_TESTS") != "1") Skip = "Set RUN_INFRASTRUCTURE_TESTS=1 with isolated PostgreSQL and RabbitMQ configured.";
    }
}
public sealed class InfrastructureTests
{
    private static ServiceProvider Services()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "FileReport.slnx"))) root = root.Parent;
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(root!.FullName, "config/processing.defaults.json"));
        if (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development")
            configuration.AddUserSecrets<Program>(optional: true);
        var config = configuration.AddEnvironmentVariables().Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config);
        services.AddFileReport(config);
        return services.BuildServiceProvider();
    }
    [InfrastructureFact]
    public async Task DurableSubmissionOwnerIsolationDuplicatesAndExplicitEmail()
    {
        await using var services = Services();
        var (owner, job) = await Submitted(services);
        var repository = services.GetRequiredService<IJobRepository>();
        var service = services.GetRequiredService<ComparisonService>();
        await Assert.ThrowsAsync<RequestException>(() => repository.Get(job.Snapshot.Id, Guid.NewGuid(), default));
        var command = await Command(services, job.Snapshot.Id);
        var worker = services.GetRequiredService<ComparisonWorker>();
        Assert.False(await worker.Process(Encoding.UTF8.GetBytes(JsonData.Write(command)), default));
        Assert.False(await worker.Process(Encoding.UTF8.GetBytes(JsonData.Write(command)), default));
        var result = await service.Get(owner, job.Snapshot.Id, default);
        Assert.Equal(JobState.Succeeded, result.Snapshot.State);
        Assert.Single(result.Snapshot.Attempts); Assert.NotNull(result.Report);
        Assert.Equal(new ComparisonSummary(1, 1, 1, 1), result.Report.Counts);
        await using var db = await services.GetRequiredService<IDbContextFactory<FileReportDbContext>>().CreateDbContextAsync();
        Assert.False(await db.Emails.AnyAsync(x => x.JobId == job.Snapshot.Id)); // no automatic sends
        var emails = services.GetRequiredService<IEmailService>();
        var key = Guid.NewGuid().ToString();
        var delivery = await emails.Request(owner, job.Snapshot.Id, key, default);
        Assert.Equal(delivery.Id, (await emails.Request(owner, job.Snapshot.Id, key, default)).Id);
        await Assert.ThrowsAsync<RequestException>(() => emails.Get(Guid.NewGuid(), delivery.Id, default));
        var payload = await db.Emails.Where(x => x.Id == delivery.Id).Select(x => x.Payload).SingleAsync();
        Assert.DoesNotContain("fixture.csv", payload); Assert.DoesNotContain("old-sensitive", payload);
        // These tests explicitly force a fake provider; no network email calls are made.
        Assert.Equal("Fake", services.GetRequiredService<IConfiguration>()["Email:Mode"]);
        await services.GetRequiredService<EmailDispatcher>().DispatchOne(default);
        Assert.Equal("Accepted", (await emails.Get(owner, delivery.Id, default)).State);
    }
    [InfrastructureFact]
    public async Task SubmissionRollbackAndRequestHashConflict()
    {
        await using var services = Services(); var (owner, job) = await Submitted(services);
        var repository = services.GetRequiredService<IJobRepository>();
        var before = await repository.Get(job.Snapshot.Id, owner, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.Mutate<bool>(job.Snapshot.Id, owner, m =>
        { m.Document.Stage = "Must roll back"; throw new InvalidOperationException(); }, default));
        Assert.Equal(before.Stage, (await repository.Get(job.Snapshot.Id, owner, default)).Stage);
        var doc = await services.GetRequiredService<ComparisonService>().Create(owner, Guid.NewGuid().ToString(), default);
        var idempotency = Guid.NewGuid().ToString();
        await repository.Mutate(doc.Snapshot.Id, owner, _ => true, default, idempotency, "test", "first");
        await Assert.ThrowsAsync<RequestException>(() => repository.Mutate(doc.Snapshot.Id, owner, _ => true, default, idempotency, "test", "different"));
    }
    [InfrastructureFact]
    public async Task ExpiredAttemptConsumesBudgetAndStaleFenceCannotCommit()
    {
        await using var services = Services(); var (owner, job) = await Submitted(services);
        var repository = services.GetRequiredService<IJobRepository>();
        await repository.Mutate(job.Snapshot.Id, owner, m =>
        {
            m.Job.StartAttempt(1, 1, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1)); return true;
        }, default);
        await Task.Delay(20);
        await services.GetRequiredService<RecoveryService>().Recover(default);
        var recovered = await repository.Get(job.Snapshot.Id, owner, default);
        Assert.Equal(JobState.RetryScheduled, recovered.Snapshot.State); Assert.Equal(2, recovered.Snapshot.NextAttemptNumber);
        Assert.Equal("LeaseExpired", recovered.Snapshot.Attempts[0].FailureCode);
        await Assert.ThrowsAsync<DomainException>(() => repository.Mutate(job.Snapshot.Id, owner, m =>
        { m.Job.Complete(1, new(0, 0, 0, 0), 0, 0, DateTimeOffset.UtcNow); return true; }, default));
        await using var db = await services.GetRequiredService<IDbContextFactory<FileReportDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await db.Outbox.CountAsync(x => x.JobId == job.Snapshot.Id));
    }
    [InfrastructureFact]
    public async Task ValidationFaultIsAcknowledgedWithoutPublishingASuccessfulPartialReport()
    {
        await using var services = Services(); var (_, job) = await Submitted(services, "id,value\n1,a\n1,b\n");
        Assert.False(await services.GetRequiredService<ComparisonWorker>().Process(
            Encoding.UTF8.GetBytes(JsonData.Write(await Command(services, job.Snapshot.Id))), default));
        var result = await services.GetRequiredService<IJobRepository>().GetSystem(job.Snapshot.Id, default);
        Assert.Equal(JobState.Failed, result.Snapshot.State); Assert.Equal("DuplicateKey", result.Snapshot.FailureCode);
        Assert.Null(result.Report); Assert.False(result.DeadLetterRequested);
        Assert.False(result.Metrics[0].Complete); Assert.Null(result.Metrics[0].BaselineRecords);
    }
    [InfrastructureFact]
    public async Task BrokerConfirmsMandatoryRoutingAndDeadLettersPoison()
    {
        await using var services = Services();
        var broker = services.GetRequiredService<Broker>();
        await using var connection = await broker.Connect(default);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        await broker.Declare(channel, default);
        await Assert.ThrowsAnyAsync<Exception>(async () => await channel.BasicPublishAsync(Broker.Exchange,
            "no-such-route", true, new BasicProperties { Persistent = true }, "{}"u8.ToArray()));
        // Use a test-only quorum queue with the same at-least-once dead-letter prerequisites.
        var suffix = Guid.NewGuid().ToString("N");
        var queue = "filereport.test." + suffix; var dlq = queue + ".dlq";
        try
        {
            await channel.QueueDeclareAsync(dlq, true, false, false, new Dictionary<string, object?> { ["x-queue-type"] = "quorum" });
            await channel.QueueDeclareAsync(queue, true, false, false, new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = dlq,
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish"
            });
            await channel.BasicPublishAsync("", queue, true, new BasicProperties { Persistent = true }, "poison"u8.ToArray());
            var message = await channel.BasicGetAsync(queue, false); Assert.NotNull(message);
            await channel.BasicRejectAsync(message.DeliveryTag, false);
            BasicGetResult? dead = null;
            for (int i = 0; i < 40 && dead == null; i++) { await Task.Delay(100); dead = await channel.BasicGetAsync(dlq, true); }
            Assert.NotNull(dead); Assert.Equal("poison", Encoding.UTF8.GetString(dead.Body.Span));
        }
        finally { await channel.QueueDeleteAsync(queue, false, false); await channel.QueueDeleteAsync(dlq, false, false); }
    }
    private static async Task<(Guid Owner, JobDocument Job)> Submitted(ServiceProvider services, string? baseline = null)
    {
        var identity = await services.GetRequiredService<IIdentityService>().Register($"test-{Guid.NewGuid():N}@example.test", "SyntheticPassword12", default);
        var service = services.GetRequiredService<ComparisonService>();
        var job = await service.Create(identity.Id, Guid.NewGuid().ToString(), default);
        foreach (var (side, text) in new[] { (FileSide.Baseline, baseline ?? "id,value\n1,same\n2,old-sensitive\n3,removed\n"), (FileSide.Candidate, "value,id\nadded,4\nnew,2\nsame,1\n") })
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            job = await service.Upload(identity.Id, job.Snapshot.Id, side, job.Snapshot.Revision, "fixture.csv", stream, default);
        }
        job = await service.Options(identity.Id, job.Snapshot.Id, job.Snapshot.Revision, ["id"], null, ',', ',', default);
        var key = Guid.NewGuid().ToString(); var revision = job.Snapshot.Revision;
        job = await service.Submit(identity.Id, job.Snapshot.Id, revision, key, default);
        var duplicate = await service.Submit(identity.Id, job.Snapshot.Id, revision, key, default);
        Assert.Equal(job.Snapshot.Id, duplicate.Snapshot.Id);
        return (identity.Id, job);
    }
    private static async Task<ComparisonCommand> Command(ServiceProvider services, Guid id)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<FileReportDbContext>>().CreateDbContextAsync();
        return JsonData.Read<ComparisonCommand>((await db.Outbox.SingleAsync(x => x.JobId == id)).Payload);
    }
}
