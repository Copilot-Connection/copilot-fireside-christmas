using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Azure.Cosmos;

namespace Fireside;

public static class InputRules
{
    public static bool Text(string? value, int minimum, int maximum) =>
        value is not null && value.Length >= minimum && value.Length <= maximum && !value.Any(char.IsControl);
}

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        logger.LogError(exception, "Request failed: {Method} {Path}", context.Request.Method, context.Request.Path);
        var (detail, status) = exception switch
        {
            EventUnavailableException => ("The event store is not ready. Please retry shortly.", 503),
            BadHttpRequestException { StatusCode: 413 } => ("The request body is too large.", 413),
            CosmosException { StatusCode: HttpStatusCode.RequestEntityTooLarge } =>
                ("The event exceeds Cosmos DB's document or transaction size limit. Contact the host; this change was not saved.", 507),
            _ => ("Something went wrong. Please retry; if it continues, contact the event host.", 500)
        };
        await Results.Problem(detail, statusCode: status).ExecuteAsync(context);
        return true;
    }
}

public static class Api
{
    public static void MapFiresideApi(this WebApplication app)
    {
        app.MapGet("/api/board", async (IEventStore store, HttpContext context, TimeProvider clock) =>
        {
            var voter = AudienceAccounts.VoterKey(context.User);
            return Results.Ok(EventViews.Snapshot(await store.Read(voter, context.RequestAborted), false, clock, voter));
        });
        app.MapGet("/api/panellists/{id:guid}/photo", async (Guid id, IEventStore store, CancellationToken token) =>
            await Photo(store, id, false, token));
        app.MapGet("/api/landing-image", async (IEventStore store, CancellationToken token) =>
        {
            var data = await store.Read(null, token);
            return data.Board.LandingImageRevision is null ? Results.Redirect(LandingImages.DefaultUrl)
                : Results.Bytes(await store.Image(data, "landing", token),
                    data.Board.LandingImageContentType ?? throw new InvalidOperationException("Image content type is missing."));
        });
        app.MapPost("/api/questions/{id:guid}/vote", Vote)
            .RequireAuthorization("Voter").RequireRateLimiting("voting");

        var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");
        admin.MapGet("/board", async (IEventStore store, TimeProvider clock, CancellationToken token) =>
            Results.Ok(EventViews.Snapshot(await store.Read(null, token), true, clock)));
        admin.MapPut("/scoreboard", async (ScoreboardInput input, HttpContext context, IEventStore store, LiveUpdates live) =>
            await Mutate(context, store, live, data => { data.Board.ScoreboardVisible = input.Visible; return null; }));
        admin.MapPut("/questions/{questionId:guid}/scores/{speakerId:guid}",
            async (Guid questionId, Guid speakerId, ScoreInput input, HttpContext context,
                IEventStore store, LiveUpdates live, TimeProvider clock) =>
                await Mutate(context, store, live, data =>
                {
                    if (input.Points is < -1000000000 or > 1000000000 || !InputRules.Text(input.Reason?.Trim(), 1, 200))
                        return Invalid("Set points between -1000000000 and 1000000000 and give a reason (1-200 characters).");
                    var actor = EntraIdentity.VoterKey(context.User);
                    if (actor is null) return Results.Forbid();
                    if (!data.Questions.Any(q => q.Id == questionId) || !data.Panellists.Any(p => p.Id == speakerId))
                        return Results.NotFound();
                    var previous = data.Scores.Where(s => s.QuestionId == questionId && s.SpeakerId == speakerId)
                        .Sum(s => s.Points);
                    var delta = checked(input.Points - previous);
                    if (delta == 0) return Invalid("Enter a different total to record an adjustment.");
                    data.NewAdjustments.Add(new ScoreAdjustment { QuestionId = questionId, SpeakerId = speakerId,
                        Delta = delta, ActorId = actor.Value, ChangedAt = clock.GetUtcNow(), Reason = input.Reason!.Trim() });
                    data.AddScore(questionId, speakerId, delta);
                    return null;
                }));
        admin.MapGet("/panellists/{id:guid}/photo", async (Guid id, IEventStore store, CancellationToken token) =>
            await Photo(store, id, true, token));
        admin.MapPut("/panellists/{id:guid}/photo", async (Guid id, HttpContext context, IEventStore store, LiveUpdates live) =>
            await Upload(context, (bytes, type) => Mutate(context, store, live, data =>
            {
                var person = data.Panellists.SingleOrDefault(p => p.Id == id);
                if (person is null) return Results.NotFound();
                person.PhotoData = bytes;
                person.PhotoContentType = type;
                person.PhotoRevision = data.Board.Revision + 1;
                return null;
            })));
        admin.MapDelete("/panellists/{id:guid}/photo", async (Guid id, HttpContext context, IEventStore store, LiveUpdates live) =>
            await Mutate(context, store, live, data =>
            {
                var person = data.Panellists.SingleOrDefault(p => p.Id == id);
                if (person is null) return Results.NotFound();
                person.PhotoData = null;
                person.PhotoContentType = null;
                person.PhotoRevision = null;
                data.Images.Remove($"photo-{id:N}");
                return null;
            }));
        admin.MapPut("/timing", async (TimingInput input, HttpContext context, IEventStore store, LiveUpdates live) =>
            await Mutate(context, store, live, data =>
            {
                if (input.SpeakerSeconds is < 5 or > 3600 || input.GroupSeconds is < 5 or > 3600)
                    return Invalid("Set each timer between 5 and 3600 seconds.");
                data.Board.SpeakerSeconds = input.SpeakerSeconds;
                data.Board.GroupSeconds = input.GroupSeconds;
                return null;
            }));
        admin.MapPost("/discussion", async (DiscussionInput input, HttpContext context,
            IEventStore store, LiveUpdates live, TimeProvider clock) => await Mutate(context, store, live, data =>
        {
            var question = data.Questions.SingleOrDefault(q => q.Id == input.QuestionId && q.Visible && !q.Answered);
            if (question is null) return Invalid("Select a published, unanswered question.");
            if (!input.GroupDiscussion && !data.Panellists.Any(p => p.Id == question.SpeakerId && p.Visible))
                return Invalid("Assign a published panellist before starting their timer.");
            data.Board.CurrentQuestionId = question.Id;
            data.Board.GroupDiscussion = input.GroupDiscussion;
            data.Board.TimerStartedAt = clock.GetUtcNow();
            data.Board.TimerDurationSeconds = input.GroupDiscussion ? data.Board.GroupSeconds : data.Board.SpeakerSeconds;
            return null;
        }));
        admin.MapDelete("/discussion/timer", async (HttpContext context, IEventStore store, LiveUpdates live) =>
            await Mutate(context, store, live, data => { ResetTimer(data.Board); return null; }));
        admin.MapPut("/questions/{id:guid}/speaker", async (Guid id, SpeakerInput input,
            HttpContext context, IEventStore store, LiveUpdates live) => await Mutate(context, store, live, data =>
        {
            var question = data.Questions.SingleOrDefault(q => q.Id == id);
            if (question is null) return Results.NotFound();
            if (!question.Visible || question.Answered) return Invalid("Select a published, unanswered question.");
            if (!data.Panellists.Any(p => p.Id == input.SpeakerId && p.Visible)) return Invalid("Choose a published panellist.");
            if (data.Board.CurrentQuestionId != id || question.SpeakerId != input.SpeakerId || data.Board.GroupDiscussion)
                ResetTimer(data.Board);
            question.SpeakerId = input.SpeakerId;
            data.Board.CurrentQuestionId = id;
            data.Board.GroupDiscussion = false;
            return null;
        }));
        admin.MapPut("/landing-image", async (string? description, HttpContext context, IEventStore store, LiveUpdates live) =>
        {
            if (!InputRules.Text(description?.Trim(), 1, 300))
                return Invalid("Describe the image for people using screen readers (1-300 characters).");
            return await Upload(context, (bytes, type) => Mutate(context, store, live, data =>
            {
                data.Board.LandingImageData = bytes;
                data.Board.LandingImageContentType = type;
                data.Board.LandingImageDescription = description!.Trim();
                data.Board.LandingImageRevision = data.Board.Revision + 1;
                return null;
            }));
        });
        admin.MapDelete("/landing-image", async (HttpContext context, IEventStore store, LiveUpdates live) =>
            await Mutate(context, store, live, data =>
            {
                data.Board.LandingImageData = null;
                data.Board.LandingImageContentType = null;
                data.Board.LandingImageRevision = null;
                data.Board.LandingImageDescription = LandingImages.DefaultDescription;
                data.Images.Remove("landing");
                return null;
            }));
        admin.MapPut("/board", async (BoardInput input, HttpContext context, IEventStore store, LiveUpdates live) =>
            await Mutate(context, store, live, data =>
            {
                if (!InputRules.Text(input.Title?.Trim(), 1, 120) || !InputRules.Text(input.Subtitle?.Trim(), 0, 300))
                    return Invalid("Enter a title (1-120 characters) and subtitle (up to 300 characters).");
                if (input.CurrentQuestionId.HasValue && !data.Questions.Any(q =>
                    q.Id == input.CurrentQuestionId && q.Visible && !q.Answered))
                    return Invalid("Only a visible, unanswered question can be the current discussion.");
                data.Board.Title = input.Title!.Trim();
                data.Board.Subtitle = input.Subtitle!.Trim();
                data.Board.VotingOpen = input.VotingOpen;
                if (data.Board.CurrentQuestionId != input.CurrentQuestionId)
                {
                    data.Board.GroupDiscussion = false;
                    ResetTimer(data.Board);
                }
                data.Board.CurrentQuestionId = input.CurrentQuestionId;
                return null;
            }));
        admin.MapPost("/panellists", async (PanellistInput input, HttpContext context, IEventStore store, LiveUpdates live) =>
            await Mutate(context, store, live, data =>
            {
                if (!ValidPanellist(input)) return Invalid("Check the panellist's name, role and bio lengths.");
                var person = new Panellist();
                Apply(person, input);
                data.Panellists.Add(person);
                return null;
            }));
        admin.MapPut("/panellists/{id:guid}", async (Guid id, PanellistInput input,
            HttpContext context, IEventStore store, LiveUpdates live) => await Mutate(context, store, live, data =>
        {
            if (!ValidPanellist(input)) return Invalid("Check the panellist's name, role and bio lengths.");
            var person = data.Panellists.SingleOrDefault(p => p.Id == id);
            if (person is null) return Results.NotFound();
            Apply(person, input);
            if (!person.Visible && !data.Board.GroupDiscussion &&
                data.Questions.Any(q => q.Id == data.Board.CurrentQuestionId && q.SpeakerId == id))
                ResetTimer(data.Board);
            return null;
        }));
        admin.MapDelete("/panellists/{id:guid}", async (Guid id, HttpContext context, IEventStore store, LiveUpdates live) =>
            await Mutate(context, store, live, data =>
            {
                var person = data.Panellists.SingleOrDefault(p => p.Id == id);
                if (person is null) return Results.NotFound();
                if (!data.Board.GroupDiscussion &&
                    data.Questions.Any(q => q.Id == data.Board.CurrentQuestionId && q.SpeakerId == id))
                    ResetTimer(data.Board);
                foreach (var question in data.Questions.Where(q => q.SpeakerId == id)) question.SpeakerId = null;
                data.Orders.RemoveAll(o => o.PanellistId == id);
                data.Images.Remove($"photo-{id:N}");
                data.Panellists.Remove(person);
                return null;
            }));
        admin.MapPost("/questions", async (QuestionInput input, HttpContext context, IEventStore store, LiveUpdates live) =>
            await Mutate(context, store, live, data =>
            {
                var invalid = ValidateQuestion(data, input);
                if (invalid is not null) return invalid;
                var question = new Question();
                Apply(question, input);
                data.Questions.Add(question);
                SetSpeakerOrder(data, question.Id, input.SpeakerIds);
                return null;
            }));
        admin.MapPut("/questions/{id:guid}", async (Guid id, QuestionInput input,
            HttpContext context, IEventStore store, LiveUpdates live) => await Mutate(context, store, live, data =>
        {
            var invalid = ValidateQuestion(data, input);
            if (invalid is not null) return invalid;
            var question = data.Questions.SingleOrDefault(q => q.Id == id);
            if (question is null) return Results.NotFound();
            if (data.Board.CurrentQuestionId == id && question.SpeakerId != input.SpeakerId && !data.Board.GroupDiscussion)
                ResetTimer(data.Board);
            Apply(question, input);
            SetSpeakerOrder(data, id, input.SpeakerIds);
            if (data.Board.CurrentQuestionId == id && (!question.Visible || question.Answered))
                ClearDiscussion(data.Board);
            return null;
        }));
        admin.MapDelete("/questions/{id:guid}", async (Guid id, HttpContext context, IEventStore store, LiveUpdates live) =>
            await Mutate(context, store, live, data =>
            {
                var question = data.Questions.SingleOrDefault(q => q.Id == id);
                if (question is null) return Results.NotFound();
                if (data.Board.CurrentQuestionId == id) ClearDiscussion(data.Board);
                data.Questions.Remove(question);
                data.Orders.RemoveAll(o => o.QuestionId == id);
                data.Scores.RemoveAll(s => s.QuestionId == id);
                return null;
            }));
    }

