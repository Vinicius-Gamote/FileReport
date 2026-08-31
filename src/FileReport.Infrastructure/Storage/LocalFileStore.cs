using System.Security.Cryptography;
using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Domain.Comparisons;
using Microsoft.Extensions.Configuration;

namespace FileReport.Infrastructure.Storage;

public sealed class LocalFileStore : IFileStore
{
    public string Root { get; }
    private readonly ProcessingSettings _settings;
    public LocalFileStore(IConfiguration config, ProcessingSettings settings)
    {
        Root = Path.GetFullPath(config["Storage:Root"] ?? throw new InvalidOperationException("Storage:Root is required."));
        _settings = settings;
        Directory.CreateDirectory(Path.Combine(Root, "objects"));
        Directory.CreateDirectory(Path.Combine(Root, "temporary"));
        Directory.CreateDirectory(Path.Combine(Root, "scratch"));
    }
    public string ObjectPath(Guid id) => Path.Combine(Root, "objects", id.ToString("N"));
    public async Task<StoredInput> Write(Guid id, Stream input, long limit, Func<long, Task>? progress, CancellationToken ct)
    {
        var temporary = Path.Combine(Root, "temporary", Guid.NewGuid().ToString("N"));
        long bytes = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                _settings.IoBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[_settings.IoBufferBytes];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    bytes = checked(bytes + read);
                    if (bytes > limit) throw new RequestException("FileTooLarge", "The file exceeds the configured byte limit.", 413);
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (progress != null) await progress(bytes);
                }
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, ObjectPath(id), overwrite: false);
            return new(id, bytes, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public Stream Open(Guid id) => new FileStream(ObjectPath(id), FileMode.Open, FileAccess.Read, FileShare.Read,
        _settings.IoBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
    public string ScratchDirectory(Guid jobId, long fence)
    {
        var path = Path.Combine(Root, "scratch", jobId.ToString("N"), fence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(path); return path;
    }
    public Task Delete(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); File.Delete(ObjectPath(id)); return Task.CompletedTask;
    }
}
