using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace Fireside;

public sealed class Board
{
    public int Id { get; set; } = 1;
    [MaxLength(120)] public string Title { get; set; } = "Copilot Fireside Chat";
    [MaxLength(300)] public string Subtitle { get; set; } = "A little warmth. A lot of possibilities.";
    public bool VotingOpen { get; set; }
    public Guid? CurrentQuestionId { get; set; }
    public long Revision { get; set; }
    public byte[]? LandingImageData { get; set; }
    [MaxLength(32)] public string? LandingImageContentType { get; set; }
    public long? LandingImageRevision { get; set; }
    [MaxLength(300)] public string LandingImageDescription { get; set; } = LandingImages.DefaultDescription;
    public int SpeakerSeconds { get; set; } = 120;
    public int GroupSeconds { get; set; } = 300;
    public bool GroupDiscussion { get; set; }
    public DateTimeOffset? TimerStartedAt { get; set; }
    public int? TimerDurationSeconds { get; set; }
    public bool ScoreboardVisible { get; set; }
    public DateTimeOffset? ScoreboardInitializedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Panellist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(120)] public string Role { get; set; } = "";
    [MaxLength(500)] public string Bio { get; set; } = "";
    public bool Visible { get; set; }
    public int SortOrder { get; set; }
    public byte[]? PhotoData { get; set; }
    [MaxLength(32)] public string? PhotoContentType { get; set; }
    public long? PhotoRevision { get; set; }
}

public sealed class Question
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(240)] public string Text { get; set; } = "";
    [MaxLength(60)] public string Category { get; set; } = "";
    public bool Visible { get; set; }
    public bool Answered { get; set; }
    public int SortOrder { get; set; }
    public Guid? SpeakerId { get; set; }
    public Panellist? Speaker { get; set; }
}

public sealed class Vote
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public Guid ParticipantId { get; set; }
    public int Delta { get; set; }
    // Attribution must survive switching or deleting the selected panellist.
    public Guid? SpeakerId { get; set; }
    public Question Question { get; set; } = null!;
}

public sealed class ScoreAdjustment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuestionId { get; set; }
    public Guid SpeakerId { get; set; }
    public long Delta { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    [MaxLength(200)] public string Reason { get; set; } = "";
    public Question Question { get; set; } = null!;
}

public sealed class QuestionSpeakerOrder
{
    public Guid QuestionId { get; set; }
    public Guid PanellistId { get; set; }
    public int SortOrder { get; set; }
    public Question Question { get; set; } = null!;
    public Panellist Panellist { get; set; } = null!;
}

