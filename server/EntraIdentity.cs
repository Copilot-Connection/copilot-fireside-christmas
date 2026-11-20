using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Fireside;

public sealed class EntraIdentity(Guid adminTenant)
{
    public const string DemoTenant = "11111111-1111-4111-8111-111111111111";
    public const string DemoVoterTenant = "22222222-2222-4222-8222-222222222222";
    public const string DemoHostObject = "33333333-3333-4333-8333-333333333333";
    public const string DemoVoterObject = "44444444-4444-4444-8444-444444444444";
    private static readonly Guid ConsumerTenant = new("9188040d-6c67-4c5b-b112-36a304b66dad");

    public bool IsAdmin(ClaimsPrincipal user) => VoterKey(user) is not null &&
        Guid.TryParse(user.FindFirst("tid")?.Value, out var tenant) && tenant == adminTenant &&
        user.IsInRole("Fireside.Admin");

    public static Guid? VoterKey(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var tenants = user.FindAll("tid").ToArray();
        var objects = user.FindAll("oid").ToArray();
        if (tenants.Length != 1 || objects.Length != 1 ||
            !Guid.TryParse(tenants[0].Value, out var tenant) || tenant == Guid.Empty || tenant == ConsumerTenant ||
            !Guid.TryParse(objects[0].Value, out var person) || person == Guid.Empty)
            return null;
        // A stable pseudonymous key deduplicates the same Entra identity across devices and cookie resets.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"fireside-voter-v1:{tenant:D}:{person:D}"));
        return new Guid(digest.AsSpan(0, 16));
    }
}