    private static async Task<IResult> Vote(Guid id, ReactionInput input, HttpContext context,
        IEventStore store, ParticipantIdentity identities, LiveUpdates live, TimeProvider clock)
    {
        if (identities.Read(context) is null) return Results.Problem("Set your name before voting.", statusCode: 401);
        var voter = AudienceAccounts.VoterKey(context.User);
        if (voter is null) return Results.Problem("Sign in with your audience username and password to vote.", statusCode: 401);
        var delta = input.Kind switch { "like" => 1, "dislike" => -1, "epic" => 3, _ => 0 };
        if (input.RequestId == Guid.Empty || delta == 0)
            return Invalid("Choose Like the point, Don't like this point, or EPIC POINT! with a new reaction identifier.");
        var outcome = await store.Write(null, voter, input.RequestId, data =>
        {
            if (data.ExistingVote is { } existing)
                return existing.QuestionId == id && existing.ParticipantId == voter && existing.Delta == delta
                    ? Results.NoContent() : StoreResults.Conflict();
            var question = data.Questions.SingleOrDefault(q => q.Id == id && q.Visible && !q.Answered);
            if (!data.Board.VotingOpen || data.Board.GroupDiscussion || question is null)
                return Results.Problem("Voting is closed for this question.", statusCode: 409);
            var speaker = data.Board.CurrentQuestionId == id &&
                data.Panellists.Any(p => p.Id == question.SpeakerId && p.Visible) ? question.SpeakerId : null;
            var now = clock.GetUtcNow();
            if (speaker is null || data.Board.TimerStartedAt is not { } started ||
                data.Board.TimerDurationSeconds is not { } duration || now < started || now >= started.AddSeconds(duration))
                return Results.Problem("Voting is only available while the current speaker's timer is running.", statusCode: 409);
            data.NewVotes.Add(new Vote { Id = input.RequestId, QuestionId = id,
                ParticipantId = voter.Value, Delta = delta, SpeakerId = speaker });
            data.AddScore(id, speaker, delta);
            data.PersonalPoints[id] = checked(data.PersonalPoints.GetValueOrDefault(id) + delta);
            return null;
        }, context.RequestAborted);
        if (outcome.Response is not null) return outcome.Response;
        if (outcome.Committed) await live.Notify(new(input.RequestId, id, input.Kind));
        return Results.NoContent();
    }

