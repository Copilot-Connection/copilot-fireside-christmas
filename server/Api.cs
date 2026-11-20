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
        app.MapGet("/api/board", async (FiresideDb db, HttpContext context, TimeProvider clock) =>
            Results.Ok(await Snapshot(db, false, context.RequestAborted, clock, EntraIdentity.VoterKey(context.User))));
        app.MapGet("/api/panellists/{id:guid}/photo", async (Guid id, FiresideDb db, CancellationToken token) =>
            await Photo(db, id, false, token));
        app.MapGet("/api/landing-image", async (FiresideDb db, CancellationToken token) =>
        {
            var image = await db.Boards.AsNoTracking().Select(b => new { b.LandingImageData, b.LandingImageContentType })
                .SingleAsync(token);
            return image.LandingImageData is { } data && image.LandingImageContentType is { } type
                ? Results.Bytes(data, type) : Results.Redirect(LandingImages.DefaultUrl);
        });
        app.MapPost("/api/questions/{id:guid}/vote", Vote)
            .RequireAuthorization("Voter").RequireRateLimiting("voting");

        var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");
        admin.MapPut("/scoreboard", async (ScoreboardInput input, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, board =>
            {
                board.ScoreboardVisible = input.Visible;
                return Task.FromResult<IResult?>(null);
            }));
        admin.MapPut("/questions/{questionId:guid}/scores/{speakerId:guid}",
            async (Guid questionId, Guid speakerId, ScoreInput input, HttpContext context, FiresideDb db, LiveUpdates live, TimeProvider clock) =>
            await Mutate(context, db, live, async _ =>
            {
                if (input.Points is < -1000000000 or > 1000000000 || !InputRules.Text(input.Reason?.Trim(), 1, 200))
                    return Invalid("Set points between -1000000000 and 1000000000 and give a reason (1-200 characters).");
                var actor = EntraIdentity.VoterKey(context.User);
                if (actor is null) return Results.Forbid();
                if (!await db.Questions.AnyAsync(q => q.Id == questionId, context.RequestAborted) ||
                    !await db.Panellists.AnyAsync(p => p.Id == speakerId, context.RequestAborted))
                    return Results.NotFound();
                var reactions = await db.Votes.Where(v => v.QuestionId == questionId && v.SpeakerId == speakerId)
                    .SumAsync(v => (long?)v.Delta, context.RequestAborted) ?? 0;
                var adjustments = await db.ScoreAdjustments.Where(s => s.QuestionId == questionId && s.SpeakerId == speakerId)
                    .SumAsync(s => (long?)s.Delta, context.RequestAborted) ?? 0;
                var delta = input.Points - reactions - adjustments;
                if (delta == 0) return Invalid("Enter a different total to record an adjustment.");
                db.ScoreAdjustments.Add(new ScoreAdjustment { QuestionId = questionId, SpeakerId = speakerId,
                    Delta = delta, ActorId = actor.Value, ChangedAt = clock.GetUtcNow(), Reason = input.Reason!.Trim() });
                return null;
            }, serializable: true));
        admin.MapGet("/board", async (FiresideDb db, TimeProvider clock, CancellationToken token) =>
            Results.Ok(await Snapshot(db, true, token, clock)));
        admin.MapGet("/panellists/{id:guid}/photo", async (Guid id, FiresideDb db, CancellationToken token) =>
            await Photo(db, id, true, token));
        admin.MapPut("/panellists/{id:guid}/photo", async (Guid id, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Upload(context, (data, type) => Mutate(context, db, live, async board =>
            {
                var person = await db.Panellists.FindAsync([id], context.RequestAborted);
                if (person is null) return Results.NotFound();
                person.PhotoData = data;
                person.PhotoContentType = type;
                person.PhotoRevision = board.Revision + 1;
                return null;
            })));
        admin.MapDelete("/panellists/{id:guid}/photo", async (Guid id, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async _ =>
            {
                var person = await db.Panellists.FindAsync([id], context.RequestAborted);
                if (person is null) return Results.NotFound();
                person.PhotoData = null;
                person.PhotoContentType = null;
                person.PhotoRevision = null;
                return null;
            }));
        admin.MapPut("/timing", async (TimingInput input, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, board =>
            {
                if (input.SpeakerSeconds is < 5 or > 3600 || input.GroupSeconds is < 5 or > 3600)
                    return Task.FromResult<IResult?>(Invalid("Set each timer between 5 and 3600 seconds."));
                board.SpeakerSeconds = input.SpeakerSeconds;
                board.GroupSeconds = input.GroupSeconds;
                return Task.FromResult<IResult?>(null);
            }));
        admin.MapPost("/discussion", async (DiscussionInput input, HttpContext context, FiresideDb db, LiveUpdates live, TimeProvider clock) =>
            await Mutate(context, db, live, async board =>
            {
                var question = await db.Questions.AsNoTracking().Where(q => q.Id == input.QuestionId && q.Visible && !q.Answered)
                    .Select(q => new { SpeakerAvailable = q.Speaker != null && q.Speaker.Visible }).SingleOrDefaultAsync(context.RequestAborted);
                if (question is null) return Invalid("Select a published, unanswered question.");
                if (!input.GroupDiscussion && !question.SpeakerAvailable)
                    return Invalid("Assign a published panellist before starting their timer.");
                board.CurrentQuestionId = input.QuestionId;
                board.GroupDiscussion = input.GroupDiscussion;
                board.TimerStartedAt = clock.GetUtcNow();
                board.TimerDurationSeconds = input.GroupDiscussion ? board.GroupSeconds : board.SpeakerSeconds;
                return null;
            }));
        admin.MapDelete("/discussion/timer", async (HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, board =>
            {
                ResetTimer(board);
                return Task.FromResult<IResult?>(null);
            }));
        admin.MapPut("/questions/{id:guid}/speaker", async (Guid id, SpeakerInput input, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async board =>
            {
                var question = await db.Questions.FindAsync([id], context.RequestAborted);
                if (question is null) return Results.NotFound();
                if (!question.Visible || question.Answered) return Invalid("Select a published, unanswered question.");
                if (!await db.Panellists.AnyAsync(p => p.Id == input.SpeakerId && p.Visible, context.RequestAborted))
                    return Invalid("Choose a published panellist.");
                if (board.CurrentQuestionId != id || question.SpeakerId != input.SpeakerId || board.GroupDiscussion)
                    ResetTimer(board);
                question.SpeakerId = input.SpeakerId;
                board.CurrentQuestionId = id;
                board.GroupDiscussion = false;
                return null;
            }));
        admin.MapPut("/landing-image", async (string? description, HttpContext context, FiresideDb db, LiveUpdates live) =>
        {
            if (!InputRules.Text(description?.Trim(), 1, 300))
                return Invalid("Describe the image for people using screen readers (1-300 characters).");
            return await Upload(context, (data, type) => Mutate(context, db, live, board =>
            {
                board.LandingImageData = data;
                board.LandingImageContentType = type;
                board.LandingImageDescription = description!.Trim();
                board.LandingImageRevision = board.Revision + 1;
                return Task.FromResult<IResult?>(null);
            }));
        });
        admin.MapDelete("/landing-image", async (HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, board =>
            {
                board.LandingImageData = null;
                board.LandingImageContentType = null;
                board.LandingImageRevision = null;
                board.LandingImageDescription = LandingImages.DefaultDescription;
                return Task.FromResult<IResult?>(null);
            }));
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
                if (board.CurrentQuestionId != input.CurrentQuestionId)
                {
                    board.GroupDiscussion = false;
                    ResetTimer(board);
                }
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
            await Mutate(context, db, live, async board =>
            {
                if (!ValidPanellist(input)) return Invalid("Check the panellist's name, role and bio lengths.");
                var person = await db.Panellists.FindAsync([id], context.RequestAborted);
                if (person is null) return Results.NotFound();
                Apply(person, input);
                if (!person.Visible && !board.GroupDiscussion && await db.Questions.AnyAsync(
                    q => q.Id == board.CurrentQuestionId && q.SpeakerId == id, context.RequestAborted))
                    ResetTimer(board);
                return null;
            }));
        admin.MapDelete("/panellists/{id:guid}", async (Guid id, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async board =>
            {
                var person = await db.Panellists.FindAsync([id], context.RequestAborted);
                if (person is null) return Results.NotFound();
                if (!board.GroupDiscussion && await db.Questions.AnyAsync(
                    q => q.Id == board.CurrentQuestionId && q.SpeakerId == id, context.RequestAborted))
                    ResetTimer(board);
                db.QuestionSpeakerOrders.RemoveRange(await db.QuestionSpeakerOrders
                    .Where(s => s.PanellistId == id).ToListAsync(context.RequestAborted));
                db.Panellists.Remove(person);
                return null;
            }));
        admin.MapPost("/questions", async (QuestionInput input, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async _ =>
            {
                if (!ValidQuestion(input)) return Invalid("Enter a question (1-240 characters) and category (up to 60).");
                if (!await SpeakerExists(db, input.SpeakerId, context.RequestAborted)) return Invalid("Choose an existing panellist.");
                if (!await ValidSpeakerOrder(db, input.SpeakerIds, context.RequestAborted))
                    return Invalid("Speaker order must include every panellist exactly once. Refresh and retry.");
                var question = new Question();
                Apply(question, input);
                db.Questions.Add(question);
                await SetSpeakerOrder(db, question.Id, input.SpeakerIds, context.RequestAborted);
                return null;
            }));
        admin.MapPut("/questions/{id:guid}", async (Guid id, QuestionInput input, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async board =>
            {
                if (!ValidQuestion(input)) return Invalid("Enter a question (1-240 characters) and category (up to 60).");
                if (!await SpeakerExists(db, input.SpeakerId, context.RequestAborted)) return Invalid("Choose an existing panellist.");
                if (!await ValidSpeakerOrder(db, input.SpeakerIds, context.RequestAborted))
                    return Invalid("Speaker order must include every panellist exactly once. Refresh and retry.");
                var question = await db.Questions.FindAsync([id], context.RequestAborted);
                if (question is null) return Results.NotFound();
                if (board.CurrentQuestionId == id && question.SpeakerId != input.SpeakerId && !board.GroupDiscussion)
                    ResetTimer(board);
                Apply(question, input);
                await SetSpeakerOrder(db, id, input.SpeakerIds, context.RequestAborted);
                if (board.CurrentQuestionId == id && (!question.Visible || question.Answered))
                    ClearDiscussion(board);
                return null;
            }));
        admin.MapDelete("/questions/{id:guid}", async (Guid id, HttpContext context, FiresideDb db, LiveUpdates live) =>
            await Mutate(context, db, live, async board =>
            {
                var question = await db.Questions.FindAsync([id], context.RequestAborted);
                if (question is null) return Results.NotFound();
                if (board.CurrentQuestionId == id) ClearDiscussion(board);
                db.Questions.Remove(question);
                return null;
            }));
    }

    public static async Task<BoardView> Snapshot(FiresideDb db, bool admin, CancellationToken token, TimeProvider clock, Guid? voter = null)
    {
        // A consistent snapshot prevents half-applied host changes appearing in the audience UI.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var board = await db.Boards.AsNoTracking().Select(b => new
        {
            b.Title, b.Subtitle, b.VotingOpen, b.CurrentQuestionId, b.Revision,
            b.LandingImageRevision, b.LandingImageDescription, b.SpeakerSeconds, b.GroupSeconds,
            b.GroupDiscussion, b.TimerStartedAt, b.TimerDurationSeconds, b.ScoreboardVisible
        }).SingleAsync(token);
        var panellists = await db.Panellists.AsNoTracking().Where(p => admin || p.Visible)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new PanellistView(p.Id, p.Name, p.Role, p.Bio, p.Visible, p.SortOrder,
                p.PhotoRevision == null ? null : (admin ? "/api/admin" : "/api") + "/panellists/" + p.Id + "/photo?v=" + p.PhotoRevision))
            .ToListAsync(token);
        var questions = await db.Questions.AsNoTracking().Where(q => admin || q.Visible)
            .OrderBy(q => q.SortOrder).ThenBy(q => q.Id)
            .Select(q => new QuestionView(q.Id, q.Text, q.Category, q.Visible, q.Answered, q.SortOrder,
                (db.Votes.Where(v => v.QuestionId == q.Id).Sum(v => (long?)v.Delta) ?? 0) +
                    (db.ScoreAdjustments.Where(s => s.QuestionId == q.Id).Sum(s => (long?)s.Delta) ?? 0),
                voter == null ? null : db.Votes.Where(v => v.QuestionId == q.Id && v.ParticipantId == voter)
                    .Sum(v => (long?)v.Delta) ?? 0,
                admin || q.Speaker != null && q.Speaker.Visible ? q.SpeakerId : null))
            .ToListAsync(token);
        var orders = await db.QuestionSpeakerOrders.AsNoTracking().Where(s => admin || s.Question.Visible)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.PanellistId).ToListAsync(token);
        var visiblePeople = panellists.Select(p => p.Id).ToHashSet();
        questions = questions.Select(q => q with
        {
            SpeakerIds = orders.Where(s => s.QuestionId == q.Id && visiblePeople.Contains(s.PanellistId))
                .Select(s => s.PanellistId).Concat(panellists.Select(p => p.Id)).Distinct().ToArray()
        }).ToList();
        ScoreboardView? scoreboard = null;
        if (admin || board.ScoreboardVisible)
        {
            var votes = await db.Votes.AsNoTracking().Where(v => admin || v.Question.Visible)
                .GroupBy(v => new { v.QuestionId, v.SpeakerId })
                .Select(g => new { g.Key.QuestionId, g.Key.SpeakerId, Points = g.Sum(v => (long)v.Delta) }).ToListAsync(token);
            var adjustments = await db.ScoreAdjustments.AsNoTracking().Where(s => admin || s.Question.Visible)
                .GroupBy(s => new { s.QuestionId, s.SpeakerId })
                .Select(g => new { g.Key.QuestionId, g.Key.SpeakerId, Points = g.Sum(s => s.Delta) }).ToListAsync(token);
            var people = panellists.Select(p => (p.Id, p.Name, Removed: false)).ToList();
            if (admin)
                people.AddRange(votes.Where(v => v.SpeakerId.HasValue).Select(v => v.SpeakerId!.Value)
                    .Concat(adjustments.Select(s => s.SpeakerId)).Distinct().Where(id => !visiblePeople.Contains(id))
                    .Select(id => (id, "Removed speaker", true)));
            var totals = votes.Where(v => v.SpeakerId.HasValue).ToDictionary(v => (v.QuestionId, v.SpeakerId!.Value), v => v.Points);
            foreach (var adjustment in adjustments)
                totals[(adjustment.QuestionId, adjustment.SpeakerId)] =
                    totals.GetValueOrDefault((adjustment.QuestionId, adjustment.SpeakerId)) + adjustment.Points;
            var rows = questions.Select(q => new QuestionScores(q.Id, q.Text,
                people.Select(p => new SpeakerPoints(p.Id, totals.GetValueOrDefault((q.Id, p.Id)))).ToArray(),
                votes.Where(v => v.QuestionId == q.Id && v.SpeakerId == null).Sum(v => v.Points))).ToArray();
            scoreboard = new(people.Select(p => new SpeakerTotal(p.Id, p.Name,
                rows.Sum(q => q.Scores.Single(s => s.SpeakerId == p.Id).Points), p.Removed))
                .OrderByDescending(p => p.Points).ThenBy(p => p.Name).ThenBy(p => p.Id).ToArray(), rows);
        }
        await transaction.CommitAsync(token);
        return new(board.Title, board.Subtitle, board.VotingOpen,
            questions.Any(q => q.Id == board.CurrentQuestionId) ? board.CurrentQuestionId : null,
            board.Revision, panellists, questions,
            board.LandingImageRevision is { } version ? $"/api/landing-image?v={version}" : LandingImages.DefaultUrl,
            board.LandingImageDescription, board.LandingImageRevision is not null,
            board.SpeakerSeconds, board.GroupSeconds, board.GroupDiscussion,
            board.TimerStartedAt, board.TimerDurationSeconds, clock.GetUtcNow(), board.ScoreboardVisible, scoreboard);
    }

    private static async Task<IResult> Vote(Guid id, ReactionInput input, HttpContext context, FiresideDb db,
        ParticipantIdentity identities, LiveUpdates live)
    {
        var participant = identities.Read(context);
        if (participant is null) return Results.Problem("Set your name before voting.", statusCode: 401);
        var voter = EntraIdentity.VoterKey(context.User);
        if (voter is null) return Results.Problem("Sign in with an organisational Microsoft Entra account to vote.", statusCode: 401);
        var delta = input.Kind switch { "like" => 1, "dislike" => -1, "epic" => 3, _ => 0 };
        if (input.RequestId == Guid.Empty || delta == 0) return Invalid("Choose Like the point, Don't like this point, or EPIC POINT! with a new reaction identifier.");
        var token = context.RequestAborted;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var existing = await db.Votes.AsNoTracking().SingleOrDefaultAsync(v => v.Id == input.RequestId, token);
            if (existing is not null) return SameReaction(existing, id, voter.Value, delta)
                ? Results.NoContent() : Conflict();
            var board = await db.Boards.AsNoTracking().Select(b => new { b.VotingOpen, b.GroupDiscussion, b.CurrentQuestionId }).SingleAsync(token);
            var question = await db.Questions.Where(q => q.Id == id && q.Visible && !q.Answered)
                .Select(q => new { q.SpeakerId, SpeakerVisible = q.Speaker != null && q.Speaker.Visible }).SingleOrDefaultAsync(token);
            if (!board.VotingOpen || board.GroupDiscussion || question is null)
                return Results.Problem("Voting is closed for this question.", statusCode: 409);
            db.Votes.Add(new Vote { Id = input.RequestId, QuestionId = id, ParticipantId = voter.Value, Delta = delta,
                SpeakerId = board.CurrentQuestionId == id && question.SpeakerVisible ? question.SpeakerId : null });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateException exception) when (IsDuplicate(exception.InnerException))
        {
            var existing = await db.Votes.AsNoTracking().SingleOrDefaultAsync(v => v.Id == input.RequestId, token);
            return existing is not null && SameReaction(existing, id, voter.Value, delta) ? Results.NoContent() : Conflict();
        }
        catch (Exception exception) when (IsContention(exception))
        {
            return Results.Problem("Another update is in progress. Please retry your vote.", statusCode: 409);
        }
        await live.Notify(new LiveReaction(input.RequestId, id, input.Kind));
        return Results.NoContent();
    }

    private static async Task<IResult> Photo(FiresideDb db, Guid id, bool admin, CancellationToken token)
    {
        var image = await db.Panellists.AsNoTracking().Where(p => p.Id == id && (admin || p.Visible))
            .Select(p => new { p.PhotoData, p.PhotoContentType }).SingleOrDefaultAsync(token);
        if (image?.PhotoData is not { } data || image.PhotoContentType is not { } type) return Results.NotFound();
        return Results.Bytes(data, type);
    }

    private static async Task<IResult> Upload(HttpContext context, Func<byte[], string, Task<IResult>> save)
    {
        if (context.Request.ContentLength > ImageUploads.MaximumBytes)
            return Results.Problem("Choose a PNG or JPEG image no larger than 2 MB.", statusCode: 413);
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            if (stream.Length + count > ImageUploads.MaximumBytes)
                return Results.Problem("Choose a PNG or JPEG image no larger than 2 MB.", statusCode: 413);
            await stream.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
        }
        var data = stream.ToArray();
        var type = ImageUploads.ContentType(data);
        if (type is null) return Invalid("Choose a PNG or JPEG image, up to 8192 pixels per side and 24 megapixels. SVG and other file types are not accepted.");
        return await save(data, type);
    }

    private static Task<bool> SpeakerExists(FiresideDb db, Guid? id, CancellationToken token) =>
        id is { } speaker ? db.Panellists.AnyAsync(p => p.Id == speaker, token) : Task.FromResult(true);

    private static async Task<bool> ValidSpeakerOrder(FiresideDb db, IReadOnlyList<Guid>? ids, CancellationToken token)
    {
        if (ids is null) return true;
        if (ids.Distinct().Count() != ids.Count) return false;
        var people = await db.Panellists.Select(p => p.Id).ToListAsync(token);
        return ids.Count == people.Count && ids.ToHashSet().SetEquals(people);
    }

    private static async Task SetSpeakerOrder(FiresideDb db, Guid question, IReadOnlyList<Guid>? ids, CancellationToken token)
    {
        if (ids is null) return;
        var existing = await db.QuestionSpeakerOrders.Where(s => s.QuestionId == question).ToListAsync(token);
        for (var index = 0; index < ids.Count; index++)
        {
            var row = existing.SingleOrDefault(s => s.PanellistId == ids[index]);
            if (row is null)
            {
                row = new QuestionSpeakerOrder { QuestionId = question, PanellistId = ids[index] };
                db.QuestionSpeakerOrders.Add(row);
            }
            row.SortOrder = index;
        }
    }

    private static void ResetTimer(Board board)
    {
        board.TimerStartedAt = null;
        board.TimerDurationSeconds = null;
    }

    private static void ClearDiscussion(Board board)
    {
        board.CurrentQuestionId = null;
        board.GroupDiscussion = false;
        ResetTimer(board);
    }

    private static bool SameReaction(Vote vote, Guid question, Guid voter, int delta) =>
        vote.QuestionId == question && vote.ParticipantId == voter && vote.Delta == delta;

    private static async Task<IResult> Mutate(HttpContext context, FiresideDb db, LiveUpdates live,
        Func<Board, Task<IResult?>> change, bool serializable = false)
    {
        if (!long.TryParse(context.Request.Headers.IfMatch.ToString().Trim('"'),
            NumberStyles.None, CultureInfo.InvariantCulture, out var expected))
            return Results.Problem("A board revision is required. Refresh and retry.", statusCode: 428);
        try
        {
            await using var transaction = serializable
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, context.RequestAborted) : null;
            var board = await db.Boards.SingleAsync(context.RequestAborted);
            if (board.Revision != expected) return Conflict();
            var invalid = await change(board);
            if (invalid is not null) return invalid;
            board.Revision++;
            await db.SaveChangesAsync(context.RequestAborted);
            if (transaction is not null) await transaction.CommitAsync(context.RequestAborted);
        }
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
        question.SpeakerId = input.SpeakerId;
    }
}
