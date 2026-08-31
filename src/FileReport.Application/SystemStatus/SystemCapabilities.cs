namespace FileReport.Application.SystemStatus;

public sealed record SystemCapabilities(string Stage, bool CanAuthenticate, bool CanSubmitComparisons, bool CanSendEmail);

public sealed class GetSystemCapabilities
{
    public SystemCapabilities Execute() => new("Foundation", false, false, false);
}