public sealed class FiresideDb(DbContextOptions<FiresideDb> options) : DbContext(options)
{
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Panellist> Panellists => Set<Panellist>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<QuestionSpeakerOrder> QuestionSpeakerOrders => Set<QuestionSpeakerOrder>();
    public DbSet<ScoreAdjustment> ScoreAdjustments => Set<ScoreAdjustment>();
    public DbSet<AudienceAccount> AudienceAccounts => Set<AudienceAccount>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Board>().Property(b => b.Revision).IsConcurrencyToken();
        model.Entity<AudienceAccount>().HasKey(a => a.Id);
        model.Entity<AudienceAccount>().HasIndex(a => a.UserId).IsUnique();
        model.Entity<AudienceAccount>().Property(a => a.Version).IsConcurrencyToken();
        model.Entity<Board>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_Boards_Durations", "[SpeakerSeconds] BETWEEN 5 AND 3600 AND [GroupSeconds] BETWEEN 5 AND 3600");
            t.HasCheckConstraint("CK_Boards_Timer", "([TimerStartedAt] IS NULL AND [TimerDurationSeconds] IS NULL) OR ([TimerStartedAt] IS NOT NULL AND [TimerDurationSeconds] IS NOT NULL AND [TimerDurationSeconds] BETWEEN 5 AND 3600)");
        });
        model.Entity<Question>().HasOne(q => q.Speaker).WithMany().HasForeignKey(q => q.SpeakerId)
            .OnDelete(DeleteBehavior.SetNull);
        model.Entity<QuestionSpeakerOrder>().HasKey(s => new { s.QuestionId, s.PanellistId });
        model.Entity<QuestionSpeakerOrder>().HasOne(s => s.Question).WithMany().HasForeignKey(s => s.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
        model.Entity<QuestionSpeakerOrder>().HasOne(s => s.Panellist).WithMany().HasForeignKey(s => s.PanellistId)
            .OnDelete(DeleteBehavior.NoAction);
        model.Entity<QuestionSpeakerOrder>().ToTable(t => t.HasCheckConstraint("CK_QuestionSpeakerOrders_Order", "[SortOrder] >= 0"));
        model.Entity<Vote>().HasKey(v => v.Id);
        model.Entity<Vote>().HasIndex(v => new { v.QuestionId, v.SpeakerId });
        model.Entity<Vote>().ToTable(t => t.HasCheckConstraint("CK_Votes_Delta", "[Delta] IN (-1, 1, 3)"));
        model.Entity<Vote>().HasOne(v => v.Question).WithMany().HasForeignKey(v => v.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
        model.Entity<ScoreAdjustment>().HasIndex(s => new { s.QuestionId, s.SpeakerId });
        model.Entity<ScoreAdjustment>().HasOne(s => s.Question).WithMany().HasForeignKey(s => s.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
        model.Entity<ScoreAdjustment>().ToTable(t => t.HasCheckConstraint("CK_ScoreAdjustments_Delta", "[Delta] <> 0"));
    }
}

public sealed record Participant(string Name, DateTimeOffset Expires, Guid Id = default);
public sealed record NameInput(string? Name);
public sealed record BoardInput(string? Title, string? Subtitle, bool VotingOpen, Guid? CurrentQuestionId);
public sealed record PanellistInput(string? Name, string? Role, string? Bio, bool Visible, int SortOrder);
public sealed record QuestionInput(string? Text, string? Category, bool Visible, bool Answered, int SortOrder,
    Guid? SpeakerId = null, IReadOnlyList<Guid>? SpeakerIds = null);
public sealed record SpeakerInput(Guid SpeakerId);
public sealed record ScoreInput(long Points, string? Reason);
public sealed record ScoreboardInput(bool Visible);
public sealed record TimingInput(int SpeakerSeconds, int GroupSeconds);
public sealed record DiscussionInput(Guid QuestionId, bool GroupDiscussion);
public sealed record ReactionInput(Guid RequestId, string Kind);
public sealed record LiveReaction(Guid Id, Guid QuestionId, string Kind);
public sealed record QuestionView(Guid Id, string Text, string Category, bool Visible, bool Answered,
    int SortOrder, long Score, long? MyScore, Guid? SpeakerId)
{
    public IReadOnlyList<Guid> SpeakerIds { get; init; } = [];
}
public sealed record PanellistView(Guid Id, string Name, string Role, string Bio, bool Visible, int SortOrder, string? PhotoUrl);
public sealed record BoardView(string Title, string Subtitle, bool VotingOpen, Guid? CurrentQuestionId,
    long Revision, IReadOnlyList<PanellistView> Panellists, IReadOnlyList<QuestionView> Questions,
    string LandingImageUrl, string LandingImageDescription, bool HasCustomLandingImage,
    int SpeakerSeconds, int GroupSeconds, bool GroupDiscussion, DateTimeOffset? TimerStartedAt,
    int? TimerDurationSeconds, DateTimeOffset ServerTime, bool ScoreboardVisible,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScoreboardView? Scoreboard);
public sealed record SpeakerPoints(Guid SpeakerId, long Points);
public sealed record SpeakerTotal(Guid Id, string Name, long Points, bool Removed);
public sealed record QuestionScores(Guid Id, string Text, IReadOnlyList<SpeakerPoints> Scores, long UnassignedPoints);
public sealed record ScoreboardView(IReadOnlyList<SpeakerTotal> Speakers, IReadOnlyList<QuestionScores> Questions);
