using System.Globalization;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fireside;

public sealed class SqliteAccountStore(FiresideDb db) : IAccountStore
{
    public async Task<AccountRecord?> Read(string id, CancellationToken token)
    {
        var account = await db.AudienceAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, token);
        return account is null ? null : new(account, account.Version.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<bool> Create(AudienceAccount account, CancellationToken token)
    {
        db.AudienceAccounts.Add(account);
        try { await db.SaveChangesAsync(token); return true; }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
        {
            db.Entry(account).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> Replace(AudienceAccount account, string tag, CancellationToken token)
    {
        var version = long.Parse(tag, CultureInfo.InvariantCulture);
        return await db.AudienceAccounts.Where(a => a.Id == account.Id && a.Version == version)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.PasswordHash, account.PasswordHash)
                .SetProperty(a => a.FailedAttempts, account.FailedAttempts)
                .SetProperty(a => a.LockedUntil, account.LockedUntil)
                .SetProperty(a => a.Version, version + 1), token) == 1;
    }
}

public sealed class CosmosAccountStore : IAccountStore
{
    private readonly ICosmosAccountContainer items;
    private readonly string eventId;

    public CosmosAccountStore(Container container, string eventId) : this(new CosmosAccountContainer(container), eventId) { }

    public CosmosAccountStore(ICosmosAccountContainer items, string eventId)
    {
        this.items = items;
        this.eventId = eventId;
    }

    public async Task<AccountRecord?> Read(string id, CancellationToken token)
    {
        try
        {
            return await items.Read(id, new PartitionKey(eventId), token);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<bool> Create(AudienceAccount account, CancellationToken token)
    {
        account.EventId = eventId;
        try { await items.Create(account, new PartitionKey(eventId), token); return true; }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict) { return false; }
    }

    public async Task<bool> Replace(AudienceAccount account, string tag, CancellationToken token)
    {
        try
        {
            await items.Replace(account, account.Id, new PartitionKey(eventId), tag, token);
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed) { return false; }
    }
}

public interface ICosmosAccountContainer
{
    Task<AccountRecord?> Read(string id, PartitionKey partition, CancellationToken token);
    Task Create(AudienceAccount account, PartitionKey partition, CancellationToken token);
    Task Replace(AudienceAccount account, string id, PartitionKey partition, string etag, CancellationToken token);
}

internal sealed class CosmosAccountContainer(Container container) : ICosmosAccountContainer
{
    public async Task<AccountRecord?> Read(string id, PartitionKey partition, CancellationToken token)
    {
        var response = await container.ReadItemAsync<AudienceAccount>(id, partition, cancellationToken: token);
        return new(response.Resource, response.ETag);
    }

    public async Task Create(AudienceAccount account, PartitionKey partition, CancellationToken token) =>
        _ = await container.CreateItemAsync(account, partition, cancellationToken: token);

    public async Task Replace(AudienceAccount account, string id, PartitionKey partition, string etag, CancellationToken token) =>
        _ = await container.ReplaceItemAsync(account, id, partition, new ItemRequestOptions { IfMatchEtag = etag }, token);
}
