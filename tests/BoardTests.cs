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
    [Fact]
    public async Task Audience_sees_only_published_content_and_admin_requires_role()
    {
        await using var site = new Site();
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

    [Fact]
    public async Task Identity_is_server_issued_and_writes_require_antiforgery()
    {
        await using var site = new Site();
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

    [Fact]
    public async Task Reactions_are_repeatable_but_retries_of_the_same_request_are_idempotent()
    {
        await using var site = new Site();
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
    }

    [Fact]
    public async Task Admin_mutations_enforce_revision_and_clear_ineligible_current_question()
    {
        await using var site = new Site();
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

    [Fact]
    public async Task Hub_notifies_after_commit_but_exposes_no_client_publish_method()
    {
        await using var site = new Site();
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

    [Fact]
    public async Task External_tenant_accounts_can_vote_but_external_admin_roles_cannot_manage_the_event()
    {
        await using var site = new Site();
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

    [Fact]
    public async Task Scores_can_be_negative_and_invalid_reaction_actions_are_rejected()
    {
        await using var site = new Site();
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

    [Fact]
    public async Task Image_uploads_reject_oversized_or_non_raster_content()
    {
        await using var site = new Site();
        using var admin = await site.Client("Fireside.Admin");
        await Token(admin);
        admin.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", "\"0\"");
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "<svg onload='alert(1)'/>"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, [])).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(admin, new byte[LandingImages.MaximumBytes + 1])).StatusCode);
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

public sealed class Site : WebApplicationFactory<Program>
{
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
