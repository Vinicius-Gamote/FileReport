namespace FileReport.Contracts;

public sealed record CredentialsRequest(string Email, string Password);
public sealed record OptionsRequest(string[] Keys, string[]? Columns,
    string BaselineDelimiter = ",", string CandidateDelimiter = ",",
    string BaselineEncoding = "Utf8", string CandidateEncoding = "Utf8");
public sealed record JobResponse(Guid Id, string Revision, string State, string Stage, object[] Files,
    string ServerReceivedBytes, DateTimeOffset CreatedAtUtc, DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? TerminalAtUtc, string? FailureCode, object[] Attempts, bool HasReport, object? Options,
    object Measurements, string? EmailMode);
