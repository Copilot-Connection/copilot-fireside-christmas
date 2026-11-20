using System.Globalization;
using System.Net;
using System.Text.Json;
using Fireside;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fireside.Tests;

public sealed class CosmosStoreTests
{
    [Fact]
    public async Task A_vote_racing_a_host_change_rechecks_the_current_state_without_partial_points()
    {
        var documents = new MemoryCosmosDocuments();
        documents.Seed(new EventData { Board = new Board { VotingOpen = true } });
        var store = new CosmosEventStore(documents, new MemoryEventImages());
        var question = Guid.NewGuid();
        var voter = Guid.NewGuid();
        documents.BeforeCommit = async () =>
        {
            var edit = await store.Write(0, null, null, data => { data.Board.GroupDiscussion = true; return null; }, default);
            Assert.True(edit.Committed);
        };
        var result = await store.Write(null, voter, Guid.NewGuid(), data =>
        {
            if (data.Board.GroupDiscussion) return Results.Conflict();
            data.NewVotes.Add(new Vote { Id = Guid.NewGuid(), ParticipantId = voter, QuestionId = question, Delta = 3 });
            data.AddScore(question, null, 3);
            data.PersonalPoints[question] = 3;
            return null;
        }, default);
        Assert.False(result.Committed);
        Assert.Equal(409, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result.Response).StatusCode);
        var final = await store.Read(voter, default);
        Assert.True(final.Board.GroupDiscussion);
        Assert.Empty(final.Scores);
        Assert.Empty(final.PersonalPoints);
        Assert.Empty(documents.Votes);
    }

    [Fact]
    public async Task Votes_do_not_invalidate_host_revisions_but_host_edits_do_and_stale_edits_do_not_run()
    {
        var documents = new MemoryCosmosDocuments();
        documents.Seed(new());
        var store = new CosmosEventStore(documents, new MemoryEventImages());
        documents.BeforeCommit = async () =>
        {
            var vote = await store.Write(null, Guid.NewGuid(), Guid.NewGuid(), data =>
            { data.AddScore(Guid.NewGuid(), null, 1); return null; }, default);
            Assert.True(vote.Committed);
        };
        var host = await store.Write(0, null, null, data => { data.Board.Title = "Saved after vote"; return null; }, default);
        Assert.True(host.Committed);
        var state = await store.Read(null, default);
        Assert.Equal("Saved after vote", state.Board.Title);
        Assert.Equal(1, state.Board.Revision);
        Assert.Equal(1, Assert.Single(state.Scores).Points);
        var called = false;
        var stale = await store.Write(0, null, null, _ => { called = true; return null; }, default);
        Assert.False(stale.Committed);
        Assert.False(called);
    }

    [Fact]
    public async Task Personal_scores_and_core_are_reread_if_another_instance_commits_between_reads()
    {
        var documents = new MemoryCosmosDocuments();
        documents.Seed(new());
        var store = new CosmosEventStore(documents, new MemoryEventImages());
        var voter = Guid.NewGuid();
        var question = Guid.NewGuid();
        documents.AfterPersonal = async () =>
        {
            await store.Write(null, voter, Guid.NewGuid(), data =>
            {
                data.NewVotes.Add(new Vote { Id = Guid.NewGuid(), QuestionId = question, ParticipantId = voter, Delta = -1 });
                data.AddScore(question, null, -1);
                data.PersonalPoints[question] = -1;
                return null;
            }, default);
        };
        var state = await store.Read(voter, default);
        Assert.Equal(-1, Assert.Single(state.Scores).Points);
        Assert.Equal(-1, state.PersonalPoints[question]);
    }

    [Fact]
    public async Task Blob_failure_does_not_publish_metadata_and_success_keeps_large_bytes_out_of_cosmos()
    {
        var documents = new MemoryCosmosDocuments();
        documents.Seed(new());
        var images = new MemoryEventImages { FailUploads = true };
        var store = new CosmosEventStore(documents, images);
        var bytes = new byte[ImageUploads.MaximumBytes];
        IResult? Upload(EventData data)
        {
            data.Board.LandingImageData = bytes;
            data.Board.LandingImageContentType = "image/png";
            data.Board.LandingImageRevision = data.Board.Revision + 1;
            return null;
        }
        await Assert.ThrowsAsync<IOException>(() => store.Write(0, null, null, Upload, default));
        Assert.Null((await store.Read(null, default)).Board.LandingImageRevision);
        images.FailUploads = false;
        Assert.True((await store.Write(0, null, null, Upload, default)).Committed);
        var state = await store.Read(null, default);
        Assert.Null(state.Board.LandingImageData);
        Assert.Single(state.Images);
        Assert.Equal(bytes, await store.Image(state, "landing", default));
    }

    [Fact]
    public async Task Readonly_source_export_preserves_post_reset_points_images_settings_and_attribution()
    {
        await using var site = new Site();
        using var client = await site.Client();
        string path;
        Guid voter = Guid.NewGuid();
        Guid speaker;
        Guid question;
        byte[] image = [1, 2, 3, 4];
        await using (var scope = site.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            var board = await db.Boards.SingleAsync();
            board.Title = "Preserve the live event";
            board.Revision = 42;
            var person = await db.Panellists.FirstAsync();
            speaker = person.Id;
            person.PhotoData = image;
            person.PhotoContentType = "image/png";
            person.PhotoRevision = 42;
            question = (await db.Questions.FirstAsync()).Id;
            db.Votes.Add(new Vote { Id = Guid.NewGuid(), QuestionId = question, SpeakerId = speaker, ParticipantId = voter, Delta = 3 });
            db.ScoreAdjustments.Add(new ScoreAdjustment { QuestionId = question, SpeakerId = speaker, Delta = -1,
                ActorId = voter, Reason = "Correction", ChangedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            path = db.Database.GetDbConnection().DataSource;
        }
        var archive = await EventArchive.Capture(path, default);
        Assert.Equal(42, archive.Data.Board.Revision);
        Assert.Equal("Preserve the live event", archive.Data.Board.Title);
        Assert.Equal(2, Assert.Single(archive.Data.Scores).Points);
        Assert.Equal(speaker, Assert.Single(archive.Votes).SpeakerId);
        Assert.Equal(-1, Assert.Single(archive.Adjustments).Delta);
        Assert.Equal(3, Assert.Single(archive.Personal("test")).Points[question]);
        Assert.Equal(image, Assert.Single(archive.Images).Bytes);
        Assert.Null(archive.Data.Panellists.Single(p => p.Id == speaker).PhotoData);
        var again = await EventArchive.Capture(path, default);
        Assert.Equal(EventArchive.Digest(archive.Data), EventArchive.Digest(again.Data));
        Assert.Single(again.Votes);
        Assert.Equal(image, Assert.Single(again.Images).Bytes);
    }

    [Fact]
    public void Cosmos_serialization_preserves_large_integer_values_and_excludes_request_state()
    {
        var data = new EventData { Board = new Board { Revision = long.MaxValue },
            PersonalPoints = new() { [Guid.NewGuid()] = 123 } };
        data.AddScore(Guid.NewGuid(), null, long.MinValue);
        data.NewVotes.Add(new Vote { Delta = 3 });
        var serializer = new EventCosmosSerializer();
        using var stream = serializer.ToStream(new EventDocument { EventId = "test", Data = data });
        var restored = serializer.FromStream<EventDocument>(stream);
        Assert.Equal(long.MaxValue, restored.Data.Board.Revision);
        Assert.Equal(long.MinValue, Assert.Single(restored.Data.Scores).Points);
        Assert.Empty(restored.Data.PersonalPoints);
        Assert.Empty(restored.Data.NewVotes);
    }
}

public sealed class MemoryEventImages : IEventImages
{
    private readonly Dictionary<string, byte[]> images = [];
    public bool FailUploads { get; set; }
    public Task Put(string name, byte[] bytes, string contentType, CancellationToken token)
    {
        if (FailUploads) throw new IOException("Simulated storage outage.");
        lock (images) images[name] = bytes.ToArray();
        return Task.CompletedTask;
    }
    public Task<byte[]> Get(string name, CancellationToken token)
    {
        lock (images) return Task.FromResult(images[name].ToArray());
    }
}

public sealed class MemoryCosmosDocuments : ICosmosEventDocuments
{
    private readonly object gate = new();
    private EventData data = new();
    private long version;
    private readonly Dictionary<Guid, Versioned<PersonalPoints>> personal = [];
    public Dictionary<Guid, Vote> Votes { get; } = [];
    public List<ScoreAdjustment> Adjustments { get; } = [];
    public Func<Task>? BeforeCommit { get; set; }
    public Func<Task>? AfterPersonal { get; set; }
    private string ETag => version.ToString(CultureInfo.InvariantCulture);

    private static T Copy<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, EventCosmosSerializer.Options),
            EventCosmosSerializer.Options)!;
    public void Seed(EventData initial) { lock (gate) data = Copy(initial); }
    public Task<Versioned<EventData>> Read(CancellationToken token)
    {
        lock (gate) return Task.FromResult(new Versioned<EventData>(Copy(data), ETag));
    }
    public async Task<Versioned<PersonalPoints>?> Personal(Guid voter, CancellationToken token)
    {
        Versioned<PersonalPoints>? result;
        lock (gate) result = personal.TryGetValue(voter, out var value) ? Copy(value) : null;
        var hook = AfterPersonal;
        AfterPersonal = null;
        if (hook is not null) await hook();
        return result;
    }
    public Task<Vote?> Vote(Guid request, CancellationToken token)
    {
        lock (gate) return Task.FromResult(Votes.TryGetValue(request, out var value) ? Copy(value) : null);
    }
    public async Task<bool> Commit(CosmosCommit commit, CancellationToken token)
    {
        var hook = BeforeCommit;
        BeforeCommit = null;
        if (hook is not null) await hook();
        lock (gate)
        {
            if (commit.ETag != ETag || commit.Data.NewVotes.Any(v => Votes.ContainsKey(v.Id))) return false;
            if (commit.Voter.HasValue && personal.GetValueOrDefault(commit.Voter.Value)?.ETag != commit.PersonalETag) return false;
            data = Copy(commit.Data);
            version++;
            foreach (var vote in commit.Data.NewVotes) Votes.Add(vote.Id, Copy(vote));
            Adjustments.AddRange(commit.Data.NewAdjustments.Select(Copy));
            if (commit.Voter.HasValue && commit.Data.NewVotes.Count != 0)
                personal[commit.Voter.Value] = new(new PersonalPoints { Points = Copy(commit.Data.PersonalPoints) }, ETag);
            return true;
        }
    }
}
