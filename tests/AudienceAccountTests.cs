using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fireside;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fireside.Tests;

public sealed class AudienceAccountTests
{
    private const string Password = "A memorable winter passphrase!";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_registration_is_case_insensitive_and_two_real_sessions_share_only_their_own_history(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var first = await site.Client();
        using var second = await site.Client();
        var board = (await first.GetFromJsonAsync<BoardView>("/api/board"))!;
        var question = board.Questions[0];
        var historicalVoter = Guid.NewGuid();
        var historicalRequest = Guid.NewGuid();
        await using (var scope = site.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
            var written = await store.Write(null, historicalVoter, historicalRequest, data =>
            {
                data.NewVotes.Add(new Vote { Id = historicalRequest, ParticipantId = historicalVoter,
                    QuestionId = question.Id, SpeakerId = question.SpeakerId, Delta = 3 });
                data.AddScore(question.Id, question.SpeakerId, 3);
                data.PersonalPoints[question.Id] = 3;
                return null;
            }, default);
            Assert.True(written.Committed);
        }
        foreach (var client in new[] { first, second })
        {
            await Token(client);
            (await client.PutAsJsonAsync("/api/participant", new NameInput("Same public display name"))).EnsureSuccessStatusCode();
        }
        var registration = await Task.WhenAll(first.PostAsJsonAsync("/api/account/register",
            new { username = "Winter-GUEST", password = Password, userId = historicalVoter, isAdmin = true }),
            second.PostAsJsonAsync("/api/account/register", new AccountInput("winter-guest", Password)));
        Assert.Single(registration, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(registration, response => response.StatusCode == HttpStatusCode.Conflict);
        var other = registration[0].IsSuccessStatusCode ? second : first;
        await Token(other);
        (await other.PostAsJsonAsync("/api/account/login", new AccountInput("WINTER-guest", Password))).EnsureSuccessStatusCode();
        foreach (var client in new[] { first, second })
        {
            var session = await Token(client);
            Assert.True(session.GetProperty("canVote").GetBoolean());
            Assert.False(session.GetProperty("isAdmin").GetBoolean());
            Assert.Null(session.GetProperty("adminName").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/board")).StatusCode);
            var own = (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
            Assert.Equal(3, own.Score);
            Assert.Equal(0, own.MyScore);
        }
        var requestId = Guid.NewGuid();
        var votes = await Task.WhenAll(first.PostAsJsonAsync($"/api/questions/{question.Id}/vote", new ReactionInput(requestId, "like")),
            second.PostAsJsonAsync($"/api/questions/{question.Id}/vote", new ReactionInput(requestId, "like")));
        Assert.All(votes, response => Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict));
        (await first.PostAsJsonAsync($"/api/questions/{question.Id}/vote", new ReactionInput(requestId, "like"))).EnsureSuccessStatusCode();
        foreach (var client in new[] { first, second })
        {
            var own = (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
            Assert.Equal(4, own.Score);
            Assert.Equal(1, own.MyScore);
        }
        using var anonymous = await site.Client();
        var publicJson = await anonymous.GetStringAsync("/api/board");
        Assert.DoesNotContain("password", publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("winter-guest", publicJson, StringComparison.OrdinalIgnoreCase);
        await using var final = site.Services.CreateAsyncScope();
        var account = (await final.ServiceProvider.GetRequiredService<IAccountStore>()
            .Read(AudienceAccounts.AccountId("winter-guest"), default))!.Account;
        Assert.NotEqual(historicalVoter, account.UserId);
        Assert.NotEqual(Password, account.PasswordHash);
        Assert.Equal(3, (await final.ServiceProvider.GetRequiredService<IEventStore>().Read(historicalVoter, default))
            .PersonalPoints[question.Id]);
    }

    [Fact]
    public async Task Failed_logins_are_atomically_locked_across_clients_and_expire_without_revoking_existing_sessions()
    {
        await using var site = new Site();
        using var registered = await site.Client();
        await Token(registered);
        (await registered.PostAsJsonAsync("/api/account/register", new AccountInput("winter", Password))).EnsureSuccessStatusCode();
        using var attempts = await site.Client();
        await Token(attempts);
        var failures = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => attempts.PostAsJsonAsync(
            "/api/account/login", new AccountInput("WINTER", "wrong password"))));
        Assert.All(failures, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        using var fresh = await site.Client();
        await Token(fresh);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fresh.PostAsJsonAsync("/api/account/login", new AccountInput("winter", Password))).StatusCode);
        Assert.True((await Token(registered)).GetProperty("canVote").GetBoolean());
        site.Clock.Now = site.Clock.Now.AddMinutes(16);
        (await fresh.PostAsJsonAsync("/api/account/login", new AccountInput("winter", Password))).EnsureSuccessStatusCode();
        Assert.True((await Token(fresh)).GetProperty("canVote").GetBoolean());
    }

    [Fact]
    public async Task Account_inputs_csrf_and_logout_preserve_the_independent_Entra_admin_boundary()
    {
        await using var site = new Site();
        using var client = await site.Client();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/register", new AccountInput("guest", Password))).StatusCode);
        await Token(client);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/register", new AccountInput("<script>", Password))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/account/register", new AccountInput("guest", "short"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await client.PostAsJsonAsync("/api/account/register", new AccountInput("guest", new string('x', 5000)))).StatusCode);
        (await client.PostAsJsonAsync("/api/account/register", new AccountInput("guest", Password))).EnsureSuccessStatusCode();
        await using (var scope = site.Services.CreateAsyncScope())
        {
            var source = scope.ServiceProvider.GetRequiredService<FiresideDb>().Database.GetDbConnection().DataSource;
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => EventArchive.Capture(source, default));
            Assert.Contains("cannot transfer accounts", failure.Message);
        }
        Assert.False((await Token(client)).GetProperty("isAdmin").GetBoolean());
        client.DefaultRequestHeaders.Add("X-Test-Role", "Fireside.Admin");
        var both = await Token(client);
        Assert.True(both.GetProperty("canVote").GetBoolean());
        Assert.True(both.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/auth/logout", null)).StatusCode);
        var adminOnly = await Token(client);
        Assert.False(adminOnly.GetProperty("canVote").GetBoolean());
        Assert.True(adminOnly.GetProperty("isAdmin").GetBoolean());
    }

    internal static async Task<JsonElement> Token(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        return session;
    }
}
