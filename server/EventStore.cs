using System.Data;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fireside;

public sealed record EventScore(Guid QuestionId, Guid? SpeakerId, long Points);

public sealed class EventData
{
    public Board Board { get; set; } = new();
    public List<Panellist> Panellists { get; set; } = [];
    public List<Question> Questions { get; set; } = [];
    public List<QuestionSpeakerOrder> Orders { get; set; } = [];
    public List<EventScore> Scores { get; set; } = [];
    public Dictionary<string, string> Images { get; set; } = [];
    [JsonIgnore] public Dictionary<Guid, long> PersonalPoints { get; set; } = [];
    [JsonIgnore] public Vote? ExistingVote { get; set; }
    [JsonIgnore] public List<Vote> NewVotes { get; } = [];
    [JsonIgnore] public List<ScoreAdjustment> NewAdjustments { get; } = [];

    public void AddScore(Guid question, Guid? speaker, long delta)
    {
        var index = Scores.FindIndex(s => s.QuestionId == question && s.SpeakerId == speaker);
        if (index < 0) Scores.Add(new(question, speaker, delta));
        else Scores[index] = Scores[index] with { Points = checked(Scores[index].Points + delta) };
    }

    public void ClearNavigations()
    {
        foreach (var question in Questions) question.Speaker = null;
        foreach (var order in Orders)
        {
            order.Question = null!;
            order.Panellist = null!;
        }
    }
}

public sealed record EventWrite(IResult? Response, bool Committed);

public interface IEventStore
{
    Task<EventData> Read(Guid? voter, CancellationToken token);
    Task<EventWrite> Write(long? revision, Guid? voter, Guid? request,
        Func<EventData, IResult?> change, CancellationToken token);
    Task<byte[]> Image(EventData data, string key, CancellationToken token);
}

public static class StoreResults
{
    public static IResult Conflict() =>
        Results.Problem("The board changed. Review the refreshed board and retry your edit.", statusCode: 409);
}

public sealed class SqliteEventStore(FiresideDb db) : IEventStore
{
    public async Task<EventData> Read(Guid? voter, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var data = await Load(voter, false, token);
        await transaction.CommitAsync(token);
        return data;
    }

    public async Task<EventWrite> Write(long? revision, Guid? voter, Guid? request,
        Func<EventData, IResult?> change, CancellationToken token)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var data = await Load(voter, true, token);
            if (revision.HasValue && data.Board.Revision != revision) return new(StoreResults.Conflict(), false);
            if (request.HasValue) data.ExistingVote = await db.Votes.AsNoTracking()
                .SingleOrDefaultAsync(v => v.Id == request, token);
            var people = data.Panellists.ToArray();
            var questions = data.Questions.ToArray();
            var orders = data.Orders.ToArray();
            var response = change(data);
            if (response is not null) return new(response, false);
            if (revision.HasValue) data.Board.Revision++;
            Sync(db.QuestionSpeakerOrders, orders, data.Orders);
            Sync(db.Questions, questions, data.Questions);
            Sync(db.Panellists, people, data.Panellists);
            db.Votes.AddRange(data.NewVotes);
            db.ScoreAdjustments.AddRange(data.NewAdjustments);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(null, true);
        }
        catch (DbUpdateConcurrencyException) { return new(StoreResults.Conflict(), false); }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        { return new(StoreResults.Conflict(), false); }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException { SqliteErrorCode: 5 or 6 })
        { return new(StoreResults.Conflict(), false); }
    }

    private static void Sync<T>(DbSet<T> set, IReadOnlyList<T> before, IReadOnlyList<T> after) where T : class
    {
        set.RemoveRange(before.Except(after));
        set.AddRange(after.Except(before));
    }

    internal async Task<EventData> Load(Guid? voter, bool tracking, CancellationToken token)
    {
        IQueryable<T> Query<T>(DbSet<T> set) where T : class => tracking ? set : set.AsNoTracking();
        var data = new EventData
        {
            Board = await Query(db.Boards).SingleAsync(token),
            Panellists = await Query(db.Panellists).OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(token),
            Questions = await Query(db.Questions).OrderBy(q => q.SortOrder).ThenBy(q => q.Id).ToListAsync(token),
            Orders = await Query(db.QuestionSpeakerOrders).OrderBy(o => o.SortOrder).ThenBy(o => o.PanellistId).ToListAsync(token)
        };
        var epoch = data.Board.ScoreEpoch;
        var votes = await db.Votes.AsNoTracking().Where(v => v.ScoreEpoch == epoch).GroupBy(v => new { v.QuestionId, v.SpeakerId })
            .Select(g => new { g.Key.QuestionId, g.Key.SpeakerId, Points = g.Sum(v => (long)v.Delta) }).ToListAsync(token);
        var adjustments = await db.ScoreAdjustments.AsNoTracking().Where(v => v.ScoreEpoch == epoch).GroupBy(v => new { v.QuestionId, v.SpeakerId })
            .Select(g => new { g.Key.QuestionId, g.Key.SpeakerId, Points = g.Sum(v => v.Delta) }).ToListAsync(token);
        foreach (var score in votes) data.AddScore(score.QuestionId, score.SpeakerId, score.Points);
        foreach (var score in adjustments) data.AddScore(score.QuestionId, score.SpeakerId, score.Points);
        if (voter.HasValue)
            data.PersonalPoints = await db.Votes.AsNoTracking().Where(v => v.ParticipantId == voter && v.ScoreEpoch == epoch)
                .GroupBy(v => v.QuestionId).Select(g => new { Id = g.Key, Points = g.Sum(v => (long)v.Delta) })
                .ToDictionaryAsync(v => v.Id, v => v.Points, token);
        return data;
    }

    public Task<byte[]> Image(EventData data, string key, CancellationToken token) =>
        Task.FromResult(key == "landing"
            ? data.Board.LandingImageData ?? throw new InvalidOperationException("Landing image data is missing.")
            : data.Panellists.Single(p => $"photo-{p.Id:N}" == key).PhotoData
                ?? throw new InvalidOperationException("Panellist image data is missing."));
}

