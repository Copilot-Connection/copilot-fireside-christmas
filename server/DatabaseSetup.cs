using Microsoft.EntityFrameworkCore;

namespace Fireside;

public static class DatabaseSetup
{
    public static async Task UpgradeSqlite(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var (table, column, definition) in new[]
        {
            ("Panellists", "Type", "TEXT NOT NULL DEFAULT 'guest'"),
            ("Boards", "ScoreEpoch", "INTEGER NOT NULL DEFAULT 0"),
            ("Votes", "ScoreEpoch", "INTEGER NOT NULL DEFAULT 0"),
            ("ScoreAdjustments", "ScoreEpoch", "INTEGER NOT NULL DEFAULT 0")
        })
        {
            await using var inspect = connection.CreateCommand();
            inspect.Transaction = transaction;
            inspect.CommandText = $"PRAGMA table_info(\"{table}\")";
            var exists = false;
            var found = false;
            await using (var reader = await inspect.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                {
                    exists = true;
                    if (reader.GetString(1) == column) found = true;
                }
            if (exists && !found)
            {
                await using var alter = connection.CreateCommand();
                alter.Transaction = transaction;
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
                await alter.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
    }

    public static async Task Initialize(IServiceProvider services, bool seedDemo)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
        await db.Database.EnsureCreatedAsync();
        await UpgradeSqlite(services);
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
                new Panellist { Name = "Sample host", Type = "host", Role = "Your fireside guide", Bio = "Sample panellist. Replace with your confirmed host.", Visible = true, SortOrder = 0 },
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
