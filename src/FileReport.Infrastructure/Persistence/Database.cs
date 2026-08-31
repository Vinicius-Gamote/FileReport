using System.Text.Json;
using System.Text.Json.Serialization;
using FileReport.Application.Comparisons;
using Microsoft.EntityFrameworkCore;

namespace FileReport.Infrastructure.Persistence;

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new InvalidDataException("Invalid persisted document.");
}
public sealed class UserRow
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
public sealed class JobRow
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string State { get; set; } = "Draft";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Document { get; set; } = "{}";
}
public sealed class OutboxRow
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTimeOffset AvailableAtUtc { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public Guid? Claim { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public int Failures { get; set; }
}
public sealed class RequestRow
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid JobId { get; set; }
    public string Operation { get; set; } = "";
    public string Key { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Result { get; set; } = "";
}
public sealed class NotificationRow
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
}
public sealed class ReceiptRow
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public string Disposition { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }
}
public sealed class EmailRow
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid JobId { get; set; }
    public string RequestKey { get; set; } = "";
    public string State { get; set; } = "Pending";
    public string Recipient { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string? ErrorCode { get; set; }
    public string? ProviderId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? FirstSendAtUtc { get; set; }
    public DateTimeOffset AvailableAtUtc { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public Guid? Claim { get; set; }
    public int Attempts { get; set; }
}
public sealed class FileReportDbContext(DbContextOptions<FileReportDbContext> options) : DbContext(options)
{
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<JobRow> Jobs => Set<JobRow>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    public DbSet<RequestRow> Requests => Set<RequestRow>();
    public DbSet<NotificationRow> Notifications => Set<NotificationRow>();
    public DbSet<ReceiptRow> Receipts => Set<ReceiptRow>();
    public DbSet<EmailRow> Emails => Set<EmailRow>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserRow>().HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Entity<UserRow>().Property(x => x.Email).HasMaxLength(254);
        b.Entity<UserRow>().Property(x => x.NormalizedEmail).HasMaxLength(254);
        b.Entity<JobRow>().HasIndex(x => new { x.OwnerId, x.Id });
        b.Entity<JobRow>().HasIndex(x => new { x.State, x.CreatedAtUtc });
        b.Entity<JobRow>().HasOne<UserRow>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<JobRow>().Property(x => x.Document).HasColumnType("jsonb");
        b.Entity<JobRow>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<OutboxRow>().HasIndex(x => new { x.PublishedAtUtc, x.AvailableAtUtc });
        b.Entity<OutboxRow>().Property(x => x.Payload).HasColumnType("jsonb");
        b.Entity<RequestRow>().HasIndex(x => new { x.OwnerId, x.Operation, x.Key }).IsUnique();
        b.Entity<RequestRow>().Property(x => x.Key).HasMaxLength(128);
        b.Entity<RequestRow>().Property(x => x.Operation).HasMaxLength(64);
        b.Entity<NotificationRow>().HasIndex(x => new { x.SentAtUtc, x.CreatedAtUtc });
        b.Entity<NotificationRow>().Property(x => x.Payload).HasColumnType("jsonb");
        b.Entity<ReceiptRow>().HasIndex(x => x.JobId);
        b.Entity<EmailRow>().HasIndex(x => new { x.OwnerId, x.RequestKey }).IsUnique();
        b.Entity<EmailRow>().HasIndex(x => new { x.State, x.AvailableAtUtc });
        b.Entity<EmailRow>().Property(x => x.Payload).HasColumnType("jsonb");
    }
}
