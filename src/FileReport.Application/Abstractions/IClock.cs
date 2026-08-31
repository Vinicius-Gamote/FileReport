namespace FileReport.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
