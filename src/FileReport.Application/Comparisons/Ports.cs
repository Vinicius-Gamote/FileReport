using FileReport.Domain.Comparisons;
namespace FileReport.Application.Comparisons;

public interface IJobRepository
{
    Task<JobDocument> Create(Guid ownerId, string key, CancellationToken ct);
    Task<JobDocument> Get(Guid id, Guid ownerId, CancellationToken ct);
    Task<HistoryPage> History(Guid ownerId, Guid? cursor, int limit, CancellationToken ct);
    Task<T> Mutate<T>(Guid id, Guid? ownerId, Func<JobMutation, T> action, CancellationToken ct,
        string? idempotencyKey = null, string? operation = null, string? requestHash = null);
    Task<JobDocument> GetSystem(Guid id, CancellationToken ct);
}
public interface IFileStore
{
    Task<StoredInput> Write(Guid id, Stream input, long limit, Func<long, Task>? progress, CancellationToken ct);
    Stream Open(Guid id);
    string ScratchDirectory(Guid jobId, long fence);
    Task Delete(Guid id, CancellationToken ct);
}
public interface ICsvPreview
{
    Task<string[]> Headers(Stream input, CsvFormat format, CancellationToken ct);
}
public interface IComparisonEngine
{
    Task<(ReportData Report, AttemptMetrics Metrics)> Execute(JobDocument document, long fence,
        Action<string> stage, CancellationToken ct);
}
public interface IIdentityService
{
    Task<IdentityResult> Register(string email, string password, CancellationToken ct);
    Task<IdentityResult> Login(string email, string password, CancellationToken ct);
}
public interface IEmailService
{
    Task<EmailStatus> Request(Guid ownerId, Guid jobId, string key, CancellationToken ct);
    Task<EmailStatus> Get(Guid ownerId, Guid deliveryId, CancellationToken ct);
}
