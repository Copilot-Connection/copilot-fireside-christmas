using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fireside;

public sealed record TransferImage(string Name, string ContentType, byte[] Bytes);

public sealed class EventArchive(EventData data, IReadOnlyList<Vote> votes, IReadOnlyList<ScoreAdjustment> adjustments)
{
    public EventData Data { get; } = data;
    public IReadOnlyList<Vote> Votes { get; } = votes;
    public IReadOnlyList<ScoreAdjustment> Adjustments { get; } = adjustments;
    public List<TransferImage> Images { get; } = [];

    public static async Task<EventArchive> Capture(string filename, CancellationToken token)
    {
        var path = Path.GetFullPath(filename);
        if (!File.Exists(path)) throw new FileNotFoundException("The source event database does not exist.", path);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        await using var db = new FiresideDb(new DbContextOptionsBuilder<FiresideDb>().UseSqlite(connection).Options);
        await db.Database.UseTransactionAsync(transaction, token);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA integrity_check";
            if ((string?)await command.ExecuteScalarAsync(token) != "ok")
                throw new InvalidDataException("SQLite integrity check failed; the event was not imported.");
            command.CommandText = "PRAGMA foreign_key_check";
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
                throw new InvalidDataException("SQLite foreign key check failed; the event was not imported.");
        }
        var state = await new SqliteEventStore(db).Load(null, false, token);
        state.ClearNavigations();
        var sourceVotes = await db.Votes.AsNoTracking().ToListAsync(token);
        var sourceAdjustments = await db.ScoreAdjustments.AsNoTracking().ToListAsync(token);
        foreach (var vote in sourceVotes) vote.Question = null!;
        foreach (var adjustment in sourceAdjustments) adjustment.Question = null!;
        var archive = new EventArchive(state, sourceVotes, sourceAdjustments);
        archive.Prepare();
        await transaction.CommitAsync(token);
        return archive;
    }

    public void Prepare()
    {
        if (Data.Board.ScoreboardInitializedAt is null)
            throw new InvalidDataException("Upgrade the source scoreboard schema before import; this importer never resets points.");
        if (Votes.Any(v => v.Delta is not (-1 or 1 or 3) || !Data.Questions.Any(q => q.Id == v.QuestionId)) ||
            Adjustments.Any(a => a.Delta == 0 || !Data.Questions.Any(q => q.Id == a.QuestionId)))
            throw new InvalidDataException("The source ledger contains invalid or orphaned records.");
        var totals = new EventData();
        foreach (var vote in Votes) totals.AddScore(vote.QuestionId, vote.SpeakerId, vote.Delta);
        foreach (var adjustment in Adjustments) totals.AddScore(adjustment.QuestionId, adjustment.SpeakerId, adjustment.Delta);
        static IEnumerable<EventScore> Ordered(IEnumerable<EventScore> values) =>
            values.OrderBy(s => s.QuestionId).ThenBy(s => s.SpeakerId);
        if (!Ordered(totals.Scores).SequenceEqual(Ordered(Data.Scores)))
            throw new InvalidDataException("The source projections disagree with the immutable vote/adjustment ledger.");
        if (Data.Board.LandingImageRevision.HasValue)
            Extract("landing", Data.Board.LandingImageData, Data.Board.LandingImageContentType);
        else if (Data.Board.LandingImageData is not null)
            throw new InvalidDataException("Landing image metadata is inconsistent.");
        Data.Board.LandingImageData = null;
        foreach (var person in Data.Panellists)
        {
            if (person.PhotoRevision.HasValue) Extract($"photo-{person.Id:N}", person.PhotoData, person.PhotoContentType);
            else if (person.PhotoData is not null) throw new InvalidDataException("Panellist image metadata is inconsistent.");
            person.PhotoData = null;
        }
    }

    private void Extract(string key, byte[]? bytes, string? contentType)
    {
        if (bytes is null || contentType is not ("image/png" or "image/jpeg"))
            throw new InvalidDataException("The source references missing or unsupported image data.");
        var name = $"import/{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
        Images.Add(new(name, contentType, bytes));
        Data.Images.Add(key, name);
    }

    public IReadOnlyList<LedgerDocument> Ledgers(string eventId) =>
        Votes.Select(v => new LedgerDocument { Id = $"vote-{v.Id:N}", EventId = eventId, Kind = "vote", Vote = v })
            .Concat(Adjustments.Select(a => new LedgerDocument { Id = $"adjustment-{a.Id:N}",
                EventId = eventId, Kind = "adjustment", Adjustment = a })).ToArray();

    public IReadOnlyList<PersonalPoints> Personal(string eventId) => Votes.GroupBy(v => v.ParticipantId)
        .Select(person => new PersonalPoints { Id = $"voter-{person.Key:N}", EventId = eventId,
            Points = person.GroupBy(v => v.QuestionId).ToDictionary(g => g.Key, g => g.Sum(v => (long)v.Delta)) }).ToArray();

    public static string Digest<T>(T value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            Canonical(JsonSerializer.SerializeToElement(value, EventCosmosSerializer.Options), writer);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void Canonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Canonical(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) Canonical(item, writer);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}

