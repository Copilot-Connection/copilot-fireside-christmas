using System.Data;
using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fireside;

public static class InputRules
{
    public static bool Text(string? value, int minimum, int maximum) =>
        value is not null && value.Length >= minimum && value.Length <= maximum &&
        !value.Any(char.IsControl);
}

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        logger.LogError(exception, "Request failed: {Method} {Path}", context.Request.Method, context.Request.Path);
        await Results.Problem("Something went wrong. Please retry; if it continues, contact the event host.",
            statusCode: 500).ExecuteAsync(context);
        return true;
    }
}

public static class Api
{
    public static void MapFiresideApi(this WebApplication app)
    {
        app.MapGet("/api/board", async (FiresideDb db, HttpContext context) =>
            Results.Ok(await Snapshot(db, EntraIdentity.VoterKey(context.User), false, context.RequestAborted)));
        app.MapPost("/api/questions/{id:guid}/vote", Vote)
            .RequireAuthorization("Voter").RequireRateLimiting("participation");

        var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");
        admin.MapGet("/board", async (FiresideDb db, CancellationToken token) =>
            Results.Ok(await Snapshot(db, null, true, token)));
        admin.MapPut("/board", async (BoardInput input, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async board =>
            {
                if (!InputRules.Text(input.Title?.Trim(), 1, 120) ||
                    !InputRules.Text(input.Subtitle?.Trim(), 0, 300))
                    return Invalid("Enter a title (1-120 characters) and subtitle (up to 300 characters).");
                if (input.CurrentQuestionId is { } id &&
                    !await db.Questions.AnyAsync(q => q.Id == id && q.Visible && !q.Answered, context.RequestAborted))
                    return Invalid("Only a visible, unanswered question can be the current discussion.");
                board.Title = input.Title!.Trim();
                board.Subtitle = input.Subtitle!.Trim();
                board.VotingOpen = input.VotingOpen;
                board.CurrentQuestionId = input.CurrentQuestionId;
                return null;
            }));
        admin.MapPost("/panellists", async (PanellistInput input, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, _ =>
            {
                if (!ValidPanellist(input)) return Task.FromResult<IResult?>(Invalid("Check the panellist's name, role and bio lengths."));
                var person = new Panellist();
                Apply(person, input);
                db.Panellists.Add(person);
                return Task.FromResult<IResult?>(null);
            }));
        admin.MapPut("/panellists/{id:guid}", async (Guid id, PanellistInput input, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async _ =>
            {
                if (!ValidPanellist(input)) return Invalid("Check the panellist's name, role and bio lengths.");
                var person = await db.Panellists.FindAsync([id], context.RequestAborted);
                if (person is null) return Results.NotFound();
                Apply(person, input);
                return null;
            }));
        admin.MapDelete("/panellists/{id:guid}", async (Guid id, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async _ =>
            {
                var person = await db.Panellists.FindAsync([id], context.RequestAborted);
                if (person is null) return Results.NotFound();
                db.Panellists.Remove(person);
                return null;
            }));
        admin.MapPost("/questions", async (QuestionInput input, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, _ =>
            {
                if (!ValidQuestion(input)) return Task.FromResult<IResult?>(Invalid("Enter a question (1-240 characters) and category (up to 60)."));
                var question = new Question();
                Apply(question, input);
                db.Questions.Add(question);
                return Task.FromResult<IResult?>(null);
            }));
        admin.MapPut("/questions/{id:guid}", async (Guid id, QuestionInput input, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async board =>
            {
                if (!ValidQuestion(input)) return Invalid("Enter a question (1-240 characters) and category (up to 60).");
                var question = await db.Questions.FindAsync([id], context.RequestAborted);
                if (question is null) return Results.NotFound();
                Apply(question, input);
                if (board.CurrentQuestionId == id && (!question.Visible || question.Answered))
                    board.CurrentQuestionId = null;
                return null;
            }));
        admin.MapDelete("/questions/{id:guid}", async (Guid id, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async board =>
            {
                var question = await db.Questions.FindAsync([id], context.RequestAborted);
                if (question is null) return Results.NotFound();
                if (board.CurrentQuestionId == id) board.CurrentQuestionId = null;
                db.Questions.Remove(question);
                return null;
            }));
    }

