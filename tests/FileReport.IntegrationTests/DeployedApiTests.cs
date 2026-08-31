using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace FileReport.IntegrationTests;

public sealed class DeployedApiFactAttribute : FactAttribute
{
    public DeployedApiFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FILEREPORT_SMOKE_BASE_URL")))
            Skip = "Set FILEREPORT_SMOKE_BASE_URL to an isolated running FileReport stack with fake email.";
    }
}

public sealed class DeployedApiTests(ITestOutputHelper output)
{
    [DeployedApiFact]
    public async Task ComparisonCompletesThroughHttpWithOwnerIsolationAndExplicitFakeEmail()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("FILEREPORT_SMOKE_BASE_URL")!),
            Timeout = TimeSpan.FromSeconds(15)
        };
        await WaitUntilReady(client, ct);
        var identity = await Register(client, ct);
        var token = identity.GetProperty("token").GetString()!;
        var job = await Send(client, HttpMethod.Post, "/api/v1/comparisons", token, ct,
            JsonContent.Create(new { }), idempotency: Guid.NewGuid().ToString());
        // Fail before requesting email if the target is not an isolated fake-provider environment.
        Assert.Equal("Fake", job.GetProperty("emailMode").GetString());
        var jobPath = $"/api/v1/comparisons/{job.GetProperty("id").GetString()}";

        foreach (var (side, csv) in new[]
        {
            ("baseline", "id,value\n1,same\n2,old\n3,removed\n"),
            ("candidate", "value,id\nadded,4\nnew,2\nsame,1\n")
        })
        {
            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(csv, Encoding.UTF8, "text/csv"), "file", side + ".csv");
            job = await Send(client, HttpMethod.Put, jobPath + "/files/" + side, token, ct,
                multipart, job.GetProperty("revision").GetString());
        }

        job = await Send(client, HttpMethod.Patch, jobPath + "/options", token, ct,
            JsonContent.Create(new { keys = new[] { "id" }, columns = (string[]?)null, baselineDelimiter = ",", candidateDelimiter = "," }),
            job.GetProperty("revision").GetString());
        job = await Send(client, HttpMethod.Post, jobPath + "/submit", token, ct,
            JsonContent.Create(new { }), job.GetProperty("revision").GetString(), Guid.NewGuid().ToString());
        while (job.GetProperty("state").GetString() is not ("Succeeded" or "Failed"))
        {
            await Task.Delay(500, ct);
            job = await Send(client, HttpMethod.Get, jobPath, token, ct);
        }
        output.WriteLine("Final job: " + job.GetRawText());
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());
        var report = await Send(client, HttpMethod.Get, jobPath + "/report", token, ct);
        output.WriteLine("Report: " + report.GetRawText());
        var result = report.GetProperty("report");
        foreach (var kind in new[] { "added", "removed", "changed", "unchanged" })
            Assert.Equal("1", result.GetProperty("counts").GetProperty(kind).GetString());

        using var download = new HttpRequestMessage(HttpMethod.Get,
            jobPath + "/artifacts/" + result.GetProperty("artifact").GetProperty("id").GetString());
        download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var response = await client.SendAsync(download, ct))
        {
            response.EnsureSuccessStatusCode();
            var lines = (await response.Content.ReadAsStringAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
        }

        var other = await Register(client, ct);
        using var denied = new HttpRequestMessage(HttpMethod.Get, jobPath + "/report");
        denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.GetProperty("token").GetString());
        using (var response = await client.SendAsync(denied, ct)) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var emailKey = Guid.NewGuid().ToString();
        var email = await Send(client, HttpMethod.Post, jobPath + "/email", token, ct, idempotency: emailKey);
        var duplicate = await Send(client, HttpMethod.Post, jobPath + "/email", token, ct, idempotency: emailKey);
        Assert.Equal(email.GetProperty("id").GetString(), duplicate.GetProperty("id").GetString());
    }

    private static async Task WaitUntilReady(HttpClient client, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/health/ready", ct);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(500, ct);
        }
    }

    private static Task<JsonElement> Register(HttpClient client, CancellationToken ct) =>
        Send(client, HttpMethod.Post, "/api/v1/auth/register", null, ct,
            JsonContent.Create(new { email = $"smoke-{Guid.NewGuid():N}@example.test", password = "SyntheticTestPassword12" }));

    private static async Task<JsonElement> Send(HttpClient client, HttpMethod method, string path, string? token,
        CancellationToken ct, HttpContent? content = null, string? revision = null, string? idempotency = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (revision is not null) request.Headers.IfMatch.Add(new EntityTagHeaderValue('"' + revision + '"'));
        if (idempotency is not null) request.Headers.Add("Idempotency-Key", idempotency);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.Clone();
    }
}
