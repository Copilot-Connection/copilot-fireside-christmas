using System.Net;
using System.Net.Http.Json;
using Fireside;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fireside.Tests;

public sealed class ScoreResetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_clears_all_score_views_without_deleting_history_or_replaying_old_votes(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var admin = await site.Client("Fireside.Admin");
        using var unprivileged = await site.Client("Attendee");
        using var first = await site.Client();
        using var second = await site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.PostAsync("/api/admin/scores/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unprivileged.PostAsync("/api/admin/scores/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/scores/reset", null)).StatusCode);
        await AudienceAccountTests.Token(admin);
        foreach (var voter in new[] { first, second })
        {
            await AudienceAccountTests.Token(voter);
            (await voter.PutAsJsonAsync("/api/participant", new NameInput("Guest"))).EnsureSuccessStatusCode();
        }
        var board = (await admin.GetFromJsonAsync<BoardView>("/api/admin/board"))!;
        var question = board.Questions.Single(q => q.Visible);
        var speaker = board.Panellists.Single(p => p.Visible);
        var originalId = Guid.NewGuid();
        (await first.PostAsJsonAsync($"/api/questions/{question.Id}/vote", new ReactionInput(originalId, "epic"))).EnsureSuccessStatusCode();
        (await second.PostAsJsonAsync($"/api/questions/{question.Id}/vote", new ReactionInput(Guid.NewGuid(), "dislike"))).EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{board.Revision}\"");
        (await admin.PutAsJsonAsync($"/api/admin/questions/{question.Id}/scores/{speaker.Id}",
            new ScoreInput(8, "Before reset"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/scores/reset", null)).StatusCode);
        board = (await admin.GetFromJsonAsync<BoardView>("/api/admin/board"))!;
        Assert.Equal(8, board.Questions.Single(q => q.Id == question.Id).Score);
        await using var hub = new HubConnectionBuilder().WithUrl("https://localhost/live", options =>
        {
            options.HttpMessageHandlerFactory = _ => site.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            options.Headers["Origin"] = "https://localhost";
        }).Build();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On("boardChanged", () => notified.TrySetResult());
        await hub.StartAsync();
        admin.DefaultRequestHeaders.Remove("If-Match");
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{board.Revision}\"");
        (await admin.PostAsync("/api/admin/scores/reset", null)).EnsureSuccessStatusCode();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var voter in new[] { first, second })
        {
            var after = (await voter.GetFromJsonAsync<BoardView>("/api/board"))!.Questions.Single(q => q.Id == question.Id);
            Assert.Equal(0, after.Score);
            Assert.Equal(0, after.MyScore);
        }
        board = (await admin.GetFromJsonAsync<BoardView>("/api/admin/board"))!;
        Assert.Equal(0, board.Scoreboard!.Speakers.Single(s => s.Id == speaker.Id).Points);
        (await first.PostAsJsonAsync($"/api/questions/{question.Id}/vote", new ReactionInput(originalId, "epic"))).EnsureSuccessStatusCode();
        Assert.Equal(0, (await first.GetFromJsonAsync<BoardView>("/api/board"))!.Questions.Single(q => q.Id == question.Id).Score);
        (await first.PostAsJsonAsync($"/api/questions/{question.Id}/vote", new ReactionInput(Guid.NewGuid(), "like"))).EnsureSuccessStatusCode();
        var latest = (await first.GetFromJsonAsync<BoardView>("/api/board"))!.Questions.Single(q => q.Id == question.Id);
        Assert.Equal(1, latest.Score);
        Assert.Equal(1, latest.MyScore);
        Assert.Equal(0, (await second.GetFromJsonAsync<BoardView>("/api/board"))!.Questions.Single(q => q.Id == question.Id).MyScore);
        if (cosmos)
        {
            var docs = site.Services.GetRequiredService<MemoryCosmosDocuments>();
            Assert.Equal(3, docs.Votes.Count);
            Assert.Single(docs.Adjustments);
        }
        else
        {
            await using var scope = site.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            Assert.Equal(3, await db.Votes.CountAsync());
            Assert.Equal(1, await db.ScoreAdjustments.CountAsync());
            var archive = await EventArchive.Capture(db.Database.GetDbConnection().DataSource, default);
            Assert.Equal(3, archive.Votes.Count);
            Assert.Equal(1, Assert.Single(archive.Data.Scores).Points);
            Assert.Equal(1, Assert.Single(archive.Personal("test-event")).Points[question.Id]);
        }
    }

    [Fact]
    public async Task Vote_racing_a_reset_retries_in_the_new_score_generation()
    {
        var docs = new MemoryCosmosDocuments();
        docs.Seed(new EventData());
        var store = new CosmosEventStore(docs, new MemoryEventImages());
        var voter = Guid.NewGuid();
        var question = Guid.NewGuid();
        docs.BeforeCommit = async () =>
        {
            var reset = await store.Write(0, null, null, data =>
            {
                data.Board.ScoreEpoch++;
                data.Scores.Clear();
                return null;
            }, default);
            Assert.True(reset.Committed);
        };
        var result = await store.Write(null, voter, Guid.NewGuid(), data =>
        {
            data.NewVotes.Add(new Vote { Id = Guid.NewGuid(), QuestionId = question, ParticipantId = voter,
                Delta = 1, ScoreEpoch = data.Board.ScoreEpoch });
            data.AddScore(question, null, 1);
            data.PersonalPoints[question] = 1;
            return null;
        }, default);
        Assert.True(result.Committed);
        var final = await store.Read(voter, default);
        Assert.Equal(1, final.Board.ScoreEpoch);
        Assert.Equal(1, Assert.Single(final.Scores).Points);
        Assert.Equal(1, final.PersonalPoints[question]);
        Assert.Equal(1, Assert.Single(docs.Votes.Values).ScoreEpoch);
    }
}
