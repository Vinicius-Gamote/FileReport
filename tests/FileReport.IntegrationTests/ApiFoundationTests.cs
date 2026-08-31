using System.Net;
using System.Net.Http.Json;
using FileReport.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
namespace FileReport.IntegrationTests;

public sealed class ApiFoundationTests : IClassFixture<ProbeFactory>
{
    private readonly HttpClient _client;
    public ApiFoundationTests(ProbeFactory factory) => _client = factory.CreateClient();
    [Fact]
    public async Task LivenessDoesNotPretendDependenciesAreReady()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync("/health/ready")).StatusCode);
    }
    [Fact]
    public async Task SystemEndpointDisclosesImplementedCapabilities()
    {
        var status = await _client.GetFromJsonAsync<SystemStatusResponse>("/api/v1/system");
        Assert.NotNull(status); Assert.Equal("Implementation", status.Stage); Assert.True(status.CanAuthenticate);
    }
    [Fact]
    public async Task SubmissionRequiresAuthentication() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/comparisons", new { })).StatusCode);
}
public sealed class ProbeFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SigningKey", new string('x', 64));
        builder.UseSetting("Jwt:Issuer", "FileReport.Tests");
        builder.UseSetting("Jwt:Audience", "FileReport.Tests");
        builder.UseSetting("ConnectionStrings:Database", "Host=127.0.0.1;Port=1;Username=unused;Database=unused;Timeout=1");
        builder.UseSetting("Storage:Root", Path.Combine(Path.GetTempPath(), "FileReport-probe-tests"));
        builder.UseSetting("Dispatchers:Enabled", "false");
    }
}
