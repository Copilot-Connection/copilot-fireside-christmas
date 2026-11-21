using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Cosmos;

namespace Fireside;

public sealed class EventUnavailableException(string message) : Exception(message);
public sealed record Versioned<T>(T Value, string ETag);
public sealed record CosmosCommit(string ETag, Guid? Voter, string? PersonalETag, EventData Data);
public sealed class PersonalPoints
{
    public string Id { get; set; } = "";
    public string EventId { get; set; } = "";
    public string Kind { get; set; } = "personal";
    public long ScoreEpoch { get; set; }
    public Dictionary<Guid, long> Points { get; set; } = [];
}
public sealed class EventDocument
{
    public string Id { get; set; } = "event";
    public string EventId { get; set; } = "";
    public string Kind { get; set; } = "event";
    public int SchemaVersion { get; set; } = 1;
    public EventData Data { get; set; } = new();
}
public sealed class LedgerDocument
{
    public string Id { get; set; } = "";
    public string EventId { get; set; } = "";
    public string Kind { get; set; } = "";
    public Vote? Vote { get; set; }
    public ScoreAdjustment? Adjustment { get; set; }
}

public interface ICosmosEventDocuments
{
    Task<Versioned<EventData>> Read(CancellationToken token);
    Task<Versioned<PersonalPoints>?> Personal(Guid voter, CancellationToken token);
    Task<Vote?> Vote(Guid request, CancellationToken token);
    Task<bool> Commit(CosmosCommit commit, CancellationToken token);
}
public interface IEventImages
{
    Task Put(string name, byte[] bytes, string contentType, CancellationToken token);
    Task<byte[]> Get(string name, CancellationToken token);
}

public sealed class BlobEventImages(BlobContainerClient container) : IEventImages
{
    public async Task Put(string name, byte[] bytes, string contentType, CancellationToken token)
    {
        await using var stream = new MemoryStream(bytes, false);
        await container.GetBlobClient(name).UploadAsync(stream,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, token);
    }

    public async Task<byte[]> Get(string name, CancellationToken token)
    {
        var result = await container.GetBlobClient(name).DownloadContentAsync(token);
        return result.Value.Content.ToArray();
    }
}

public sealed class CosmosEventStore(ICosmosEventDocuments documents, IEventImages images) : IEventStore
{
    public async Task<EventData> Read(Guid? voter, CancellationToken token)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var core = await documents.Read(token);
            if (!voter.HasValue) return core.Value;
            var personal = await documents.Personal(voter.Value, token);
            var current = await documents.Read(token);
            if (current.ETag != core.ETag) continue;
            core.Value.PersonalPoints = personal?.Value.ScoreEpoch == core.Value.Board.ScoreEpoch
                ? personal.Value.Points : [];
            return core.Value;
        }
        throw new EventUnavailableException("A coherent event snapshot could not be read during concurrent changes.");
    }

    public async Task<EventWrite> Write(long? revision, Guid? voter, Guid? request,
        Func<EventData, IResult?> change, CancellationToken token)
    {
        var operation = Guid.NewGuid().ToString("N");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var core = await documents.Read(token);
            var data = core.Value;
            if (revision.HasValue && data.Board.Revision != revision) return new(StoreResults.Conflict(), false);
            var personal = voter.HasValue ? await documents.Personal(voter.Value, token) : null;
            data.PersonalPoints = personal?.Value.ScoreEpoch == data.Board.ScoreEpoch
                ? personal.Value.Points : [];
            if (request.HasValue) data.ExistingVote = await documents.Vote(request.Value, token);
            var response = change(data);
            if (response is not null) return new(response, false);
            if (revision.HasValue) data.Board.Revision++;
            await StoreImages(data, operation, token);
            data.ClearNavigations();
            if (await documents.Commit(new(core.ETag, voter, personal?.ETag, data), token))
                return new(null, true);
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), token);
        }
        return new(StoreResults.Conflict(), false);
    }

    private async Task StoreImages(EventData data, string operation, CancellationToken token)
    {
        if (data.Board.LandingImageData is { } landing)
        {
            var name = $"landing/{operation}";
            await images.Put(name, landing, data.Board.LandingImageContentType
                ?? throw new InvalidOperationException("Landing image type is missing."), token);
            data.Images["landing"] = name;
            data.Board.LandingImageData = null;
        }
        foreach (var person in data.Panellists.Where(p => p.PhotoData is not null))
        {
            var name = $"panellists/{person.Id:N}/{operation}";
            await images.Put(name, person.PhotoData!, person.PhotoContentType
                ?? throw new InvalidOperationException("Photo type is missing."), token);
            data.Images[$"photo-{person.Id:N}"] = name;
            person.PhotoData = null;
        }
    }

    public Task<byte[]> Image(EventData data, string key, CancellationToken token) =>
        images.Get(data.Images.TryGetValue(key, out var name) ? name
            : throw new InvalidOperationException("The event references a missing image."), token);
}

