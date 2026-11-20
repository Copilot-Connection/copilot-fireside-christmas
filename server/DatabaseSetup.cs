using Microsoft.EntityFrameworkCore;

namespace Fireside;

public static class DatabaseSetup
{
    public static async Task Initialize(IServiceProvider services, bool seedDemo)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
        await db.Database.EnsureCreatedAsync();
        await using var schema = typeof(DatabaseSetup).Assembly
            .GetManifestResourceStream("Fireside.AudienceAccountSchema")
            ?? throw new InvalidOperationException("The audience account schema is missing.");
        using var reader = new StreamReader(schema);
        await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
        if (await db.Boards.AnyAsync()) return;
        db.Boards.Add(new Board
        {
            Title = "Copilot Fireside Chat",
            Subtitle = seedDemo ? "The Christmas edition - sample event" : "The Christmas edition",
            VotingOpen = seedDemo
        });
        if (seedDemo)
        {
            db.Panellists.AddRange(
                new Panellist { Name = "Sample host", Role = "Your fireside guide", Bio = "Sample panellist. Replace with your confirmed host.", Visible = true, SortOrder = 0 },
                new Panellist { Name = "Sample builder", Role = "From idea to everyday impact", Bio = "Sample panellist. Add your speaker's story in the host desk.", Visible = true, SortOrder = 1 },
                new Panellist { Name = "Sample explorer", Role = "Curiosity meets Copilot", Bio = "Sample panellist. No real speaker is represented.", Visible = true, SortOrder = 2 });
            db.Questions.AddRange(
                new Question { Text = "What was your biggest Copilot lightbulb moment this year?", Category = "A year of discovery", Visible = true, SortOrder = 0 },
                new Question { Text = "If you could put one Copilot tip in everyone's Christmas stocking, what would it be?", Category = "Practical magic", Visible = true, SortOrder = 1 },
                new Question { Text = "Where should someone who's just getting started focus first?", Category = "Getting started", Visible = true, SortOrder = 2 },
                new Question { Text = "What are you most excited to build with Copilot next year?", Category = "Looking ahead", Visible = true, SortOrder = 3 });
        }
        await db.SaveChangesAsync();
    }
}
