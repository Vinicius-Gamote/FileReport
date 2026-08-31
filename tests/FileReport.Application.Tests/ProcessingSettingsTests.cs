using System.Text.Json;
using FileReport.Application.Configuration;
using FileReport.Application.SystemStatus;

namespace FileReport.Application.Tests;

public sealed class ProcessingSettingsTests
{
    private static ProcessingSettings Defaults()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "FileReport.slnx")))
            folder = folder.Parent;

        var path = Path.Combine(folder!.FullName, "config", "processing.defaults.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("Processing").Deserialize<ProcessingSettings>()!;
    }

    [Fact]
    public void CheckedInDefaultsSatisfyRecoveryAndResourceConstraints()
    {
        Assert.Empty(Defaults().GetValidationErrors());
    }

    [Fact]
    public void IncompleteConfigurationIsRejected()
    {
        Assert.NotEmpty(new ProcessingSettings().GetValidationErrors());
    }

    [Theory]
    [InlineData(3600)]
    [InlineData(3599)]
    public void BrokerTimeoutMustExceedExecutionTimeout(int acknowledgmentTimeout)
    {
        var settings = Defaults();
        settings.ConsumerAcknowledgmentTimeoutSeconds = acknowledgmentTimeout;
        Assert.Contains(settings.GetValidationErrors(), error => error.Contains("acknowledgment", StringComparison.Ordinal));
    }

    [Fact]
    public void RetryScheduleMustCoverTheFiniteAttemptBudget()
    {
        var settings = Defaults();
        settings.MaxAttempts = 4;
        Assert.Contains(settings.GetValidationErrors(), error => error.Contains("retry", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeLeaseAndMemoryRelationshipsAreRejected()
    {
        var settings = Defaults();
        settings.LeaseDurationSeconds = 30;
        settings.SortBufferBytes = 100;
        Assert.Equal(2, settings.GetValidationErrors().Count);
    }

    [Fact]
    public void FoundationDoesNotAdvertiseUnimplementedCapabilities()
    {
        var status = new GetSystemCapabilities().Execute();
        Assert.False(status.CanAuthenticate);
        Assert.False(status.CanSubmitComparisons);
        Assert.False(status.CanSendEmail);
    }
}
