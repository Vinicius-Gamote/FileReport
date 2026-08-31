using System.Security.Claims;
using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Contracts;
using FileReport.Domain.Comparisons;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
namespace FileReport.Api;

public static class ComparisonEndpoints
{
    public static void MapComparisons(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization().RequireRateLimiting("api");
        api.MapPost("/comparisons", async (HttpContext context, ComparisonService service, CancellationToken ct) =>
        {
            var doc = await service.Create(Transport.Owner(context.User), context.Request.Headers["Idempotency-Key"].ToString(), ct);
            return Results.Created($"/api/v1/comparisons/{doc.Snapshot.Id}", Transport.Job(doc));
        });
        api.MapGet("/comparisons", async (ClaimsPrincipal user, ComparisonService service, Guid? cursor, int? limit, CancellationToken ct) =>
        {
            var page = await service.History(Transport.Owner(user), cursor, limit ?? 20, ct);
            return Results.Ok(new { items = page.Items.Select(d => Transport.Job(d)), page.NextCursor });
        });
        api.MapGet("/comparisons/{id:guid}", async (Guid id, ClaimsPrincipal user, ComparisonService service, IConfiguration config, CancellationToken ct) =>
            Results.Ok(Transport.Job(await service.Get(Transport.Owner(user), id, ct), config["Email:Mode"])));
        api.MapPut("/comparisons/{id:guid}/files/{side}", Upload);
        api.MapGet("/comparisons/{id:guid}/schema", async (Guid id, ClaimsPrincipal user, ComparisonService service,
            string? baselineDelimiter, string? candidateDelimiter, CancellationToken ct) =>
            Results.Ok(await service.Headers(Transport.Owner(user), id, Transport.Delimiter(baselineDelimiter ?? ","), Transport.Delimiter(candidateDelimiter ?? ","), ct)));
        api.MapPatch("/comparisons/{id:guid}/options", async (Guid id, OptionsRequest body, HttpContext context, ComparisonService service, CancellationToken ct) =>
            Results.Ok(Transport.Job(await service.Options(Transport.Owner(context.User), id, Transport.Revision(context.Request),
                body.Keys, body.Columns, Transport.Delimiter(body.BaselineDelimiter), Transport.Delimiter(body.CandidateDelimiter), ct))));
        api.MapPost("/comparisons/{id:guid}/submit", async (Guid id, HttpContext context, ComparisonService service, CancellationToken ct) =>
            Results.Accepted($"/api/v1/comparisons/{id}", Transport.Job(await service.Submit(Transport.Owner(context.User), id,
                Transport.Revision(context.Request), context.Request.Headers["Idempotency-Key"].ToString(), ct))));
        api.MapGet("/comparisons/{id:guid}/report", async (Guid id, ClaimsPrincipal user, ComparisonService service, CancellationToken ct) =>
        {
            var doc = await service.Get(Transport.Owner(user), id, ct);
            if (doc.Report == null) throw new RequestException("ReportNotReady", "A complete report is not available.", 409);
            return Results.Ok(new { report = doc.Report with { Samples = [] }, measurements = Transport.Job(doc).Measurements });
        });
        api.MapGet("/comparisons/{id:guid}/samples", async (Guid id, ClaimsPrincipal user, ComparisonService service, ProcessingSettings settings, int? offset, int? limit, CancellationToken ct) =>
        {
            var doc = await service.Get(Transport.Owner(user), id, ct);
            if (doc.Report == null) throw new RequestException("ReportNotReady", "A complete report is not available.", 409);
            var start = Math.Clamp(offset ?? 0, 0, settings.MaxSampleCount); var size = Math.Clamp(limit ?? 20, 1, settings.MaxPageSize);
            return Results.Ok(new
            {
                items = doc.Report.Samples.Skip(start).Take(size),
                retainedCount = doc.Report.Samples.Length,
                doc.Report.SamplesTruncated,
                nextOffset = start + size < doc.Report.Samples.Length ? (int?)(start + size) : null
            });
        });
        api.MapGet("/comparisons/{id:guid}/artifacts/{artifactId:guid}", async (Guid id, Guid artifactId, ClaimsPrincipal user, ComparisonService service, CancellationToken ct) =>
        {
            var result = await service.Artifact(Transport.Owner(user), id, artifactId, ct);
            return Results.Stream(result.Stream, "application/x-ndjson", $"comparison-{id:N}.jsonl");
        });
        api.MapPost("/comparisons/{id:guid}/email", async (Guid id, HttpContext context, IEmailService service, CancellationToken ct) =>
        {
            var result = await service.Request(Transport.Owner(context.User), id, context.Request.Headers["Idempotency-Key"].ToString(), ct);
            return Results.Accepted($"/api/v1/email-deliveries/{result.Id}", result);
        });
        api.MapGet("/email-deliveries/{id:guid}", async (Guid id, ClaimsPrincipal user, IEmailService service, CancellationToken ct) =>
            Results.Ok(await service.Get(Transport.Owner(user), id, ct)));
    }
    private static async Task<IResult> Upload(Guid id, string side, HttpContext context,
        ComparisonService service, ProcessingSettings settings, CancellationToken ct)
    {
        var fileSide = side switch
        {
            "baseline" => FileSide.Baseline,
            "candidate" => FileSide.Candidate,
            _ => throw new RequestException("InvalidSide", "Use baseline or candidate.")
        };
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = settings.MaxFileBytes + settings.MaxMultipartHeadersBytes + 4096;
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) || contentType.MediaType != "multipart/form-data")
            throw new RequestException("MultipartRequired", "Use multipart/form-data with one file.");
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw new RequestException("InvalidMultipart", "Invalid multipart boundary.");
        var reader = new MultipartReader(boundary, context.Request.Body) { HeadersLengthLimit = settings.MaxMultipartHeadersBytes, BodyLengthLimit = settings.MaxFileBytes };
        var section = await reader.ReadNextSectionAsync(ct) ?? throw new RequestException("FileRequired", "Select a file.");
        if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.FileName.HasValue)
            throw new RequestException("FileRequired", "The section must be a file.");
        var name = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value!;
        var doc = await service.Upload(Transport.Owner(context.User), id, fileSide, Transport.Revision(context.Request), name, section.Body, ct,
            async token => { if (await reader.ReadNextSectionAsync(token) != null) throw new RequestException("SingleFileRequired", "Use one file per request."); });
        return Results.Ok(Transport.Job(doc));
    }
}