public sealed class CosmosEventTransfer(Container container, string eventId, IEventImages images)
{
    public async Task Import(EventArchive archive, CancellationToken token)
    {
        if ((await Documents(token)).Count != 0)
            throw new InvalidOperationException("The target event partition is not empty. Refusing to overwrite or resume an unverified import.");
        foreach (var image in archive.Images)
        {
            await images.Put(image.Name, image.Bytes, image.ContentType, token);
            await VerifyImage(image, token);
        }
        foreach (var document in archive.Ledgers(eventId))
            await container.CreateItemAsync(document, new PartitionKey(eventId), cancellationToken: token);
        foreach (var document in archive.Personal(eventId))
            await container.CreateItemAsync(document, new PartitionKey(eventId), cancellationToken: token);
        await VerifyDocuments(archive, false, token);
        // The app cannot serve an event until its final, verified core document exists.
        await container.CreateItemAsync(new EventDocument { EventId = eventId, Data = archive.Data },
            new PartitionKey(eventId), cancellationToken: token);
        await Verify(archive, token);
    }

    public async Task Verify(EventArchive archive, CancellationToken token)
    {
        await VerifyDocuments(archive, true, token);
        foreach (var image in archive.Images) await VerifyImage(image, token);
    }

    private async Task VerifyImage(TransferImage image, CancellationToken token)
    {
        var stored = await images.Get(image.Name, token);
        if (!SHA256.HashData(image.Bytes).AsSpan().SequenceEqual(SHA256.HashData(stored)))
            throw new InvalidDataException("An uploaded image hash differs from its source; do not cut over.");
    }

    private async Task<List<JsonElement>> Documents(CancellationToken token)
    {
        var documents = new List<JsonElement>();
        using var query = container.GetItemQueryIterator<JsonElement>("SELECT * FROM c",
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(eventId) });
        while (query.HasMoreResults) documents.AddRange(await query.ReadNextAsync(token));
        return documents;
    }

    private async Task VerifyDocuments(EventArchive archive, bool published, CancellationToken token)
    {
        var actual = await Documents(token);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in archive.Ledgers(eventId)) expected.Add(document.Id, EventArchive.Digest(document));
        foreach (var document in archive.Personal(eventId)) expected.Add(document.Id, EventArchive.Digest(document));
        if (published)
            expected.Add("event", EventArchive.Digest(new EventDocument { EventId = eventId, Data = archive.Data }));
        if (actual.Count != expected.Count) throw new InvalidDataException("Imported document counts differ from the source.");
        foreach (var document in actual)
        {
            var id = document.GetProperty("id").GetString() ?? throw new InvalidDataException("Imported document has no id.");
            var kind = document.GetProperty("kind").GetString();
            var digest = kind switch
            {
                "event" => EventArchive.Digest(document.Deserialize<EventDocument>(EventCosmosSerializer.Options)),
                "personal" => EventArchive.Digest(document.Deserialize<PersonalPoints>(EventCosmosSerializer.Options)),
                "vote" or "adjustment" => EventArchive.Digest(document.Deserialize<LedgerDocument>(EventCosmosSerializer.Options)),
                _ => throw new InvalidDataException("An unexpected document was found in the target partition.")
            };
            if (!expected.Remove(id, out var sourceDigest) || sourceDigest != digest)
                throw new InvalidDataException("Imported metadata, ledger, attribution or personal totals differ from the source.");
        }
        if (expected.Count != 0) throw new InvalidDataException("Source documents are missing from the target.");
    }
}