    private static async Task<IResult> Mutate(HttpContext context, IEventStore store, LiveUpdates live,
        Func<EventData, IResult?> change)
    {
        if (!long.TryParse(context.Request.Headers.IfMatch.ToString().Trim('"'),
            NumberStyles.None, CultureInfo.InvariantCulture, out var expected))
            return Results.Problem("A board revision is required. Refresh and retry.", statusCode: 428);
        var outcome = await store.Write(expected, null, null, change, context.RequestAborted);
        if (outcome.Response is not null) return outcome.Response;
        if (outcome.Committed) await live.Notify();
        return Results.NoContent();
    }

    private static async Task<IResult> Photo(IEventStore store, Guid id, bool admin, CancellationToken token)
    {
        var data = await store.Read(null, token);
        var person = data.Panellists.SingleOrDefault(p => p.Id == id && (admin || p.Visible));
        if (person?.PhotoRevision is null) return Results.NotFound();
        return Results.Bytes(await store.Image(data, $"photo-{id:N}", token),
            person.PhotoContentType ?? throw new InvalidOperationException("Image content type is missing."));
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
        var bytes = stream.ToArray();
        var type = ImageUploads.ContentType(bytes);
        if (type is null)
            return Invalid("Choose a PNG or JPEG image, up to 8192 pixels per side and 24 megapixels. SVG and other file types are not accepted.");
        return await save(bytes, type);
    }

