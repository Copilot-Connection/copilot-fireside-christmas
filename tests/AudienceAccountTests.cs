using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fireside;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fireside.Tests;

public sealed class AudienceAccountTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_sessions_keep_separate_history_and_do_not_change_existing_accounts(bool cosmos)
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
        foreach (var client in new[] { first, second })
        {
            var session = await Token(client);
            Assert.True(session.GetProperty("canVote").GetBoolean());
            Assert.False(session.GetProperty("isAdmin").GetBoolean());
            Assert.Null(session.GetProperty("adminName").GetString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/board")).StatusCode);
            var own = (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
            Assert.Equal(3, own.Score);
            Assert.Equal(0, own.MyScore);
        }
        (await first.PostAsJsonAsync($"/api/questions/{question.Id}/vote", new ReactionInput(Guid.NewGuid(), "like"))).EnsureSuccessStatusCode();
        (await second.PostAsJsonAsync($"/api/questions/{question.Id}/vote", new ReactionInput(Guid.NewGuid(), "epic"))).EnsureSuccessStatusCode();
        Assert.Equal(1, (await first.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].MyScore);
        Assert.Equal(3, (await second.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].MyScore);
        (await first.PutAsJsonAsync("/api/participant", new NameInput("Changed name"))).EnsureSuccessStatusCode();
        Assert.Equal(1, (await first.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].MyScore);
        using var anonymous = await site.Client();
        var publicJson = await anonymous.GetStringAsync("/api/board");
        Assert.DoesNotContain("password", publicJson, StringComparison.OrdinalIgnoreCase);
        await using var final = site.Services.CreateAsyncScope();
        Assert.Equal(3, (await final.ServiceProvider.GetRequiredService<IEventStore>().Read(historicalVoter, default))
            .PersonalPoints[question.Id]);
    }

    [Fact]
    public async Task Cosmos_account_store_uses_the_event_partition_and_rejects_duplicate_and_stale_writes()
    {
        const string eventId = "test-event";
        var items = new MemoryCosmosAccountContainer(eventId);
        var store = new CosmosAccountStore(items, eventId);
        var account = new AudienceAccount { Id = AudienceAccounts.AccountId("winter"), Username = "winter" };

        Assert.True(await store.Create(account, default));
        Assert.False(await store.Create(new AudienceAccount { Id = account.Id }, default));
        var first = (await store.Read(account.Id, default))!;
        var second = (await store.Read(account.Id, default))!;
        second.Account.FailedAttempts = 2;
        Assert.True(await store.Replace(second.Account, second.Tag, default));
        first.Account.FailedAttempts = 1;
        Assert.False(await store.Replace(first.Account, first.Tag, default));
        Assert.Equal(2, (await store.Read(account.Id, default))!.Account.FailedAttempts);
    }

    [Fact]
    public async Task Legacy_account_records_remain_untouched_and_account_routes_are_disabled()
    {
        await using var site = new Site();
        using var client = await site.Client();
        var account = new AudienceAccount { Id = AudienceAccounts.AccountId("guest"), Username = "guest", PasswordHash = "preserved-hash" };
        await using (var scope = site.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<IAccountStore>().Create(account, default));
        await Token(client);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/account/register", new AccountInput("guest", "password"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/account/login", new AccountInput("guest", "password"))).StatusCode);
        await using (var scope = site.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IAccountStore>().Read(account.Id, default);
            Assert.Equal("preserved-hash", stored!.Account.PasswordHash);
            var source = scope.ServiceProvider.GetRequiredService<FiresideDb>().Database.GetDbConnection().DataSource;
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => EventArchive.Capture(source, default));
            Assert.Contains("cannot transfer accounts", failure.Message);
        }
    }

    internal static async Task<JsonElement> Token(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        return session;
    }
}

public sealed class MemoryCosmosAccountContainer : ICosmosAccountContainer
{
    public const string TestEventId = "test-event";
    private readonly object gate = new();
    private readonly Dictionary<string, (AudienceAccount Account, long Version)> accounts = [];
    private readonly string eventId;

    public MemoryCosmosAccountContainer() : this(TestEventId) { }

    public MemoryCosmosAccountContainer(string eventId) => this.eventId = eventId;

    public Task<AccountRecord?> Read(string id, PartitionKey partition, CancellationToken token)
    {
        CheckPartition(partition);
        lock (gate)
        {
            if (!accounts.TryGetValue(id, out var stored)) return Task.FromResult<AccountRecord?>(null);
            return Task.FromResult<AccountRecord?>(new(Copy(stored.Account), stored.Version.ToString()));
        }
    }

    public Task Create(AudienceAccount account, PartitionKey partition, CancellationToken token)
    {
        CheckPartition(partition);
        lock (gate)
        {
            if (accounts.ContainsKey(account.Id)) throw Error(HttpStatusCode.Conflict);
            if (account.EventId != eventId) throw new InvalidOperationException("Account event partition does not match.");
            accounts.Add(account.Id, (Copy(account), 1));
        }
        return Task.CompletedTask;
    }

    public Task Replace(AudienceAccount account, string id, PartitionKey partition, string etag, CancellationToken token)
    {
        CheckPartition(partition);
        lock (gate)
        {
            if (id != account.Id || !accounts.TryGetValue(id, out var stored) ||
                stored.Version.ToString() != etag)
                throw Error(HttpStatusCode.PreconditionFailed);
            accounts[id] = (Copy(account), stored.Version + 1);
        }
        return Task.CompletedTask;
    }

    private void CheckPartition(PartitionKey partition)
    {
        if (partition != new PartitionKey(eventId))
            throw new InvalidOperationException("Unexpected account partition key.");
    }

    private static CosmosException Error(HttpStatusCode status) =>
        new("Simulated Cosmos conflict.", status, (int)status, "test", 0);

    private static AudienceAccount Copy(AudienceAccount account) => new()
    {
        Id = account.Id,
        EventId = account.EventId,
        Kind = account.Kind,
        UserId = account.UserId,
        Username = account.Username,
        PasswordHash = account.PasswordHash,
        SecurityStamp = account.SecurityStamp,
        FailedAttempts = account.FailedAttempts,
        LockedUntil = account.LockedUntil,
        Version = account.Version
    };
}
