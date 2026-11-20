using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Fireside;

public sealed class AudienceAccount
{
    public string Id { get; set; } = "";
    public string EventId { get; set; } = "";
    public string Kind { get; set; } = "audience-account";
    public Guid UserId { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public long Version { get; set; }
}

public sealed record AccountRecord(AudienceAccount Account, string Tag);
public sealed record AccountInput(string? Username, string? Password);

public interface IAccountStore
{
    Task<AccountRecord?> Read(string id, CancellationToken token);
    Task<bool> Create(AudienceAccount account, CancellationToken token);
    Task<bool> Replace(AudienceAccount account, string tag, CancellationToken token);
}

public sealed class AccountPasswords
{
    private readonly PasswordHasher<AudienceAccount> hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = 210_000
    }));
    private readonly AudienceAccount dummy = new();
    private readonly string dummyHash;

    public AccountPasswords() => dummyHash = hasher.HashPassword(dummy, Guid.NewGuid().ToString("N"));
    public string Hash(AudienceAccount account, string password) => hasher.HashPassword(account, password);
    public PasswordVerificationResult Verify(AudienceAccount account, string password) =>
        hasher.VerifyHashedPassword(account, account.PasswordHash, password);
    public void VerifyMissing(string password) => hasher.VerifyHashedPassword(dummy, dummyHash, password);
}

public static class AudienceAccounts
{
    public const string Scheme = "FiresideAudience";
    private const string StampClaim = "fireside:security-stamp";
    private const string Rejected = "Username or password is incorrect, or sign-in is temporarily locked. Try again later.";

    public static string? Normalize(string? username)
    {
        var value = username?.Trim();
        return value is { Length: >= 3 and <= 32 } && value.All(c =>
            c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')
            ? value.ToLowerInvariant() : null;
    }

    public static string AccountId(string normalized) =>
        "account-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    public static ClaimsIdentity? Identity(ClaimsPrincipal user) =>
        user.Identities.SingleOrDefault(i => i.IsAuthenticated && i.AuthenticationType == Scheme);

    public static Guid? VoterKey(ClaimsPrincipal user) =>
        Identity(user) is { } identity &&
        Guid.TryParse(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) && id != Guid.Empty ? id : null;

    public static async Task ValidateCookie(CookieValidatePrincipalContext context)
    {
        var identity = context.Principal is { } principal ? Identity(principal) : null;
        var normalized = Normalize(identity?.Name);
        var record = normalized is null ? null : await context.HttpContext.RequestServices
            .GetRequiredService<IAccountStore>().Read(AccountId(normalized), context.HttpContext.RequestAborted);
        if (record is not null && VoterKey(context.Principal!) == record.Account.UserId &&
            identity?.FindFirst(StampClaim)?.Value == record.Account.SecurityStamp) return;
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(Scheme);
    }

    public static void MapAudienceAccounts(this WebApplication app)
    {
        var accounts = app.MapGroup("/api/account").RequireRateLimiting("accounts");
        accounts.MapPost("/register", async (AccountInput input, HttpContext context,
            IAccountStore store, AccountPasswords passwords) =>
        {
            var normalized = Normalize(input.Username);
            if (normalized is null)
                return Results.Problem("Use a username of 3-32 letters, numbers, dots, underscores or hyphens.", statusCode: 400);
            if (input.Password is not { Length: >= 15 and <= 128 } || string.IsNullOrWhiteSpace(input.Password))
                return Results.Problem("Use a password or passphrase of 15-128 characters.", statusCode: 400);
            var account = new AudienceAccount { Id = AccountId(normalized), Username = input.Username!.Trim() };
            account.PasswordHash = passwords.Hash(account, input.Password);
            if (!await store.Create(account, context.RequestAborted))
                return Results.Problem("This username is unavailable.", statusCode: 409);
            await SignIn(context, account);
            return Results.Created("/api/session", new { username = account.Username });
        });
        accounts.MapPost("/login", async (AccountInput input, HttpContext context,
            IAccountStore store, AccountPasswords passwords, TimeProvider clock) =>
        {
            var normalized = Normalize(input.Username);
            if (normalized is null || input.Password is not { Length: > 0 and <= 128 })
                return Results.Problem(Rejected, statusCode: 401);
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var record = await store.Read(AccountId(normalized), context.RequestAborted);
                var now = clock.GetUtcNow();
                if (record is null || record.Account.LockedUntil > now)
                {
                    passwords.VerifyMissing(input.Password);
                    return Results.Problem(Rejected, statusCode: 401);
                }
                var account = record.Account;
                var result = passwords.Verify(account, input.Password);
                if (account.LockedUntil <= now) account.FailedAttempts = 0;
                account.LockedUntil = null;
                if (result == PasswordVerificationResult.Failed)
                {
                    account.FailedAttempts++;
                    if (account.FailedAttempts >= 5) account.LockedUntil = now.AddMinutes(15);
                }
                else
                {
                    account.FailedAttempts = 0;
                    if (result == PasswordVerificationResult.SuccessRehashNeeded)
                        account.PasswordHash = passwords.Hash(account, input.Password);
                }
                if (!await store.Replace(account, record.Tag, context.RequestAborted)) continue;
                if (result == PasswordVerificationResult.Failed)
                    return Results.Problem(Rejected, statusCode: 401);
                await SignIn(context, account);
                return Results.NoContent();
            }
            return Results.Problem("Account activity changed concurrently. Wait a moment and retry.", statusCode: 409);
        });
    }

    private static Task SignIn(HttpContext context, AudienceAccount account) =>
        context.SignInAsync(Scheme, new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, account.UserId.ToString("D")),
            new Claim(ClaimTypes.Name, account.Username),
            new Claim(StampClaim, account.SecurityStamp)
        ], Scheme)), new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
}
