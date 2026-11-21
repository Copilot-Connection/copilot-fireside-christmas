using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fireside;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fireside.Tests;

public sealed class BrowserOriginTests
{
    [Fact]
    public async Task Api_requires_exact_origin_or_same_origin_referer_and_keeps_csrf()
    {
        await using var site = new Site();
        using var client = await site.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/board")).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/board")).StatusCode);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://localhost/page");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/board")).StatusCode);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://localhost.evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/board")).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://localhost");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/session")).StatusCode);
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/participant", new NameInput("Guest"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/board")).StatusCode);
    }

    [Fact]
    public async Task Existing_name_cookie_is_upgraded_to_a_stable_voter_session()
    {
        await using var site = new Site();
        using var client = await site.Client();
        var protector = site.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Fireside.Participant.v1");
        var oldCookie = protector.Protect(JsonSerializer.Serialize(new { Name = "Existing guest", Expires = DateTimeOffset.UtcNow.AddDays(1) }));
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-fireside-participant={oldCookie}");
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("Existing guest", session.GetProperty("participantName").GetString());
        Assert.True(session.GetProperty("canVote").GetBoolean());
    }
}
