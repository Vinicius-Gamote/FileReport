using System.Text;
using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Domain;
using FileReport.Domain.Comparisons;
using FileReport.Infrastructure.Processing;
using FileReport.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
namespace FileReport.IntegrationTests;

public sealed class ProcessingTests
{
    private static ProcessingSettings Settings() => new()
    {
        IoBufferBytes = 17,
        MaxFieldBytes = 256,
        MaxRecordBytes = 1024,
        MaxColumns = 10,
        SortBufferBytes = 2048,
        MergeFanIn = 2,
        MaxScratchBytes = 10485760,
        MaxReportBytes = 10485760,
        MaxSampleCount = 3,
        MaxSampleBytes = 500,
        ReportRetentionDays = 30,
        ResourceSamplingIntervalMilliseconds = 10
    };
    [Theory]
    [InlineData("id,value\n1,\"a,b\"\n", "a,b")]
    [InlineData("\uFEFFid,value\r\n1,\"a\r\nb\"\r\n", "a\r\nb")]
    [InlineData("id,value\n1,\"a\"\"b\"", "a\"b")]
    [InlineData("id,value\n1,NULL", "NULL")]
    [InlineData("id,value\n1,\"\"", "")]
    [InlineData("id,value\n1, A ", " A ")]
    public async Task CsvPreservesExactStrings(string input, string expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var reader = new StrictCsvReader(stream, ',', Settings());
        Assert.Equal(["id", "value"], (await reader.Read(default))!);
        Assert.Equal(["1", expected], (await reader.Read(default))!);
        Assert.Null(await reader.Read(default));
        Assert.Equal(stream.Length, reader.BytesRead);
    }
    [Theory]
    [InlineData("id,v\n1,\"unterminated", "MalformedCsv")]
    [InlineData("id,v\n1,a\"b", "MalformedCsv")]
    [InlineData("id,v\n1,\"a\" x", "MalformedCsv")]
    [InlineData("id,v\r1,x", "MalformedCsv")]
    public async Task MalformedCsvNeverSilentlySucceeds(string input, string code)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var reader = new StrictCsvReader(stream, ',', Settings());
        var error = await Assert.ThrowsAsync<DomainException>(async () => { while (await reader.Read(default) != null) { } });
        Assert.Equal(code, error.Code);
    }
    [Fact]
    public async Task InvalidUtf8AndFieldBoundsFailBeforeUnboundedAllocation()
    {
        foreach (var bytes in new[] { new byte[] { 0xC3, 0x28 }, Encoding.UTF8.GetBytes(new string('a', 257)) })
        {
            using var stream = new MemoryStream(bytes);
            await Assert.ThrowsAsync<DomainException>(() => new StrictCsvReader(stream, ',', Settings()).Read(default));
        }
    }
    [Fact]
    public async Task BlankRecordIsNotAFinalTerminator()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("id\n\n"));
        var reader = new StrictCsvReader(stream, ',', Settings());
        Assert.Equal(["id"], (await reader.Read(default))!); Assert.Equal([""], (await reader.Read(default))!); Assert.Null(await reader.Read(default));
    }
    [Fact]
    public async Task ExternalMergeMatchesOracleAndCapsSamplesAcrossManyRuns()
    {
        for (int seed = 0; seed < 5; seed++)
        {
            var random = new Random(seed);
            var left = Enumerable.Range(0, 100).OrderBy(_ => random.Next()).Select(i => $"{i},v{i}");
            var right = Enumerable.Range(30, 100).OrderBy(_ => random.Next()).Select(i => $"{i},{(i % 2 == 0 ? "changed" : $"v{i}")}");
            var result = await Compare("id,v\n" + string.Join("\n", left), "id,v\n" + string.Join("\n", right));
            Assert.Equal(new ComparisonSummary(30, 30, 35, 35), result.Report.Counts);
            Assert.Equal(100, result.Report.BaselineRecords); Assert.Equal(100, result.Report.CandidateRecords);
            Assert.True(result.Report.SamplesTruncated); Assert.InRange(result.Report.Samples.Length, 1, 3);
            Assert.True(result.Metrics.PhysicalReadBytes > result.Metrics.UniqueInputBytes);
        }
    }
    [Fact]
    public async Task DuplicateKeysAcrossDistantRunsFail() =>
        Assert.Equal("DuplicateKey", (await Assert.ThrowsAsync<DomainException>(() =>
            Compare("id,v\n" + string.Join("\n", Enumerable.Range(0, 100).Select(i => $"{i},x")) + "\n0,x", "id,v\n"))).Code);
    [Theory]
    [InlineData("", "id,v\n", "MissingHeader")]
    [InlineData("id,v\n\n", "id,v\n", "MalformedRecord")]
    [InlineData("id,v\n,x", "id,v\n", "EmptyKey")]
    [InlineData("id,v\n1,x,y", "id,v\n", "MalformedRecord")]
    public async Task FatalInputsProduceNoReport(string left, string right, string code) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => Compare(left, right))).Code);
    [Fact]
    public async Task HeaderOnlyAndReorderedHeadersWork()
    {
        var result = await Compare("id,v\n", "v,id\nvalue,2\n");
        Assert.Equal(new ComparisonSummary(1, 0, 0, 0), result.Report.Counts);
    }
    private static async Task<(ReportData Report, AttemptMetrics Metrics)> Compare(string left, string right)
    {
        var root = Path.Combine(Path.GetTempPath(), "filereport-engine-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        var settings = Settings(); var store = new LocalFileStore(config, settings);
        try
        {
            var job = new ComparisonJob(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            var doc = new JobDocument { Snapshot = job.Capture() };
            foreach (var (side, content) in new[] { (FileSide.Baseline, left), (FileSide.Candidate, right) })
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                var file = await store.Write(Guid.NewGuid(), stream, 100000, null, default);
                var generation = job.BeginUpload(side, job.Revision); job.StoreFile(side, generation, file);
                doc.Files.Add(new(file.FileId, side, "fixture.csv", file.ByteLength, file.Sha256, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)));
            }
            job.SetOptions(new(["id"]), job.Revision); job.Submit(DateTimeOffset.UtcNow, 3);
            job.StartAttempt(1, 1, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)); doc.Snapshot = job.Capture();
            var result = await new ExternalComparisonEngine(store, settings).Execute(doc, 1, _ => { }, default);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "scratch"), "*", SearchOption.AllDirectories));
            return result;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