public sealed class EventCosmosSerializer : CosmosSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString
    };

    public override T FromStream<T>(Stream stream)
    {
        if (typeof(T) == typeof(Stream)) return (T)(object)stream;
        using (stream)
            return JsonSerializer.Deserialize<T>(stream, Options)
                ?? throw new InvalidDataException("Cosmos returned an empty document.");
    }

    public override Stream ToStream<T>(T input) =>
        new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(input, Options), false);
}

public sealed class CosmosEventDocuments(Container container, string eventId) : ICosmosEventDocuments
{
    public async Task<Versioned<EventData>> Read(CancellationToken token)
    {
        try
        {
            var result = await container.ReadItemAsync<EventDocument>("event", new PartitionKey(eventId),
                cancellationToken: token);
            if (result.Resource.SchemaVersion != 1 || result.Resource.EventId != eventId ||
                result.Resource.Data.Board.ScoreboardInitializedAt is null)
                throw new InvalidDataException("The Cosmos event schema/import marker is invalid.");
            return new(result.Resource.Data, result.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EventUnavailableException("The Cosmos event has not been imported and published.");
        }
    }

    public async Task<Versioned<PersonalPoints>?> Personal(Guid voter, CancellationToken token)
    {
        try
        {
            var result = await container.ReadItemAsync<PersonalPoints>($"voter-{voter:N}",
                new PartitionKey(eventId), cancellationToken: token);
            return new(result.Resource, result.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<Vote?> Vote(Guid request, CancellationToken token)
    {
        try
        {
            var result = await container.ReadItemAsync<LedgerDocument>($"vote-{request:N}",
                new PartitionKey(eventId), cancellationToken: token);
            return result.Resource.Vote ?? throw new InvalidDataException("Vote ledger document has no vote.");
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<bool> Commit(CosmosCommit commit, CancellationToken token)
    {
        var batch = container.CreateTransactionalBatch(new PartitionKey(eventId))
            .ReplaceItem("event", new EventDocument { EventId = eventId, Data = commit.Data },
                new TransactionalBatchItemRequestOptions { IfMatchEtag = commit.ETag });
        foreach (var vote in commit.Data.NewVotes)
            batch.CreateItem(new LedgerDocument { Id = $"vote-{vote.Id:N}", EventId = eventId, Kind = "vote", Vote = vote });
        foreach (var adjustment in commit.Data.NewAdjustments)
            batch.CreateItem(new LedgerDocument { Id = $"adjustment-{adjustment.Id:N}",
                EventId = eventId, Kind = "adjustment", Adjustment = adjustment });
        if (commit.Voter.HasValue && commit.Data.NewVotes.Count != 0)
        {
            var personal = new PersonalPoints { Id = $"voter-{commit.Voter:N}",
                EventId = eventId, ScoreEpoch = commit.Data.Board.ScoreEpoch, Points = commit.Data.PersonalPoints };
            if (commit.PersonalETag is null) batch.CreateItem(personal);
            else batch.ReplaceItem(personal.Id, personal,
                new TransactionalBatchItemRequestOptions { IfMatchEtag = commit.PersonalETag });
        }
        using var result = await batch.ExecuteAsync(token);
        if (result.IsSuccessStatusCode) return true;
        if (result.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) return false;
        throw new CosmosException(result.ErrorMessage, result.StatusCode, 0, result.ActivityId, result.RequestCharge);
    }
}
