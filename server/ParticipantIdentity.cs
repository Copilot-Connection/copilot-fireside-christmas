using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Fireside;

public sealed class ParticipantIdentity(IDataProtectionProvider provider, IWebHostEnvironment environment)
{
    private readonly IDataProtector protector = provider.CreateProtector("Fireside.Participant.v1");
    private readonly string cookieName = environment.IsDevelopment()
        ? "fireside-participant" : "__Host-fireside-participant";

    public Participant? Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(cookieName, out var value)) return null;
        try
        {
            var participant = JsonSerializer.Deserialize<Participant>(protector.Unprotect(value));
            return participant is not null && participant.Expires > DateTimeOffset.UtcNow ? participant : null;
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }

    public Participant Set(HttpContext context, string name)
    {
        var participant = new Participant(name, DateTimeOffset.UtcNow.AddDays(30));
        context.Response.Cookies.Append(cookieName, protector.Protect(JsonSerializer.Serialize(participant)),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !environment.IsDevelopment() || context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                Path = "/",
                Expires = participant.Expires
            });
        return participant;
    }
}