    private static IResult? ValidateQuestion(EventData data, QuestionInput input)
    {
        if (!InputRules.Text(input.Text?.Trim(), 1, 240) || !InputRules.Text(input.Category?.Trim(), 0, 60) ||
            input.SortOrder is < 0 or > 10000)
            return Invalid("Enter a question (1-240 characters) and category (up to 60).");
        if (input.SpeakerId.HasValue && !data.Panellists.Any(p => p.Id == input.SpeakerId))
            return Invalid("Choose an existing panellist.");
        if (input.SpeakerIds is not null &&
            (input.SpeakerIds.Distinct().Count() != input.SpeakerIds.Count ||
                !input.SpeakerIds.ToHashSet().SetEquals(data.Panellists.Select(p => p.Id))))
            return Invalid("Speaker order must include every panellist exactly once. Refresh and retry.");
        return null;
    }

    private static void SetSpeakerOrder(EventData data, Guid question, IReadOnlyList<Guid>? ids)
    {
        if (ids is null) return;
        for (var index = 0; index < ids.Count; index++)
        {
            var row = data.Orders.SingleOrDefault(o => o.QuestionId == question && o.PanellistId == ids[index]);
            if (row is null)
            {
                row = new() { QuestionId = question, PanellistId = ids[index] };
                data.Orders.Add(row);
            }
            row.SortOrder = index;
        }
    }

    private static IResult Invalid(string detail) => Results.Problem(detail, statusCode: 400);
    private static void ResetTimer(Board board) { board.TimerStartedAt = null; board.TimerDurationSeconds = null; }
    private static void ClearDiscussion(Board board)
    {
        board.CurrentQuestionId = null;
        board.GroupDiscussion = false;
        ResetTimer(board);
    }
    private static bool ValidPanellist(PanellistInput input) =>
        InputRules.Text(input.Name?.Trim(), 1, 80) && InputRules.Text(input.Role?.Trim(), 0, 120) &&
        InputRules.Text(input.Bio?.Trim(), 0, 500) && input.SortOrder is >= 0 and <= 10000;
    private static void Apply(Panellist person, PanellistInput input)
    {
        person.Name = input.Name!.Trim(); person.Role = input.Role!.Trim(); person.Bio = input.Bio!.Trim();
        person.Visible = input.Visible; person.SortOrder = input.SortOrder;
    }
    private static void Apply(Question question, QuestionInput input)
    {
        question.Text = input.Text!.Trim(); question.Category = input.Category!.Trim();
        question.Visible = input.Visible; question.Answered = input.Answered;
        question.SortOrder = input.SortOrder; question.SpeakerId = input.SpeakerId;
    }
}
