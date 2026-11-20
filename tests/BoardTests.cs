using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Fireside;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fireside.Tests;

public sealed class BoardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audience_sees_only_published_content_and_admin_requires_role(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var audience = await site.Client();
        var board = await audience.GetFromJsonAsync<BoardView>("/api/board");
        Assert.Single(board!.Questions);
        Assert.Single(board.Panellists);
        Assert.Equal(HttpStatusCode.Unauthorized, (await audience.GetAsync("/api/admin/board")).StatusCode);
        audience.DefaultRequestHeaders.Add("X-Test-Role", "Attendee");
        Assert.Equal(HttpStatusCode.Forbidden, (await audience.GetAsync("/api/admin/board")).StatusCode);
        audience.DefaultRequestHeaders.Remove("X-Test-Role");
        audience.DefaultRequestHeaders.Add("X-Test-Role", "Fireside.Admin");
        var all = await audience.GetFromJsonAsync<BoardView>("/api/admin/board");
        Assert.Equal(2, all!.Questions.Count);
        Assert.Equal(2, all.Panellists.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identity_is_server_issued_and_writes_require_antiforgery(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var client = await site.Client();
        var board = (await client.GetFromJsonAsync<BoardView>("/api/board"))!;
        var id = board.Questions[0].Id;
        Assert.Equal(HttpStatusCode.Unauthorized, (await React(client, id)).StatusCode);
        await Token(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await React(client, id)).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-fireside-participant=forged; fireside-participant=forged");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/questions/{id}/vote",
            new { participantId = Guid.NewGuid(), isAdmin = true })).StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/participant", new NameInput(" \n "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/participant", new NameInput("a\u0001b"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/participant", new NameInput("<script>alert(1)</script>"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await React(client, id)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", "Attendee");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await React(client, id)).StatusCode);
        await Token(client);
        Assert.Equal(HttpStatusCode.NoContent, (await React(client, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/questions",
            new { text = "unauthorized", visible = true, isAdmin = true })).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reactions_are_repeatable_but_retries_of_the_same_request_are_idempotent(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var client = await site.Client("Attendee");
        await Token(client);
        (await client.PutAsJsonAsync("/api/participant", new NameInput("A curious person"))).EnsureSuccessStatusCode();
        var question = (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        var requestId = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => React(client, question.Id, requestId: requestId)));
        Assert.All(results, response => Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict));
        (await client.PutAsJsonAsync("/api/participant", new NameInput("A new name"))).EnsureSuccessStatusCode();
        (await React(client, question.Id, requestId: requestId)).EnsureSuccessStatusCode();
        var after = (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        Assert.Equal(1, after.Score);
        (await React(client, question.Id)).EnsureSuccessStatusCode();
        (await React(client, question.Id, "dislike")).EnsureSuccessStatusCode();
        (await React(client, question.Id, "epic")).EnsureSuccessStatusCode();
        Assert.Equal(4, (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].Score);
        using var secondBrowser = await site.Client("Attendee");
        await Token(secondBrowser);
        (await secondBrowser.PutAsJsonAsync("/api/participant", new NameInput("A new name"))).EnsureSuccessStatusCode();
        (await React(secondBrowser, question.Id)).EnsureSuccessStatusCode();
        Assert.Equal(5, (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].Score);
        using var otherIdentity = await site.Client("Attendee");
        otherIdentity.DefaultRequestHeaders.Add("X-Test-ObjectId", EntraIdentity.DemoVoterObject);
        await Token(otherIdentity);
        (await otherIdentity.PutAsJsonAsync("/api/participant", new NameInput("Same display name"))).EnsureSuccessStatusCode();
        (await React(otherIdentity, question.Id, "dislike")).EnsureSuccessStatusCode();
        Assert.Equal(4, (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].Score);
        Assert.Equal(HttpStatusCode.Conflict, (await React(otherIdentity, question.Id, requestId: requestId)).StatusCode);
        Assert.Equal(5, (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].MyScore);
        Assert.Equal(5, (await secondBrowser.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].MyScore);
        Assert.Equal(-1, (await otherIdentity.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].MyScore);
        using var anonymous = await site.Client();
        var publicQuestion = (await anonymous.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        Assert.Equal(4, publicQuestion.Score);
        Assert.Null(publicQuestion.MyScore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admin_mutations_enforce_revision_and_clear_ineligible_current_question(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var admin = await site.Client("Fireside.Admin");
        await Token(admin);
        var board = (await admin.GetFromJsonAsync<BoardView>("/api/admin/board"))!;
        var question = board.Questions.Single(q => q.Visible);
        var change = new BoardInput("Edited title", "A live subtitle", true, question.Id);
        Assert.Equal((HttpStatusCode)428, (await admin.PutAsJsonAsync("/api/admin/board", change)).StatusCode);
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{board.Revision}\"");
        (await admin.PutAsJsonAsync("/api/admin/board", change)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/admin/board", change)).StatusCode);
        var next = (await admin.GetFromJsonAsync<BoardView>("/api/admin/board"))!;
        Assert.Equal(question.Id, next.CurrentQuestionId);
        admin.DefaultRequestHeaders.Remove("If-Match");
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{next.Revision}\"");
        (await admin.PutAsJsonAsync($"/api/admin/questions/{question.Id}",
            new QuestionInput(question.Text, question.Category, false, false, 0))).EnsureSuccessStatusCode();
        using var audience = await site.Client("Attendee");
        var publicBoard = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Null(publicBoard.CurrentQuestionId);
        Assert.Empty(publicBoard.Questions);
        await Token(audience);
        (await audience.PutAsJsonAsync("/api/participant", new NameInput("Test voter"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await React(audience, question.Id)).StatusCode);
    }

    [Fact]
    public async Task Paused_or_answered_questions_reject_votes_and_question_deletion_cascades()
    {
        await using var site = new Site();
        using var voter = await site.Client("Attendee");
        await Token(voter);
        (await voter.PutAsJsonAsync("/api/participant", new NameInput("Test voter"))).EnsureSuccessStatusCode();
        var question = (await voter.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            (await db.Boards.SingleAsync()).VotingOpen = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await React(voter, question.Id)).StatusCode);
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            (await db.Boards.SingleAsync()).VotingOpen = true;
            (await db.Questions.FindAsync(question.Id))!.Answered = true;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await React(voter, question.Id)).StatusCode);
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            (await db.Questions.FindAsync(question.Id))!.Answered = false;
            await db.SaveChangesAsync();
        }
        (await React(voter, question.Id)).EnsureSuccessStatusCode();
        using var admin = await site.Client("Fireside.Admin");
        await Token(admin);
        var board = (await admin.GetFromJsonAsync<BoardView>("/api/admin/board"))!;
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{board.Revision}\"");
        (await admin.DeleteAsync($"/api/admin/questions/{question.Id}")).EnsureSuccessStatusCode();
        using var final = site.Services.CreateScope();
        Assert.Equal(0, await final.ServiceProvider.GetRequiredService<FiresideDb>().Votes.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hub_notifies_after_commit_but_exposes_no_client_publish_method(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var admin = await site.Client("Fireside.Admin");
        await Token(admin);
        await using var hub = new HubConnectionBuilder().WithUrl("https://localhost/live", options =>
        {
            options.HttpMessageHandlerFactory = _ => site.Server.CreateHandler();
            options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
        }).Build();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On("boardChanged", () => notified.TrySetResult());
        await hub.StartAsync();
        var board = (await admin.GetFromJsonAsync<BoardView>("/api/admin/board"))!;
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{board.Revision}\"");
        (await admin.PutAsJsonAsync("/api/admin/board", new BoardInput("Live update", "", true, null))).EnsureSuccessStatusCode();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Live update", (await admin.GetFromJsonAsync<BoardView>("/api/board"))!.Title);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => hub.InvokeAsync("Publish", "forged state"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task External_tenant_accounts_can_vote_but_external_admin_roles_cannot_manage_the_event(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var external = await site.Client("Fireside.Admin");
        external.DefaultRequestHeaders.Add("X-Test-Tenant", EntraIdentity.DemoVoterTenant);
        await Token(external);
        var session = await external.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.False(session.GetProperty("isAdmin").GetBoolean());
        Assert.True(session.GetProperty("canVote").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await external.GetAsync("/api/admin/board")).StatusCode);
        var question = (await external.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        Assert.Equal(HttpStatusCode.Unauthorized, (await React(external, question.Id)).StatusCode);
        (await external.PutAsJsonAsync("/api/participant", new NameInput("Guest from another organisation"))).EnsureSuccessStatusCode();
        (await React(external, question.Id)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await external.PostAsJsonAsync("/api/admin/questions",
            new QuestionInput("Cannot publish", "", true, false, 0))).StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid", EntraIdentity.DemoVoterObject)]
    [InlineData(EntraIdentity.DemoVoterTenant, "not-a-guid")]
    [InlineData("9188040d-6c67-4c5b-b112-36a304b66dad", EntraIdentity.DemoVoterObject)]
    public async Task Malformed_or_personal_microsoft_identity_is_not_a_voter(string tenant, string person)
    {
        await using var site = new Site();
        using var client = await site.Client("Attendee");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Test-ObjectId", person);
        await Token(client);
        var question = (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        Assert.Equal(HttpStatusCode.Forbidden, (await React(client, question.Id)).StatusCode);
    }

    [Fact]
    public void Demo_mode_is_rejected_outside_local_development()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Demo:Enabled", "true");
        });
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Demo authentication", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scores_can_be_negative_and_invalid_reaction_actions_are_rejected(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var client = await site.Client("Attendee");
        await Token(client);
        (await client.PutAsJsonAsync("/api/participant", new NameInput("Reactor"))).EnsureSuccessStatusCode();
        var question = (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        (await React(client, question.Id, "dislike")).EnsureSuccessStatusCode();
        Assert.Equal(-1, (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].Score);
        Assert.Equal(HttpStatusCode.BadRequest, (await React(client, question.Id, "add-a-million")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await React(client, question.Id, requestId: Guid.Empty)).StatusCode);
        Assert.Equal(-1, (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].Score);
    }

    [Fact]
    public async Task Every_new_reaction_reaches_both_listeners_and_is_not_replayed()
    {
        await using var site = new Site();
        using var voter = await site.Client("Attendee");
        await Token(voter);
        (await voter.PutAsJsonAsync("/api/participant", new NameInput("Reactor"))).EnsureSuccessStatusCode();
        var question = (await voter.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        await using var first = Listener(site);
        await using var second = Listener(site);
        var firstEvents = Channel.CreateUnbounded<LiveReaction>();
        var secondEvents = Channel.CreateUnbounded<LiveReaction>();
        first.On<LiveReaction>("reaction", reaction => { firstEvents.Writer.TryWrite(reaction); });
        second.On<LiveReaction>("reaction", reaction => { secondEvents.Writer.TryWrite(reaction); });
        await first.StartAsync();
        await second.StartAsync();
        Guid last = Guid.Empty;
        foreach (var kind in new[] { "like", "dislike", "epic" })
        {
            last = Guid.NewGuid();
            (await React(voter, question.Id, kind, last)).EnsureSuccessStatusCode();
            foreach (var channel in new[] { firstEvents, secondEvents })
            {
                var reaction = await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(last, reaction.Id);
                Assert.Equal(question.Id, reaction.QuestionId);
                Assert.Equal(kind, reaction.Kind);
            }
        }
        (await React(voter, question.Id, "epic", last)).EnsureSuccessStatusCode();
        await first.StopAsync();
        await first.StartAsync();
        using var timeout = new CancellationTokenSource(400);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstEvents.Reader.ReadAsync(timeout.Token));
        Assert.False(secondEvents.Reader.TryRead(out _));
        Assert.Equal(3, (await voter.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].Score);
    }

    [Fact]
    public async Task Reaction_rate_limits_are_per_identity_instead_of_shared_venue_ip()
    {
        await using var site = new Site();
        using var voter = await site.Client("Attendee");
        await Token(voter);
        (await voter.PutAsJsonAsync("/api/participant", new NameInput("Reactor"))).EnsureSuccessStatusCode();
        var question = (await voter.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        var responses = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => React(voter, question.Id)));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        using var other = await site.Client("Attendee");
        other.DefaultRequestHeaders.Add("X-Test-ObjectId", Guid.NewGuid().ToString());
        await Token(other);
        (await other.PutAsJsonAsync("/api/participant", new NameInput("Another reactor"))).EnsureSuccessStatusCode();
        (await React(other, question.Id)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Landing_artwork_is_role_csrf_and_revision_protected_and_persists_until_reset()
    {
        await using var site = new Site();
        using var publicClient = await site.Client();
        var before = (await publicClient.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Equal(LandingImages.DefaultUrl, before.LandingImageUrl);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(publicClient, Pixel)).StatusCode);
        using var voter = await site.Client("Attendee");
        await Token(voter);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(voter, Pixel)).StatusCode);
        using var admin = await site.Client("Fireside.Admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, Pixel)).StatusCode);
        await Token(admin);
        Assert.Equal((HttpStatusCode)428, (await Upload(admin, Pixel)).StatusCode);
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{before.Revision}\"");
        (await Upload(admin, Pixel)).EnsureSuccessStatusCode();
        var after = (await publicClient.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.True(after.HasCustomLandingImage);
        Assert.Equal("A test poster", after.LandingImageDescription);
        using var image = await publicClient.GetAsync(after.LandingImageUrl);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Pixel, await image.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(admin, Pixel)).StatusCode);
        using (var scope = site.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<FiresideDb>().Boards.SingleAsync();
            Assert.Equal(Pixel, stored.LandingImageData);
        }
        admin.DefaultRequestHeaders.Remove("If-Match");
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{after.Revision}\"");
        (await admin.DeleteAsync("/api/admin/landing-image")).EnsureSuccessStatusCode();
        var reset = (await publicClient.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.False(reset.HasCustomLandingImage);
        Assert.Equal(LandingImages.DefaultUrl, reset.LandingImageUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Speaker_timers_are_authoritative_and_group_mode_blocks_votes_even_after_stopping_the_clock(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var admin = await site.Client("Fireside.Admin");
        using var voter = await site.Client("Attendee");
        using var audience = await site.Client();
        var board = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        var question = board.Questions[0];
        var speaker = board.Panellists[0];
        var discussion = new DiscussionInput(question.Id, false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await audience.PostAsJsonAsync("/api/admin/discussion", discussion)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await voter.PostAsJsonAsync("/api/admin/discussion", discussion)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/discussion", discussion)).StatusCode);
        await Token(admin);
        await Revision(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/discussion", discussion)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/timing", new TimingInput(4, 30))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/questions/{question.Id}",
            new QuestionInput(question.Text, question.Category, true, false, 0, Guid.NewGuid()))).StatusCode);
        (await admin.PutAsJsonAsync($"/api/admin/questions/{question.Id}",
            new QuestionInput(question.Text, question.Category, true, false, 0, speaker.Id))).EnsureSuccessStatusCode();
        await Revision(admin);
        (await admin.PutAsJsonAsync("/api/admin/timing", new TimingInput(15, 30))).EnsureSuccessStatusCode();
        await Revision(admin);
        (await admin.PostAsJsonAsync("/api/admin/discussion", discussion)).EnsureSuccessStatusCode();
        var started = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Equal(speaker.Id, started.Questions[0].SpeakerId);
        Assert.Equal(site.Clock.GetUtcNow(), started.TimerStartedAt);
        Assert.Equal(15, started.TimerDurationSeconds);
        Assert.False(started.GroupDiscussion);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/discussion", discussion)).StatusCode);
        site.Clock.Now = site.Clock.Now.AddSeconds(10);
        var recovered = (await voter.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Equal(started.TimerStartedAt, recovered.TimerStartedAt);
        Assert.Equal(5, (recovered.TimerStartedAt!.Value.AddSeconds(recovered.TimerDurationSeconds!.Value) - recovered.ServerTime).TotalSeconds);
        await Revision(admin);
        (await admin.PutAsJsonAsync("/api/admin/timing", new TimingInput(20, 45))).EnsureSuccessStatusCode();
        Assert.Equal(15, (await audience.GetFromJsonAsync<BoardView>("/api/board"))!.TimerDurationSeconds);
        await Revision(admin);
        (await admin.PostAsJsonAsync("/api/admin/discussion", discussion with { GroupDiscussion = true })).EnsureSuccessStatusCode();
        var group = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.True(group.GroupDiscussion);
        Assert.True(group.VotingOpen);
        Assert.Equal(45, group.TimerDurationSeconds);
        await Token(voter);
        (await voter.PutAsJsonAsync("/api/participant", new NameInput("Group listener"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await React(voter, question.Id, "epic")).StatusCode);
        await Revision(admin);
        (await admin.DeleteAsync("/api/admin/discussion/timer")).EnsureSuccessStatusCode();
        var stopped = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Null(stopped.TimerStartedAt);
        Assert.True(stopped.GroupDiscussion);
        Assert.Equal(HttpStatusCode.Conflict, (await React(voter, question.Id)).StatusCode);
        await Revision(admin);
        (await admin.PostAsJsonAsync("/api/admin/discussion", discussion)).EnsureSuccessStatusCode();
        Assert.Equal(20, (await audience.GetFromJsonAsync<BoardView>("/api/board"))!.TimerDurationSeconds);
        (await React(voter, question.Id)).EnsureSuccessStatusCode();
        await Revision(admin);
        (await admin.DeleteAsync($"/api/admin/panellists/{speaker.Id}")).EnsureSuccessStatusCode();
        var deleted = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Null(deleted.Questions[0].SpeakerId);
        Assert.Null(deleted.TimerStartedAt);
    }

    [Fact]
    public async Task Panellist_uploads_persist_and_hidden_photos_and_assignments_are_not_public()
    {
        await using var site = new Site();
        using var audience = await site.Client();
        using var voter = await site.Client("Attendee");
        using var admin = await site.Client("Fireside.Admin");
        var board = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        var person = board.Panellists[0];
        var path = $"/api/admin/panellists/{person.Id}/photo";
        Assert.Equal(HttpStatusCode.Unauthorized, (await audience.PutAsync(path, new ByteArrayContent(Pixel))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await voter.PutAsync(path, new ByteArrayContent(Pixel))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync(path, new ByteArrayContent(Pixel))).StatusCode);
        await Token(admin);
        Assert.Equal((HttpStatusCode)428, (await admin.PutAsync(path, new ByteArrayContent(Pixel))).StatusCode);
        await Revision(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync(path, new ByteArrayContent("<svg/>"u8.ToArray()))).StatusCode);
        (await admin.PutAsync(path, new ByteArrayContent(Pixel))).EnsureSuccessStatusCode();
        var publicPerson = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!.Panellists[0];
        using var image = await audience.GetAsync(publicPerson.PhotoUrl);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Pixel, await image.Content.ReadAsByteArrayAsync());
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            Assert.Equal(Pixel, (await db.Panellists.FindAsync(person.Id))!.PhotoData);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync(path, new ByteArrayContent(Pixel))).StatusCode);
        var revision = await Revision(admin);
        var question = revision.Questions.Single(q => q.Visible);
        (await admin.PutAsJsonAsync($"/api/admin/questions/{question.Id}",
            new QuestionInput(question.Text, question.Category, true, false, 0, person.Id))).EnsureSuccessStatusCode();
        await Revision(admin);
        (await admin.PutAsJsonAsync($"/api/admin/panellists/{person.Id}",
            new PanellistInput(person.Name, person.Role, person.Bio, false, 0))).EnsureSuccessStatusCode();
        var hidden = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.DoesNotContain(hidden.Panellists, p => p.Id == person.Id);
        Assert.Null(hidden.Questions[0].SpeakerId);
        Assert.Equal(HttpStatusCode.NotFound, (await audience.GetAsync(publicPerson.PhotoUrl)).StatusCode);
        var adminBoard = await Revision(admin);
        var privateUrl = adminBoard.Panellists.Single(p => p.Id == person.Id).PhotoUrl;
        Assert.Equal(HttpStatusCode.Unauthorized, (await audience.GetAsync(privateUrl)).StatusCode);
        Assert.Equal(Pixel, await admin.GetByteArrayAsync(privateUrl));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/discussion", new DiscussionInput(question.Id, false))).StatusCode);
        (await admin.DeleteAsync(path)).EnsureSuccessStatusCode();
        Assert.Null((await admin.GetFromJsonAsync<BoardView>("/api/admin/board"))!.Panellists.Single(p => p.Id == person.Id).PhotoUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(privateUrl)).StatusCode);
    }

    [Fact]
    public async Task Per_question_speaker_order_persists_and_selection_clears_the_timer_without_starting_it()
    {
        await using var site = new Site();
        using var admin = await site.Client("Fireside.Admin");
        using var audience = await site.Client();
        using var voter = await site.Client("Attendee");
        await Token(admin);
        var board = await Revision(admin);
        var question = board.Questions.Single(q => q.Visible);
        var first = board.Panellists.Single(p => p.Visible);
        var hidden = board.Panellists.Single(p => !p.Visible);
        (await admin.PostAsJsonAsync("/api/admin/panellists",
            new PanellistInput("Another speaker", "", "", true, 1))).EnsureSuccessStatusCode();
        board = await Revision(admin);
        var second = board.Panellists.Single(p => p.Name == "Another speaker");
        var order = new[] { second.Id, hidden.Id, first.Id };
        var input = new QuestionInput(question.Text, question.Category, true, false, 0, first.Id, order);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/questions/{question.Id}",
            input with { SpeakerIds = [first.Id, first.Id, second.Id] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/admin/questions/{question.Id}",
            input with { SpeakerIds = [Guid.NewGuid(), second.Id, first.Id] })).StatusCode);
        (await admin.PutAsJsonAsync($"/api/admin/questions/{question.Id}", input)).EnsureSuccessStatusCode();
        board = await Revision(admin);
        Assert.Equal(order, board.Questions.Single(q => q.Id == question.Id).SpeakerIds);
        Assert.Equal(board.Panellists.Select(p => p.Id), board.Questions.Single(q => q.Id != question.Id).SpeakerIds);
        var publicBoard = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Equal(new[] { second.Id, first.Id }, publicBoard.Questions[0].SpeakerIds);
        using (var scope = site.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<FiresideDb>().QuestionSpeakerOrders
                .Where(s => s.QuestionId == question.Id).OrderBy(s => s.SortOrder).Select(s => s.PanellistId).ToArrayAsync();
            Assert.Equal(order, stored);
        }
        var path = $"/api/admin/questions/{question.Id}/speaker";
        Assert.Equal(HttpStatusCode.Unauthorized, (await audience.PutAsJsonAsync(path, new SpeakerInput(second.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await voter.PutAsJsonAsync(path, new SpeakerInput(second.Id))).StatusCode);
        using var noCsrf = await site.Client("Fireside.Admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await noCsrf.PutAsJsonAsync(path, new SpeakerInput(second.Id))).StatusCode);
        (await admin.PostAsJsonAsync("/api/admin/discussion", new DiscussionInput(question.Id, true))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(path, new SpeakerInput(second.Id))).StatusCode);
        await Revision(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(path, new SpeakerInput(hidden.Id))).StatusCode);
        (await admin.PutAsJsonAsync(path, new SpeakerInput(second.Id))).EnsureSuccessStatusCode();
        var selected = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Equal(question.Id, selected.CurrentQuestionId);
        Assert.Equal(second.Id, selected.Questions[0].SpeakerId);
        Assert.Null(selected.TimerStartedAt);
        Assert.Null(selected.TimerDurationSeconds);
        Assert.False(selected.GroupDiscussion);
        await Revision(admin);
        (await admin.PostAsJsonAsync("/api/admin/discussion", new DiscussionInput(question.Id, false))).EnsureSuccessStatusCode();
        await Revision(admin);
        (await admin.PutAsJsonAsync(path, new SpeakerInput(first.Id))).EnsureSuccessStatusCode();
        Assert.Null((await audience.GetFromJsonAsync<BoardView>("/api/board"))!.TimerStartedAt);
        await Revision(admin);
        (await admin.DeleteAsync($"/api/admin/panellists/{second.Id}")).EnsureSuccessStatusCode();
        Assert.DoesNotContain(second.Id, (await audience.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].SpeakerIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Speaker_scores_keep_cast_time_attribution_and_audited_adjustments_leave_voter_history_unchanged(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var admin = await site.Client("Fireside.Admin");
        using var voter = await site.Client("Attendee");
        using var audience = await site.Client();
        await Token(admin);
        await Token(voter);
        (await voter.PutAsJsonAsync("/api/participant", new NameInput("Score viewer"))).EnsureSuccessStatusCode();
        var board = await Revision(admin);
        Assert.NotNull(board.Scoreboard);
        var question = board.Questions.Single(q => q.Visible);
        var first = board.Panellists.Single(p => p.Visible);
        (await admin.PostAsJsonAsync("/api/admin/panellists", new PanellistInput("Second speaker", "", "", true, 1))).EnsureSuccessStatusCode();
        board = await Revision(admin);
        var second = board.Panellists.Single(p => p.Name == "Second speaker");
        var path = $"/api/admin/questions/{question.Id}/speaker";
        (await admin.PutAsJsonAsync(path, new SpeakerInput(first.Id))).EnsureSuccessStatusCode();
        var requestId = Guid.NewGuid();
        (await React(voter, question.Id, "epic", requestId)).EnsureSuccessStatusCode();
        await Revision(admin);
        (await admin.PutAsJsonAsync(path, new SpeakerInput(second.Id))).EnsureSuccessStatusCode();
        (await React(voter, question.Id, "dislike")).EnsureSuccessStatusCode();
        (await React(voter, question.Id, "epic", requestId)).EnsureSuccessStatusCode();
        board = await Revision(admin);
        var row = board.Scoreboard!.Questions.Single(q => q.Id == question.Id);
        Assert.Equal(3, row.Scores.Single(s => s.SpeakerId == first.Id).Points);
        Assert.Equal(-1, row.Scores.Single(s => s.SpeakerId == second.Id).Points);
        var edit = $"/api/admin/questions/{question.Id}/scores/{first.Id}";
        (await admin.PutAsJsonAsync(edit, new ScoreInput(10, "Host correction"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(edit, new ScoreInput(12, "Stale edit"))).StatusCode);
        var personal = (await voter.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Equal(9, personal.Questions[0].Score);
        Assert.Equal(2, personal.Questions[0].MyScore);
        Assert.False(personal.ScoreboardVisible);
        Assert.Null(personal.Scoreboard);
        Assert.DoesNotContain("\"scoreboard\":", await audience.GetStringAsync("/api/board"));
        await Revision(admin);
        (await admin.PutAsJsonAsync("/api/admin/scoreboard", new ScoreboardInput(true))).EnsureSuccessStatusCode();
        var published = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Equal(10, published.Scoreboard!.Speakers.Single(s => s.Id == first.Id).Points);
        ScoreAdjustment adjustment;
        if (cosmos)
        {
            var stored = site.Services.GetRequiredService<MemoryCosmosDocuments>();
            Assert.Equal(2, stored.Votes.Count);
            Assert.Equal(first.Id, stored.Votes[requestId].SpeakerId);
            adjustment = Assert.Single(stored.Adjustments);
        }
        else
        {
            using var scope = site.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            Assert.Equal(2, await db.Votes.CountAsync());
            Assert.Equal(first.Id, (await db.Votes.FindAsync(requestId))!.SpeakerId);
            adjustment = await db.ScoreAdjustments.SingleAsync();
        }
        Assert.Equal(7, adjustment.Delta);
        Assert.Equal("Host correction", adjustment.Reason);
        Assert.NotEqual(Guid.Empty, adjustment.ActorId);
        Assert.Equal(site.Clock.GetUtcNow(), adjustment.ChangedAt);
        await Revision(admin);
        (await admin.PutAsJsonAsync(path, new SpeakerInput(first.Id))).EnsureSuccessStatusCode();
        (await React(voter, question.Id)).EnsureSuccessStatusCode();
        Assert.Equal(11, (await audience.GetFromJsonAsync<BoardView>("/api/board"))!.Scoreboard!.Speakers.Single(s => s.Id == first.Id).Points);
        await Revision(admin);
        (await admin.PutAsJsonAsync($"/api/admin/panellists/{first.Id}", new PanellistInput(first.Name, first.Role, first.Bio, false, 0))).EnsureSuccessStatusCode();
        Assert.DoesNotContain(first.Id.ToString(), await audience.GetStringAsync("/api/board"), StringComparison.OrdinalIgnoreCase);
        await Revision(admin);
        (await admin.DeleteAsync($"/api/admin/panellists/{first.Id}")).EnsureSuccessStatusCode();
        board = await Revision(admin);
        Assert.True(board.Scoreboard!.Speakers.Single(s => s.Id == first.Id).Removed);
        Assert.Equal(11, board.Scoreboard.Speakers.Single(s => s.Id == first.Id).Points);
        (await admin.PutAsJsonAsync("/api/admin/scoreboard", new ScoreboardInput(false))).EnsureSuccessStatusCode();
        Assert.DoesNotContain("\"scoreboard\":", await audience.GetStringAsync("/api/board"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Score_edits_and_visibility_enforce_role_csrf_revision_and_input_validation(bool cosmos)
    {
        await using var site = new Site(cosmos);
        using var admin = await site.Client("Fireside.Admin");
        using var voter = await site.Client("Attendee");
        using var audience = await site.Client();
        var board = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        var edit = $"/api/admin/questions/{board.Questions[0].Id}/scores/{board.Panellists[0].Id}";
        foreach (var path in new[] { edit, "/api/admin/scoreboard" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await audience.PutAsJsonAsync(path, new { points = 3, reason = "Test", visible = true })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await voter.PutAsJsonAsync(path, new { points = 3, reason = "Test", visible = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(path, new { points = 3, reason = "Test", visible = true })).StatusCode);
        }
        await Token(admin);
        Assert.Equal((HttpStatusCode)428, (await admin.PutAsJsonAsync(edit, new ScoreInput(3, "Test"))).StatusCode);
        await Revision(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(edit, new ScoreInput(3, ""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(edit, new ScoreInput(1000000001, "Too large"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/admin/questions/{Guid.NewGuid()}/scores/{board.Panellists[0].Id}", new ScoreInput(3, "Unknown"))).StatusCode);
        (await admin.PutAsJsonAsync("/api/admin/scoreboard", new ScoreboardInput(true))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/admin/scoreboard", new ScoreboardInput(false))).StatusCode);
    }

    private static async Task<BoardView> Revision(HttpClient client)
    {
        var board = (await client.GetFromJsonAsync<BoardView>("/api/admin/board"))!;
        client.DefaultRequestHeaders.Remove("If-Match");
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", $"\"{board.Revision}\"");
        return board;
    }

    [Fact]
    public async Task Image_uploads_reject_oversized_or_non_raster_content()
    {
        await using var site = new Site();
        using var admin = await site.Client("Fireside.Admin");
        await Token(admin);
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", "\"0\"");
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "<svg onload='alert(1)'/>"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, [])).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(admin, new byte[ImageUploads.MaximumBytes + 1])).StatusCode);
        var enormous = (byte[])Pixel.Clone();
        enormous[16] = 127;
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, enormous)).StatusCode);
        Assert.False((await admin.GetFromJsonAsync<BoardView>("/api/board"))!.HasCustomLandingImage);
    }

    private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6Vf8AAAAASUVORK5CYII=");
    private static Task<HttpResponseMessage> Upload(HttpClient client, byte[] image) =>
        client.PutAsync("/api/admin/landing-image?description=A%20test%20poster", new ByteArrayContent(image));
    private static Task<HttpResponseMessage> React(HttpClient client, Guid question, string kind = "like", Guid? requestId = null) =>
        client.PostAsJsonAsync($"/api/questions/{question}/vote", new ReactionInput(requestId ?? Guid.NewGuid(), kind));
    private static HubConnection Listener(Site site) => new HubConnectionBuilder().WithUrl("https://localhost/live", options =>
    {
        options.HttpMessageHandlerFactory = _ => site.Server.CreateHandler();
        options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
    }).Build();

    private static async Task Token(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
    }
}

public sealed class Site(bool cosmos = false) : WebApplicationFactory<Program>
{
    public TestClock Clock { get; } = new();
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"fireside-tests-{Guid.NewGuid():N}");
    private bool initialized;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(directory);
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Fireside", $"Data Source={Path.Combine(directory, "test.db")};Pooling=False;Default Timeout=10");
        builder.UseSetting("DataProtection:KeyPath", Path.Combine(directory, "keys"));
        builder.UseSetting("AzureAd:TenantId", EntraIdentity.DemoTenant);
        builder.ConfigureServices(services =>
        {
            if (cosmos)
            {
                services.AddSingleton<MemoryCosmosDocuments>();
                services.AddSingleton<ICosmosEventDocuments>(provider => provider.GetRequiredService<MemoryCosmosDocuments>());
                services.AddSingleton<IEventImages, MemoryEventImages>();
                services.AddScoped<IEventStore, CosmosEventStore>();
            }
            services.AddSingleton<TimeProvider>(Clock);
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
                options.DefaultForbidScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
        });
    }

    public async Task<HttpClient> Client(string? role = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (role is not null) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        if (!initialized)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            await db.Database.EnsureCreatedAsync();
            db.Boards.Add(new Board { VotingOpen = true });
            db.Questions.AddRange(new Question { Text = "A published question", Category = "Test", Visible = true },
                new Question { Text = "A hidden question", Visible = false });
            db.Panellists.AddRange(new Panellist { Name = "Visible person", Visible = true },
                new Panellist { Name = "Hidden person", Visible = false });
            await db.SaveChangesAsync();
            if (cosmos)
                Services.GetRequiredService<MemoryCosmosDocuments>().Seed(
                    await new SqliteEventStore(db).Read(null, default));
            initialized = true;
        }
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].ToString();
        if (string.IsNullOrEmpty(role)) return Task.FromResult(AuthenticateResult.NoResult());
        var tenant = Request.Headers["X-Test-Tenant"].FirstOrDefault() ?? EntraIdentity.DemoTenant;
        var person = Request.Headers["X-Test-ObjectId"].FirstOrDefault() ?? EntraIdentity.DemoHostObject;
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, $"{tenant}:{person}"),
            new Claim(ClaimTypes.Name, "Test host"), new Claim(ClaimTypes.Role, role),
            new Claim("tid", tenant), new Claim("oid", person)], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}
