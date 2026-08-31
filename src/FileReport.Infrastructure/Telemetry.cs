using System.Diagnostics;
using System.Diagnostics.Metrics;
namespace FileReport.Infrastructure;

public static class Telemetry
{
    public static readonly ActivitySource Source = new("FileReport");
    public static readonly Meter Meter = new("FileReport");
    public static readonly Counter<long> Completed = Meter.CreateCounter<long>("filereport.attempts", description: "Attempt outcomes; differences are not failures.");
}