public static class EventViews
{
    public static BoardView Snapshot(EventData data, bool admin, TimeProvider clock, Guid? voter = null)
    {
        var board = data.Board;
        var people = data.Panellists.Where(p => admin || p.Visible).OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new PanellistView(p.Id, p.Name, p.Role, p.Bio, p.Visible, p.SortOrder,
                p.PhotoRevision is null ? null : $"{(admin ? "/api/admin" : "/api")}/panellists/{p.Id}/photo?v={p.PhotoRevision}",
                p.Type)).ToArray();
        var visible = people.Select(p => p.Id).ToHashSet();
        var questions = data.Questions.Where(q => admin || q.Visible).OrderBy(q => q.SortOrder).ThenBy(q => q.Id)
            .Select(q => new QuestionView(q.Id, q.Text, q.Category, q.Visible, q.Answered, q.SortOrder,
                data.Scores.Where(s => s.QuestionId == q.Id).Sum(s => s.Points),
                voter.HasValue ? data.PersonalPoints.GetValueOrDefault(q.Id) : null,
                admin || q.SpeakerId.HasValue && visible.Contains(q.SpeakerId.Value) ? q.SpeakerId : null)
            {
                SpeakerIds = data.Orders.Where(o => o.QuestionId == q.Id && visible.Contains(o.PanellistId))
                    .OrderBy(o => o.SortOrder).ThenBy(o => o.PanellistId).Select(o => o.PanellistId)
                    .Concat(people.Select(p => p.Id)).Distinct().ToArray()
            }).ToArray();
        ScoreboardView? scoreboard = null;
        if (admin || board.ScoreboardVisible)
        {
            var speakers = people.Where(p => p.Type == "guest")
                .Select(p => (p.Id, p.Name, Removed: false)).ToList();
            var rows = questions.Select(q => new QuestionScores(q.Id, q.Text,
                speakers.Select(p => new SpeakerPoints(p.Id,
                    data.Scores.Where(s => s.QuestionId == q.Id && s.SpeakerId == p.Id).Sum(s => s.Points))).ToArray(),
                data.Scores.Where(s => s.QuestionId == q.Id && s.SpeakerId is null).Sum(s => s.Points))).ToArray();
            scoreboard = new(speakers.Select(p => new SpeakerTotal(p.Id, p.Name,
                rows.Sum(q => q.Scores.Single(s => s.SpeakerId == p.Id).Points), p.Removed))
                .OrderByDescending(p => p.Points).ThenBy(p => p.Name).ThenBy(p => p.Id).ToArray(), rows);
        }
        return new(board.Title, board.Subtitle, board.VotingOpen,
            questions.Any(q => q.Id == board.CurrentQuestionId) ? board.CurrentQuestionId : null,
            board.Revision, people, questions,
            board.LandingImageRevision is { } version ? $"/api/landing-image?v={version}" : LandingImages.DefaultUrl,
            board.LandingImageDescription, board.LandingImageRevision is not null,
            board.SpeakerSeconds, board.GroupSeconds, board.GroupDiscussion, board.TimerStartedAt,
            board.TimerDurationSeconds, clock.GetUtcNow(), board.ScoreboardVisible, scoreboard);
    }
}
