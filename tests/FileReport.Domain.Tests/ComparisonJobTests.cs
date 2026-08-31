using FileReport.Domain.Comparisons;

namespace FileReport.Domain.Tests;

public sealed class ComparisonJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(90);
    private static StoredInput File() => new(Guid.NewGuid(), 10, new string('a', 64));

    private static ComparisonJob Ready()
    {
        var job = new ComparisonJob(Guid.NewGuid(), Guid.NewGuid(), Now);
        var first = job.BeginUpload(FileSide.Baseline, job.Revision);
        job.StoreFile(FileSide.Baseline, first, File());
        var second = job.BeginUpload(FileSide.Candidate, job.Revision);
        job.StoreFile(FileSide.Candidate, second, File());
        job.SetOptions(new ComparisonOptions(["id"]), job.Revision);
        return job;
    }

    private static ComparisonJob Processing(int maxAttempts = 3)
    {
        var job = Ready();
        job.Submit(Now, maxAttempts);
        job.StartAttempt(1, 1, Now, Lease);
        return job;
    }

    [Fact]
    public void SubmissionRequiresTwoStoredFilesAndOptions()
    {
        var job = new ComparisonJob(Guid.NewGuid(), Guid.NewGuid(), Now);
        var generation = job.BeginUpload(FileSide.Baseline, job.Revision);
        job.StoreFile(FileSide.Baseline, generation, File());
        job.SetOptions(new ComparisonOptions(["id"]), job.Revision);
        Assert.Equal(JobState.Uploading, job.State);
        Assert.Throws<DomainException>(() => job.Submit(Now, 3));
        var candidate = job.BeginUpload(FileSide.Candidate, job.Revision);
        job.StoreFile(FileSide.Candidate, candidate, File());
        job.Submit(Now, 3);
        Assert.Equal(JobState.PendingDispatch, job.State);
    }

    [Fact]
    public void ReplacingAFileInvalidatesReadinessAndRejectsStaleUpload()
    {
        var job = Ready();
        var generation = job.BeginUpload(FileSide.Baseline, job.Revision);
        Assert.Equal(JobState.Uploading, job.State);
        Assert.Throws<DomainException>(() => job.StoreFile(FileSide.Baseline, generation - 1, File()));
        Assert.Throws<DomainException>(() => job.BeginUpload(FileSide.Baseline, job.Revision));
        job.FailUpload(FileSide.Baseline, generation);
        var replacement = job.BeginUpload(FileSide.Baseline, job.Revision);
        job.StoreFile(FileSide.Baseline, replacement, File());
        Assert.Equal(JobState.Ready, job.State);
    }

    [Fact]
    public void StaleRevisionsCannotMutateAJob()
    {
        var job = Ready();
        Assert.Equal("RevisionConflict", Assert.Throws<DomainException>(() =>
            job.SetOptions(new ComparisonOptions(["other"]), job.Revision - 1)).Code);
    }

    [Fact]
    public void SubmittedFilesAndOptionsAreImmutable()
    {
        var job = Ready();
        job.Submit(Now, 3);
        Assert.Throws<DomainException>(() => job.BeginUpload(FileSide.Baseline, job.Revision));
        Assert.Throws<DomainException>(() => job.SetOptions(new ComparisonOptions(["other"]), job.Revision));
        Assert.Throws<DomainException>(() => job.Submit(Now, 3));
    }

    [Fact]
    public void SubmissionFreezesTheInputVersionAndRetryBudget()
    {
        var job = Ready();
        var inputVersion = job.Revision;
        job.Submit(Now, 2);
        job.MarkQueued(1);
        job.StartAttempt(1, 1, Now, Lease);
        Assert.Equal(inputVersion, job.InputVersion);
        Assert.Equal(2, job.MaxAttempts);
        Assert.True(job.Revision > inputVersion);
    }

    [Fact]
    public void ADelayedPublisherCannotRegressProcessingOrSuccess()
    {
        var job = Processing();
        var revision = job.Revision;
        Assert.False(job.MarkQueued(1));
        Assert.Equal(revision, job.Revision);
        job.Complete(1, new ComparisonSummary(0, 0, 0, 1), 1, 1, Now.AddSeconds(1));
        Assert.False(job.MarkQueued(1));
        Assert.Equal(JobState.Succeeded, job.State);
    }

    [Fact]
    public void WrongOrExpiredLeasesCannotPublishResults()
    {
        var job = Processing();
        var summary = new ComparisonSummary(0, 0, 0, 1);
        Assert.Equal("StaleAttempt", Assert.Throws<DomainException>(() =>
            job.Complete(2, summary, 1, 1, Now.AddSeconds(1))).Code);
        Assert.Equal("LeaseExpired", Assert.Throws<DomainException>(() =>
            job.Complete(1, summary, 1, 1, Now.Add(Lease))).Code);
        Assert.Null(job.Summary);
    }

    [Fact]
    public void AFailedInvariantCannotPublishSuccess()
    {
        var job = Processing();
        Assert.Throws<DomainException>(() =>
            job.Complete(1, new ComparisonSummary(0, 0, 1, 0), 2, 2, Now.AddSeconds(1)));
        Assert.Equal(JobState.Processing, job.State);
        Assert.Null(job.TerminalAtUtc);
    }

    [Fact]
    public void ValidDifferencesAreASuccessfulOutcome()
    {
        var job = Processing();
        job.Complete(1, new ComparisonSummary(1, 1, 1, 0), 2, 2, Now.AddSeconds(1));
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Null(job.FailureCode);
        Assert.Throws<DomainException>(() => job.Fail(1, "TooLate", Now.AddSeconds(2)));
        Assert.Throws<DomainException>(() => job.Expire(Now.AddDays(1)));
    }

    [Fact]
    public void RetryBudgetSurvivesTheDomainLifecycleAndEndsInFailure()
    {
        var job = Processing(2);
        job.ScheduleRetry(1, "StorageUnavailable", Now.AddSeconds(1), Now.AddSeconds(6));
        Assert.Equal(JobState.RetryScheduled, job.State);
        Assert.Equal(2, job.NextAttemptNumber);
        Assert.Throws<DomainException>(() => job.StartAttempt(2, 2, Now.AddSeconds(5), Lease));
        job.StartAttempt(2, 2, Now.AddSeconds(6), Lease);
        job.ScheduleRetry(2, "StorageUnavailable", Now.AddSeconds(7), Now.AddSeconds(37));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(2, job.Attempts.Count);
        Assert.Equal("StorageUnavailable", job.FailureCode);
    }

    [Fact]
    public void ExpiredAttemptRecoveryConsumesBudgetAndFencesTheOldWorker()
    {
        var job = Processing();
        var expired = Now.Add(Lease);
        Assert.Throws<DomainException>(() => job.RecoverExpiredLease(Now.AddSeconds(1), expired));
        job.RecoverExpiredLease(expired, expired.AddSeconds(5));
        Assert.Equal("LeaseExpired", job.Attempts[0].FailureCode);
        Assert.Throws<DomainException>(() => job.StartAttempt(2, 1, expired.AddSeconds(5), Lease));
        job.StartAttempt(2, 2, expired.AddSeconds(5), Lease);
        Assert.Throws<DomainException>(() => job.Complete(1, new ComparisonSummary(0, 0, 0, 1), 1, 1, expired.AddSeconds(6)));
    }

    [Fact]
    public void LastExpiredAttemptBecomesFailed()
    {
        var job = Processing(1);
        job.RecoverExpiredLease(Now.Add(Lease), Now.Add(Lease));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("LeaseExpired", job.FailureCode);
    }

    [Fact]
    public void LeaseRenewalExtendsOwnershipButNeverRevivesExpiredLeases()
    {
        var job = Processing();
        job.RenewLease(1, Now.AddSeconds(60), Lease);
        job.Complete(1, new ComparisonSummary(0, 0, 0, 0), 0, 0, Now.AddSeconds(100));
        Assert.Equal(JobState.Succeeded, job.State);
        var expiredJob = Processing();
        Assert.Throws<DomainException>(() => expiredJob.RenewLease(1, Now.Add(Lease), Lease));
    }

    [Fact]
    public void DeterministicValidationFailureDoesNotScheduleARetry()
    {
        var job = Processing();
        job.Fail(1, "DuplicateKey", Now.AddSeconds(1));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Null(job.RetryDueAtUtc);
        Assert.Single(job.Attempts);
    }

    [Fact]
    public void AbandonedDraftCanExpireButCannotBeReused()
    {
        var job = new ComparisonJob(Guid.NewGuid(), Guid.NewGuid(), Now);
        job.Expire(Now.AddDays(1));
        Assert.Equal(JobState.Expired, job.State);
        Assert.Throws<DomainException>(() => job.BeginUpload(FileSide.Baseline, job.Revision));
    }
}
