using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FileReport.Api;
using FileReport.Application.Comparisons;
using FileReport.Contracts;
using FileReport.Domain;
using FileReport.Infrastructure;
using FileReport.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 65536);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "processing.defaults.json"), false)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Services.AddProblemDetails(); builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.Converters.Add(new LongStringConverter());
});
builder.Services.AddFileReport(builder.Configuration);
var signingKey = builder.Configuration["Jwt:SigningKey"] ?? "";
if (Encoding.UTF8.GetByteCount(signingKey) < 32) throw new InvalidOperationException("Configure a JWT signing key of at least 32 random bytes.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ClockSkew = TimeSpan.FromSeconds(5),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
    };
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
    {
        if (context.Request.Path.StartsWithSegments("/hubs/comparisons") && context.Request.Query.TryGetValue("access_token", out var token))
            context.Token = token;
        return Task.CompletedTask;
    }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration["Cors:Origin"] ?? "http://localhost:4200")
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSignalR(o => { o.MaximumReceiveMessageSize = 16384; o.EnableDetailedErrors = false; })
    .AddJsonProtocol(o => { o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()); o.PayloadSerializerOptions.Converters.Add(new LongStringConverter()); });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirstValue("sub") ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (context, ct) => await Results.Problem(statusCode: 429, title: "Request limit reached.",
        extensions: new Dictionary<string, object?> { ["code"] = "RateLimit", ["traceId"] = context.HttpContext.TraceIdentifier }).ExecuteAsync(context.HttpContext);
});
if (builder.Configuration.GetValue("Dispatchers:Enabled", true))
{
    builder.Services.AddGatewayDispatchers(); builder.Services.AddHostedService<NotificationDispatcher>();
}
var app = builder.Build();
if (args.Contains("--migrate"))
{
    await using var db = await app.Services.GetRequiredService<IDbContextFactory<FileReportDbContext>>().CreateDbContextAsync();
    await db.Database.MigrateAsync(); return;
}
app.UseExceptionHandler(error => error.Run(async context =>
{
    var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (code, title, status) = ex switch
    {
        RequestException r => (r.Code, r.Message, r.Status),
        DomainException d => (d.Code, d.Message, d.Code is "RevisionConflict" or "ImmutableComparison" or "UploadInProgress" or "StaleUpload" or "ComparisonNotReady" ? 409 : 400),
        BadHttpRequestException b => ("InvalidRequest", "The request is invalid or exceeds its limit.", b.StatusCode),
        OperationCanceledException => ("RequestTimeout", "The request timed out.", 408),
        _ => ("DependencyUnavailable", "The request could not be completed. Retry with the same idempotency key.", 503)
    };
    await Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
}));
app.UseCors(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" }));
app.MapGet("/health/ready", async (IDbContextFactory<FileReportDbContext> factory, CancellationToken ct) =>
{
    try
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.CanConnectAsync(ct) && !(await db.Database.GetPendingMigrationsAsync(ct)).Any()
            ? Results.Ok(new { status = "Ready", note = "Durable submissions available; dispatch may be delayed." }) : Results.StatusCode(503);
    }
    catch (Exception) { return Results.StatusCode(503); }
});
app.MapGet("/api/v1/system", () => Results.Ok(new SystemStatusResponse("FileReport", "Implementation", true, true, true, "Workload-specific; see reports")));
app.MapOpenApi().RequireAuthorization();
app.MapPost("/api/v1/auth/register", async (CredentialsRequest body, IIdentityService identity, CancellationToken ct) =>
    Results.Json(await identity.Register(body.Email, body.Password, ct), statusCode: 201)).RequireRateLimiting("auth");
app.MapPost("/api/v1/auth/login", async (CredentialsRequest body, IIdentityService identity, CancellationToken ct) =>
    Results.Ok(await identity.Login(body.Email, body.Password, ct))).RequireRateLimiting("auth");
app.MapGet("/api/v1/auth/me", (ClaimsPrincipal user) => new { id = Transport.Owner(user), email = user.FindFirstValue("email") }).RequireAuthorization();
app.MapComparisons();
app.MapHub<JobHub>("/hubs/comparisons", o => o.CloseOnAuthenticationExpiration = true);
app.Run();
public partial class Program;
