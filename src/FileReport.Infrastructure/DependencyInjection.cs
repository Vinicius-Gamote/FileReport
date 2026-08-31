using FileReport.Application.Abstractions;
using FileReport.Application.Comparisons;
using FileReport.Application.Configuration;
using FileReport.Application.SystemStatus;
using FileReport.Infrastructure.Email;
using FileReport.Infrastructure.Messaging;
using FileReport.Infrastructure.Persistence;
using FileReport.Infrastructure.Processing;
using FileReport.Infrastructure.Security;
using FileReport.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
namespace FileReport.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFileReport(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<ProcessingSettings>, ProcessingSettingsValidator>();
        services.AddOptions<ProcessingSettings>().Bind(configuration.GetSection(ProcessingSettings.SectionName)).ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ProcessingSettings>>().Value);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<GetSystemCapabilities>();
        services.AddDbContextFactory<FileReportDbContext>(o => o.UseNpgsql(configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("ConnectionStrings:Database is required.")));
        services.AddSingleton<IJobRepository, JobRepository>();
        services.AddSingleton<LocalFileStore>();
        services.AddSingleton<IFileStore>(sp => sp.GetRequiredService<LocalFileStore>());
        services.AddSingleton<ICsvPreview, CsvPreview>();
        services.AddSingleton<IComparisonEngine, ExternalComparisonEngine>();
        services.AddSingleton<ComparisonService>();
        services.AddSingleton<IIdentityService, IdentityService>();
        services.AddSingleton<IEmailService, EmailService>();
        services.AddSingleton<Broker>();
        services.AddSingleton<ComparisonWorker>();
        services.AddSingleton<OutboxPublisher>();
        services.AddSingleton<RecoveryService>();
        services.AddSingleton<EmailDispatcher>();
        services.AddSingleton<RetentionService>();
        services.AddHttpClient("Resend", client => client.Timeout = TimeSpan.FromSeconds(15));
        var telemetry = services.AddOpenTelemetry().WithTracing(t => t.AddSource("FileReport")).WithMetrics(m => m.AddMeter("FileReport"));
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            telemetry.WithTracing(t => t.AddOtlpExporter()).WithMetrics(m => m.AddOtlpExporter());
        return services;
    }
    public static IServiceCollection AddGatewayDispatchers(this IServiceCollection services)
    {
        services.AddHostedService(sp => sp.GetRequiredService<OutboxPublisher>());
        services.AddHostedService(sp => sp.GetRequiredService<RecoveryService>());
        services.AddHostedService(sp => sp.GetRequiredService<EmailDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
        return services;
    }
    private sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class ProcessingSettingsValidator : IValidateOptions<ProcessingSettings>
    {
        public ValidateOptionsResult Validate(string? name, ProcessingSettings options)
        {
            var errors = options.GetValidationErrors();
            return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }
    }
}
