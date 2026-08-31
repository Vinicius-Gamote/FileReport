namespace FileReport.Contracts;

public sealed record SystemStatusResponse(
    string Application,
    string Stage,
    bool CanAuthenticate,
    bool CanSubmitComparisons,
    bool CanSendEmail,
    string MeasurementStatus);
