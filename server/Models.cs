using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Fireside;

public sealed class Board
{
    public int Id { get; set; } = 1;
    [MaxLength(120)] public string Title { get; set; } = "Copilot Fireside Chat";
    [MaxLength(300)] public string Subtitle { get; set; } = "A little warmth. A lot of possibilities.";
    public bool VotingOpen { get; set; }
    public Guid? CurrentQuestionId { get; set; }
    public long Revision { get; set; }
}

public sealed class Panellist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(120)] public string Role { get; set; } = "";
    [MaxLength(500)] public string Bio { get; set; } = "";
    public bool Visible { get; set; }
    public int SortOrder { get; set; }
}

public sealed class Question
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(240)] public string Text { get; set; } = "";
    [MaxLength(60)] public string Category { get; set; } = "";
    public bool Visible { get; set; }
    public bool Answered { get; set; }
    public int SortOrder { get; set; }
}

public sealed class Vote
{
    public Guid QuestionId { get; set; }
    public Guid ParticipantId { get; set; }
    public Question Question { get; set; } = null!;
}

public sealed class FiresideDb(DbContextOptions<FiresideDb> options) : DbContext(options)
{
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<Panellist> Panellists => Set<Panellist>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Vote> Votes => Set<Vote>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Board>().Property(b => b.Revision).IsConcurrencyToken();
        model.Entity<Vote>().HasKey(v => new { v.QuestionId, v.ParticipantId });
        model.Entity<Vote>().HasOne(v => v.Question).WithMany().HasForeignKey(v => v.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed record Participant(Guid Id, string Name, DateTimeOffset Expires);
public sealed record NameInput(string? Name);
public sealed record BoardInput(string? Title, string? Subtitle, bool VotingOpen, Guid? CurrentQuestionId);
public sealed record PanellistInput(string? Name, string? Role, string? Bio, bool Visible, int SortOrder);
public sealed record QuestionInput(string? Text, string? Category, bool Visible, bool Answered, int SortOrder);
public sealed record QuestionView(Guid Id, string Text, string Category, bool Visible, bool Answered,
    int SortOrder, int Votes, bool HasVoted);
public sealed record BoardView(string Title, string Subtitle, bool VotingOpen, Guid? CurrentQuestionId,
    long Revision, IReadOnlyList<Panellist> Panellists, IReadOnlyList<QuestionView> Questions);
