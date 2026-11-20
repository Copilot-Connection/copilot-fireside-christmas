using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/questions/{id}/vote", null)).StatusCode);
        await Token(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/questions/{id}/vote", null)).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-fireside-participant=forged; fireside-participant=forged");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/questions/{id}/vote",
            new { participantId = Guid.NewGuid(), isAdmin = true })).StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/participant", new NameInput(" \n "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/participant", new NameInput("a\u0001b"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/participant", new NameInput("<script>alert(1)</script>"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/questions/{id}/vote", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/admin/questions",
            new { text = "unauthorized", visible = true, isAdmin = true })).StatusCode);
    }

    [Fact]
    public async Task Concurrent_votes_are_unique_and_renaming_does_not_reset_identity()
    {
        await using var site = new Site();
        using var client = await site.Client();
        await Token(client);
        (await client.PutAsJsonAsync("/api/participant", new NameInput("A curious person"))).EnsureSuccessStatusCode();
        var question = (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsync($"/api/questions/{question.Id}/vote", null)));
        Assert.All(results, response => Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict));
        (await client.PutAsJsonAsync("/api/participant", new NameInput("A new name"))).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/questions/{question.Id}/vote", null)).EnsureSuccessStatusCode();
        var after = (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        Assert.Equal(1, after.Votes);
        Assert.True(after.HasVoted);
        using var secondBrowser = await site.Client();
        await Token(secondBrowser);
        (await secondBrowser.PutAsJsonAsync("/api/participant", new NameInput("A new name"))).EnsureSuccessStatusCode();
        (await secondBrowser.PostAsync($"/api/questions/{question.Id}/vote", null)).EnsureSuccessStatusCode();
        Assert.Equal(2, (await client.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0].Votes);
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
        using var audience = await site.Client();
        var publicBoard = (await audience.GetFromJsonAsync<BoardView>("/api/board"))!;
        Assert.Null(publicBoard.CurrentQuestionId);
        Assert.Empty(publicBoard.Questions);
        await Token(audience);
        (await audience.PutAsJsonAsync("/api/participant", new NameInput("Test voter"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await audience.PostAsync($"/api/questions/{question.Id}/vote", null)).StatusCode);
    }

    [Fact]
    public async Task Paused_or_answered_questions_reject_votes_and_question_deletion_cascades()
    {
        await using var site = new Site();
        using var voter = await site.Client();
        await Token(voter);
        (await voter.PutAsJsonAsync("/api/participant", new NameInput("Test voter"))).EnsureSuccessStatusCode();
        var question = (await voter.GetFromJsonAsync<BoardView>("/api/board"))!.Questions[0];
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            (await db.Boards.SingleAsync()).VotingOpen = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await voter.PostAsync($"/api/questions/{question.Id}/vote", null)).StatusCode);
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            (await db.Boards.SingleAsync()).VotingOpen = true;
            (await db.Questions.FindAsync(question.Id))!.Answered = true;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await voter.PostAsync($"/api/questions/{question.Id}/vote", null)).StatusCode);
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
            (await db.Questions.FindAsync(question.Id))!.Answered = false;
            await db.SaveChangesAsync();
        }
        (await voter.PostAsync($"/api/questions/{question.Id}/vote", null)).EnsureSuccessStatusCode();
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
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user"),
            new Claim(ClaimTypes.Name, "Test host"), new Claim(ClaimTypes.Role, role)], "Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
    }
}
