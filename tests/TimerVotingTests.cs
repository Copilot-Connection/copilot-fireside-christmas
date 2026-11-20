using System.Net;
using System.Net.Http.Json;
using Fireside;
using Xunit;

namespace Fireside.Tests;

public sealed class TimerVotingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_votes_stop_at_the_exact_deadline_and_when_stopped_but_accepted_retries_remain_idempotent(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var voter = await site.Client("Attendee");
        using var admin = await site.Client("Fireside.Admin");
        await AudienceAccountTests.Token(voter);
        (await voter.PutAsJsonAsync("/api/participant", new NameInput("Timer listener"))).EnsureSuccessStatusCode();
        await AudienceAccountTests.Token(admin);
        var board = (await admin.GetFromJsonAsync<BoardView>("/api/admin/board"))!;
        var id = board.CurrentQuestionId!.Value;
        var requestId = Guid.NewGuid();
        site.Clock.Now = board.TimerStartedAt!.Value.AddSeconds(board.TimerDurationSeconds!.Value).AddTicks(-1);
        (await voter.PostAsJsonAsync($"/api/questions/{id}/vote", new ReactionInput(requestId, "epic"))).EnsureSuccessStatusCode();
        site.Clock.Now = site.Clock.Now.AddTicks(1);
        Assert.Equal(HttpStatusCode.Conflict,
            (await voter.PostAsJsonAsync($"/api/questions/{id}/vote", new ReactionInput(Guid.NewGuid(), "like"))).StatusCode);
        (await voter.PostAsJsonAsync($"/api/questions/{id}/vote", new ReactionInput(requestId, "epic"))).EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{board.Revision}\"");
        (await admin.PostAsJsonAsync("/api/admin/discussion", new DiscussionInput(id, false))).EnsureSuccessStatusCode();
        board = (await admin.GetFromJsonAsync<BoardView>("/api/admin/board"))!;
        admin.DefaultRequestHeaders.Remove("If-Match");
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{board.Revision}\"");
        (await admin.DeleteAsync("/api/admin/discussion/timer")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,
            (await voter.PostAsJsonAsync($"/api/questions/{id}/vote", new ReactionInput(Guid.NewGuid(), "like"))).StatusCode);
        Assert.Equal(3, (await voter.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].Score);
    }
}
