using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileReport.Application.Comparisons;
using FileReport.Contracts;
namespace FileReport.Api;

public static class Transport
{
    public static Guid Owner(ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue("sub"), out var id)
        ? id : throw new RequestException("Unauthenticated", "Sign in to continue.", 401);
    public static long Revision(HttpRequest request) => long.TryParse(request.Headers.IfMatch.ToString().Trim('"'),
        NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ? revision
        : throw new RequestException("PreconditionRequired", "Supply the current revision in If-Match.", 428);
    public static char Delimiter(string value) => value.Length == 1 ? value[0]
        : throw new RequestException("InvalidDelimiter", "Select comma, semicolon, or tab.");
    public static JobResponse Job(JobDocument doc, string? emailMode = null)
    {
        var s = doc.Snapshot;
        return new(s.Id, s.Revision.ToString(CultureInfo.InvariantCulture), s.State.ToString(), doc.Stage,
            doc.Files.Select(f => (object)new { f.Id, side = f.Side.ToString(), f.Name, f.Bytes, f.Sha256, f.StoredAtUtc, f.ExpiresAtUtc }).ToArray(),
            doc.ServerReceivedBytes.ToString(CultureInfo.InvariantCulture), s.CreatedAtUtc, s.SubmittedAtUtc, s.TerminalAtUtc, s.FailureCode,
            s.Attempts.Cast<object>().ToArray(), doc.Report != null,
            s.Keys == null ? null : new { s.Keys, s.Columns, s.BaselineDelimiter, s.CandidateDelimiter },
            new
            {
                uniqueInputBytes = doc.Files.Sum(f => f.Bytes),
                uploadSeconds = (doc.LastUploadAtUtc - doc.FirstUploadAtUtc)?.TotalSeconds,
                submittedTotalSeconds = (s.TerminalAtUtc - s.SubmittedAtUtc)?.TotalSeconds,
                fullWorkflowSeconds = (s.TerminalAtUtc - doc.FirstUploadAtUtc)?.TotalSeconds,
                clockNote = "UTC intervals span processes; clock offset is not measured. Full workflow includes user idle time.",
                attempts = doc.Metrics,
                memoryScope = "Worker process sampled peaks, shared with concurrent jobs. Other service scopes require external sampling.",
                availability = s.State == Domain.Comparisons.JobState.Succeeded ? "Worker attempts measured; other scopes unavailable" : "Partial or unavailable",
                cost = new
                {
                    status = "Unavailable",
                    total = (decimal?)null,
                    currency = (string?)null,
                    reason = "No provider rate card or shared allocation configured. Local execution is not assumed free."
                }
            }, emailMode);
    }
}
public sealed class LongStringConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? long.Parse(reader.GetString()!, CultureInfo.InvariantCulture) : reader.GetInt64();
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
