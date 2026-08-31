using FileReport.Infrastructure;
using FileReport.Infrastructure.Messaging;
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "processing.defaults.json"), optional: false)
    .AddEnvironmentVariables().AddCommandLine(args);
builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole();
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Services.AddFileReport(builder.Configuration);
builder.Services.AddHostedService(sp => sp.GetRequiredService<ComparisonWorker>());
await builder.Build().RunAsync();
