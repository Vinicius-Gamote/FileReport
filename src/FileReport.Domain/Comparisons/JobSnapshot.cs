namespace FileReport.Domain.Comparisons;

public sealed record JobSnapshot(Guid Id, Guid OwnerId, DateTimeOffset CreatedAtUtc, JobState State,
    long Revision, string[]? Keys, string[]? Columns, char BaselineDelimiter, char CandidateDelimiter,
    int NextAttemptNumber, int MaxAttempts, long? InputVersion, DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? TerminalAtUtc, DateTimeOffset? RetryDueAtUtc, string? FailureCode,
    ComparisonSummary? Summary, FileSlotSnapshot[] Slots, ProcessingAttempt[] Attempts, long LastFence);
