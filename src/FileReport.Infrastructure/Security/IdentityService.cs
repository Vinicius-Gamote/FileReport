using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using FileReport.Application.Comparisons;
using FileReport.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using IdentityResult = FileReport.Application.Comparisons.IdentityResult;

namespace FileReport.Infrastructure.Security;

public sealed class IdentityService(IDbContextFactory<FileReportDbContext> factory, IConfiguration config) : IIdentityService
{
    private readonly PasswordHasher<UserRow> _hasher = new();
    public async Task<IdentityResult> Register(string email, string password, CancellationToken ct)
    {
        email = email.Trim();
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email)
            throw new RequestException("InvalidEmail", "Enter a valid email address.");
        if (password.Length is < 12 or > 128 || !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
            throw new RequestException("InvalidPassword", "Use 12–128 characters with uppercase, lowercase, and a number.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = new UserRow { Id = Guid.NewGuid(), Email = email, NormalizedEmail = email.ToUpperInvariant(), CreatedAtUtc = DateTimeOffset.UtcNow };
        user.PasswordHash = _hasher.HashPassword(user, password);
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        { throw new RequestException("RegistrationUnavailable", "Registration could not be completed.", 409); }
        return Issue(user);
    }
    public async Task<IdentityResult> Login(string email, string password, CancellationToken ct)
    {
        if (email.Length > 254 || password.Length > 128) throw InvalidLogin();
        await using var db = await factory.CreateDbContextAsync(ct);
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        // Run the configured hasher for unknown accounts too.
        var dummy = user ?? new UserRow();
        var hash = user?.PasswordHash ?? _hasher.HashPassword(dummy, "Unused-dummy-password-193");
        var verification = _hasher.VerifyHashedPassword(dummy, hash, password);
        if (user is null || verification == PasswordVerificationResult.Failed) throw InvalidLogin();
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
            await db.SaveChangesAsync(ct);
        }
        return Issue(user);
    }
    private IdentityResult Issue(UserRow user)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        var jwt = new JwtSecurityToken(config["Jwt:Issuer"], config["Jwt:Audience"],
            [new("sub", user.Id.ToString()), new("email", user.Email), new("jti", Guid.NewGuid().ToString())],
            DateTime.UtcNow, expires.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!)), SecurityAlgorithms.HmacSha256));
        return new(user.Id, user.Email, new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
    private static RequestException InvalidLogin() => new("InvalidCredentials", "Email or password is incorrect.", 401);
}
