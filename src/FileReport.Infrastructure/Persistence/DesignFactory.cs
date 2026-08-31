using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace FileReport.Infrastructure.Persistence;

public sealed class DesignFactory : IDesignTimeDbContextFactory<FileReportDbContext>
{
    public FileReportDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<FileReportDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Database") ??
            "Host=localhost;Database=filereport_design;Username=design").Options);
}