    public static async Task<BoardView> Snapshot(FiresideDb db, Guid? participant, bool admin, CancellationToken token)
    {
        // A consistent snapshot prevents half-applied host changes appearing in the audience UI.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var board = await db.Boards.AsNoTracking().SingleAsync(token);
        var panellists = await db.Panellists.AsNoTracking().Where(p => admin || p.Visible)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(token);
        var questions = await db.Questions.AsNoTracking().Where(q => admin || q.Visible)
            .OrderBy(q => q.SortOrder).ThenBy(q => q.Id)
            .Select(q => new QuestionView(q.Id, q.Text, q.Category, q.Visible, q.Answered, q.SortOrder,
                db.Votes.Count(v => v.QuestionId == q.Id),
                participant != null && db.Votes.Any(v => v.QuestionId == q.Id && v.ParticipantId == participant)))
            .ToListAsync(token);
        await transaction.CommitAsync(token);
        return new(board.Title, board.Subtitle, board.VotingOpen,
            questions.Any(q => q.Id == board.CurrentQuestionId) ? board.CurrentQuestionId : null,
            board.Revision, panellists, questions);
    }

    private static async Task<IResult> Vote(Guid id, HttpContext context, FiresideDb db,
        ParticipantIdentity identities, LiveUpdates live)
    {
        var participant = identities.Read(context);
        if (participant is null) return Results.Problem("Set your name before voting.", statusCode: 401);
        var voter = EntraIdentity.VoterKey(context.User);
        if (voter is null) return Results.Problem("Sign in with an organisational Microsoft Entra account to vote.", statusCode: 401);
        var token = context.RequestAborted;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var board = await db.Boards.AsNoTracking().SingleAsync(token);
            var eligible = board.VotingOpen && await db.Questions
                .AnyAsync(q => q.Id == id && q.Visible && !q.Answered, token);
            if (!eligible) return Results.Problem("Voting is closed for this question.", statusCode: 409);
            if (await db.Votes.AnyAsync(v => v.QuestionId == id && v.ParticipantId == voter, token))
                return Results.NoContent();
            db.Votes.Add(new Vote { QuestionId = id, ParticipantId = voter.Value });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateException exception) when (IsDuplicate(exception.InnerException))
        {
            // The composite primary key is the final authority when requests arrive concurrently.
            return Results.NoContent();
        }
        catch (Exception exception) when (IsContention(exception))
        {
            return Results.Problem("Another update is in progress. Please retry your vote.", statusCode: 409);
        }
        await live.Notify();
        return Results.NoContent();
    }

    private static async Task<IResult> Mutate(HttpContext context, FiresideDb db, LiveUpdates live,
        Func<Board, Task<IResult?>> change)
    {
        if (!long.TryParse(context.Request.Headers.IfMatch.ToString().Trim('"'),
            NumberStyles.None, CultureInfo.InvariantCulture, out var expected))
            return Results.Problem("A board revision is required. Refresh and retry.", statusCode: 428);
        var board = await db.Boards.SingleAsync(context.RequestAborted);
        if (board.Revision != expected) return Conflict();
        var invalid = await change(board);
        if (invalid is not null) return invalid;
        board.Revision++;
        try { await db.SaveChangesAsync(context.RequestAborted); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        catch (Exception exception) when (IsContention(exception)) { return Conflict(); }
        await live.Notify();
        return Results.NoContent();
    }

    private static bool IsDuplicate(Exception? exception) => exception is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 } ||
        exception is SqlException { Number: 2601 or 2627 };
    private static bool IsContention(Exception exception) => exception is SqliteException { SqliteErrorCode: 5 or 6 } ||
        exception is SqlException { Number: 1205 } ||
        (exception is DbUpdateException && exception.InnerException is { } inner && IsContention(inner));
    private static IResult Invalid(string detail) => Results.Problem(detail, statusCode: 400);
    private static IResult Conflict() => Results.Problem("The board changed. Review the refreshed board and retry your edit.", statusCode: 409);
    private static bool ValidPanellist(PanellistInput input) =>
        InputRules.Text(input.Name?.Trim(), 1, 80) && InputRules.Text(input.Role?.Trim(), 0, 120) &&
        InputRules.Text(input.Bio?.Trim(), 0, 500) && input.SortOrder is >= 0 and <= 10000;
    private static bool ValidQuestion(QuestionInput input) =>
        InputRules.Text(input.Text?.Trim(), 1, 240) && InputRules.Text(input.Category?.Trim(), 0, 60) &&
        input.SortOrder is >= 0 and <= 10000;
    private static void Apply(Panellist person, PanellistInput input)
    {
        person.Name = input.Name!.Trim();
        person.Role = input.Role!.Trim();
        person.Bio = input.Bio!.Trim();
        person.Visible = input.Visible;
        person.SortOrder = input.SortOrder;
    }
    private static void Apply(Question question, QuestionInput input)
    {
        question.Text = input.Text!.Trim();
        question.Category = input.Category!.Trim();
        question.Visible = input.Visible;
        question.Answered = input.Answered;
        question.SortOrder = input.SortOrder;
    }
}
