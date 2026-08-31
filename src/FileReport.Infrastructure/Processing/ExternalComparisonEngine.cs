using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Domain;
using FileReport.Domain.Comparisons;
using FileReport.Infrastructure.Persistence;

namespace FileReport.Infrastructure.Processing;

public sealed class ExternalComparisonEngine(IFileStore store, ProcessingSettings settings) : IComparisonEngine
{
    public async Task<(ReportData Report, AttemptMetrics Metrics)> Execute(JobDocument document, long fence,
        Action<string> stage, CancellationToken ct)
    {
        var options = ComparisonJob.Restore(document.Snapshot).Options!;
        var workspace = store.ScratchDirectory(document.Snapshot.Id, fence);
        var disk = new ScratchBudget(workspace, settings.MaxScratchBytes);
        var timers = new Dictionary<string, double>();
        var watch = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        long allocatedStart = GC.GetTotalAllocatedBytes(), peakRss = 0, peakHeap = 0;
        int samples = 0;
        using var samplerCancellation = new CancellationTokenSource();
        void Sample()
        {
            process.Refresh(); peakRss = Math.Max(peakRss, process.WorkingSet64);
            peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false)); samples++;
        }
        Sample();
        var sampler = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settings.ResourceSamplingIntervalMilliseconds));
            try { while (await timer.WaitForNextTickAsync(samplerCancellation.Token)) Sample(); }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        try
        {
            using var baseline = store.Open(document.Files.Single(f => f.Side == FileSide.Baseline).Id);
            using var candidate = store.Open(document.Files.Single(f => f.Side == FileSide.Candidate).Id);
            var left = new StrictCsvReader(baseline, options.BaselineFormat.Delimiter, settings);
            var right = new StrictCsvReader(candidate, options.CandidateFormat.Delimiter, settings);
            var bh = await left.Read(ct) ?? throw new DomainException("MissingHeader", "Baseline has no header.");
            var ch = await right.Read(ct) ?? throw new DomainException("MissingHeader", "Candidate has no header.");
            var schema = new ComparisonSchema(bh, ch, options);
            stage("Sorting Baseline"); var sw = Stopwatch.StartNew();
            var sortedLeft = await Sort(left, schema, FileSide.Baseline, disk, ct);
            timers["sortBaseline"] = sw.Elapsed.TotalSeconds;
            stage("Sorting Candidate"); sw.Restart();
            var sortedRight = await Sort(right, schema, FileSide.Candidate, disk, ct);
            timers["sortCandidate"] = sw.Elapsed.TotalSeconds;
            stage("Comparing"); sw.Restart();
            long added = 0, removed = 0, changed = 0, unchanged = 0, sampleBytes = 0, outputBytes = 0;
            var retained = new List<Difference>(); bool truncated = false;
            var outputPath = disk.NewPath();
            using (var l = Open(sortedLeft, disk))
            using (var r = Open(sortedRight, disk))
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, settings.IoBufferBytes, true))
            {
                var lkeys = new OrderedKeyValidator(); var rkeys = new OrderedKeyValidator();
                ComparisonRecord? NextRecord(BinaryReader reader, OrderedKeyValidator validator)
                {
                    var record = Read(reader); if (record != null) validator.Accept(record.Key); return record;
                }
                var a = NextRecord(l, lkeys); var b = NextRecord(r, rkeys);
                while (a != null || b != null)
                {
                    ct.ThrowIfCancellationRequested();
                    var order = a is null ? 1 : b is null ? -1 : a.Key.CompareTo(b.Key);
                    var x = order <= 0 ? a : null; var y = order >= 0 ? b : null;
                    var kind = ComparisonPolicy.Classify(x, y);
                    switch (kind)
                    {
                        case DifferenceKind.Added: added++; break;
                        case DifferenceKind.Removed: removed++; break;
                        case DifferenceKind.Changed: changed++; break;
                        default: unchanged++; break;
                    }
                    if (kind != DifferenceKind.Unchanged)
                    {
                        var difference = new Difference(kind.ToString(), (x ?? y)!.Key.Components.ToArray(), x?.Values.ToArray(), y?.Values.ToArray());
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(difference, JsonData.Options);
                        outputBytes = checked(outputBytes + bytes.Length + 1);
                        if (outputBytes > settings.MaxReportBytes) throw new DomainException("ReportQuota", "The difference artifact exceeds the output quota.");
                        disk.Allocate(bytes.Length + 1);
                        await output.WriteAsync(bytes, ct); await output.WriteAsync(new byte[] { 10 }, ct);
                        if (retained.Count < settings.MaxSampleCount && sampleBytes + bytes.Length <= settings.MaxSampleBytes)
                        { retained.Add(difference); sampleBytes += bytes.Length; }
                        else truncated = true;
                    }
                    if (order <= 0) a = NextRecord(l, lkeys);
                    if (order >= 0) b = NextRecord(r, rkeys);
                }
                await output.FlushAsync(ct);
            }
            var counts = new ComparisonSummary(added, removed, changed, unchanged);
            counts.ValidateRecordCounts(sortedLeft.Count, sortedRight.Count);
            timers["mergeCompare"] = sw.Elapsed.TotalSeconds;
            stage("Finalizing report"); sw.Restart();
            var artifactId = Guid.NewGuid();
            await using var artifactStream = File.OpenRead(outputPath);
            var artifact = await store.Write(artifactId, artifactStream, settings.MaxReportBytes, null, ct);
            disk.ReadBytes += artifact.ByteLength; disk.WrittenBytes += artifact.ByteLength;
            timers["artifactFinalization"] = sw.Elapsed.TotalSeconds;
            await samplerCancellation.CancelAsync(); await sampler; Sample();
            var metrics = new AttemptMetrics(document.Snapshot.NextAttemptNumber, document.Files.Sum(f => f.Bytes),
                left.BytesRead + right.BytesRead + disk.ReadBytes, disk.WrittenBytes, disk.Peak,
                sortedLeft.Count, sortedRight.Count, watch.Elapsed.TotalSeconds,
                (process.TotalProcessorTime - cpuStart).TotalSeconds, peakRss, peakHeap,
                GC.GetTotalAllocatedBytes() - allocatedStart, settings.ResourceSamplingIntervalMilliseconds,
                samples, true, "Succeeded", timers);
            return (new(sortedLeft.Count, sortedRight.Count, counts,
                new(artifact.FileId, artifact.ByteLength, artifact.Sha256, DateTimeOffset.UtcNow.AddDays(settings.ReportRetentionDays)),
                retained.ToArray(), truncated, schema.ComparedColumns.ToArray()), metrics);
        }
        finally
        {
            await samplerCancellation.CancelAsync(); await sampler;
            // All files here were generated for this fenced attempt; never follow client paths.
            foreach (var path in Directory.EnumerateFiles(workspace)) File.Delete(path);
            Directory.Delete(workspace);
        }
    }

    private async Task<Run> Sort(StrictCsvReader reader, ComparisonSchema schema, FileSide side, ScratchBudget disk, CancellationToken ct)
    {
        var levels = new List<List<Run>>();
        var chunk = new List<ComparisonRecord>(); long size = 0, records = 0;
        void Carry(Run run, int level)
        {
            if (levels.Count <= level) levels.Add([]);
            levels[level].Add(run);
            if (levels[level].Count < settings.MergeFanIn) return;
            var merged = Merge(levels[level], disk, ct); levels[level].Clear(); Carry(merged, level + 1);
        }
        void Flush()
        {
            if (chunk.Count == 0) return;
            chunk.Sort((a, b) => a.Key.CompareTo(b.Key));
            var path = disk.NewPath();
            using (var writer = Writer(path)) foreach (var record in chunk) Write(writer, record, disk);
            Carry(new(path, chunk.Count), 0); chunk.Clear(); size = 0;
        }
        string[]? fields;
        while ((fields = await reader.Read(ct)) != null)
        {
            var record = schema.Project(side, fields);
            // Conservative retained object estimate, independent of UTF-8 file length.
            long cost = 256 + record.Key.Components.Concat(record.Values).Sum(s => 64L + s.Length * 2L);
            if (cost > settings.SortBufferBytes) throw new DomainException("SortRecordLimit", "A projected record exceeds the sort buffer.");
            if (size + cost > settings.SortBufferBytes) Flush();
            chunk.Add(record); size += cost; records++;
        }
        Flush();
        var remaining = levels.SelectMany(x => x).ToList(); // At most (fan-in - 1) per logarithmic level.
        if (remaining.Count == 0)
        {
            var path = disk.NewPath(); using (Writer(path)) { }
            return new(path, 0);
        }
        while (remaining.Count > 1)
        {
            var group = remaining.Take(settings.MergeFanIn).ToArray();
            var merged = Merge(group, disk, ct); remaining.RemoveRange(0, group.Length); remaining.Add(merged);
        }
        return remaining[0] with { Count = records };
    }
    private Run Merge(IReadOnlyList<Run> runs, ScratchBudget disk, CancellationToken ct)
    {
        var output = disk.NewPath();
        var readers = runs.Select(run => Open(run, disk)).ToArray();
        try
        {
            var queue = new PriorityQueue<(ComparisonRecord Record, int Source), CompositeKey>();
            for (int i = 0; i < readers.Length; i++)
            { var record = Read(readers[i]); if (record != null) queue.Enqueue((record, i), record.Key); }
            using (var writer = Writer(output))
                while (queue.TryDequeue(out var current, out _))
                {
                    ct.ThrowIfCancellationRequested(); Write(writer, current.Record, disk);
                    var next = Read(readers[current.Source]); if (next != null) queue.Enqueue((next, current.Source), next.Key);
                }
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
        foreach (var run in runs) disk.Remove(run.Path);
        return new(output, runs.Sum(x => x.Count));
    }
    private static BinaryReader Open(Run run, ScratchBudget disk)
    { disk.ReadBytes += new FileInfo(run.Path).Length; return new(File.OpenRead(run.Path), Encoding.UTF8); }
    private static BinaryWriter Writer(string path) => new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536), Encoding.UTF8);
    private static void Write(BinaryWriter writer, ComparisonRecord record, ScratchBudget disk)
    {
        var before = writer.BaseStream.Position;
        // Explicit component counts plus BinaryWriter's length-prefixed UTF-8 strings prevent key collisions.
        long upperBound = 8 + record.Key.Components.Concat(record.Values).Sum(s => 5L + Encoding.UTF8.GetByteCount(s));
        disk.Ensure(upperBound);
        writer.Write(record.Key.Components.Count);
        foreach (var part in record.Key.Components) writer.Write(part);
        writer.Write(record.Values.Count);
        foreach (var value in record.Values) writer.Write(value);
        disk.Allocate(writer.BaseStream.Position - before);
    }
    private static ComparisonRecord? Read(BinaryReader reader)
    {
        if (reader.BaseStream.Position == reader.BaseStream.Length) return null;
        var key = new string[reader.ReadInt32()];
        for (int i = 0; i < key.Length; i++) key[i] = reader.ReadString();
        var values = new string[reader.ReadInt32()];
        for (int i = 0; i < values.Length; i++) values[i] = reader.ReadString();
        return new(new CompositeKey(key), values);
    }
    private sealed record Run(string Path, long Count);
    private sealed class ScratchBudget(string directory, long maximum)
    {
        private long _current;
        public long Peak { get; private set; }
        public long ReadBytes { get; set; }
        public long WrittenBytes { get; set; }
        public string NewPath() => Path.Combine(directory, Guid.NewGuid().ToString("N"));
        public void Ensure(long bytes)
        { if (bytes > maximum - _current) throw new DomainException("ScratchQuota", "The scratch disk quota is exhausted."); }
        public void Allocate(long bytes) { Ensure(bytes); _current += bytes; WrittenBytes += bytes; Peak = Math.Max(Peak, _current); }
        public void Remove(string path) { _current -= new FileInfo(path).Length; File.Delete(path); }
    }
}
