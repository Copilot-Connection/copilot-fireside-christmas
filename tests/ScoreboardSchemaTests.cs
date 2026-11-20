using Microsoft.Data.Sqlite;
using Xunit;

namespace Fireside.Tests;

public sealed class ScoreboardSchemaTests
{
    [Fact]
    public async Task Upgrade_resets_only_pre_feature_points_and_cannot_reset_later_votes_when_repeated()
    {
        await using var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync();
        async Task Run(string sql)
        {
            await using var command = db.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        async Task<object?> Value(string sql)
        {
            await using var command = db.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }
        await Run("""
            PRAGMA foreign_keys=ON;
            CREATE TABLE Boards(Id INTEGER PRIMARY KEY, Title TEXT, SpeakerSeconds INTEGER);
            CREATE TABLE Questions(Id TEXT PRIMARY KEY, Text TEXT);
            CREATE TABLE Panellists(Id TEXT PRIMARY KEY, PhotoData BLOB);
            CREATE TABLE Votes(Id TEXT PRIMARY KEY, QuestionId TEXT, ParticipantId TEXT, Delta INTEGER);
            INSERT INTO Boards VALUES(1, 'Keep my event', 180);
            INSERT INTO Questions VALUES('q', 'Keep my question');
            INSERT INTO Panellists VALUES('p', X'010203');
            INSERT INTO Votes VALUES('old', 'q', 'v', 3);
            """);
        var upgrade = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schema", "006-speaker-scoreboard.sqlite.sql"));
        await Run(upgrade);
        Assert.Equal(0L, await Value("SELECT COUNT(*) FROM Votes"));
        Assert.Equal("Keep my event", await Value("SELECT Title FROM Boards"));
        Assert.Equal(180L, await Value("SELECT SpeakerSeconds FROM Boards"));
        Assert.Equal("Keep my question", await Value("SELECT Text FROM Questions"));
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])(await Value("SELECT PhotoData FROM Panellists"))!);
        Assert.Equal(0L, await Value("SELECT ScoreboardVisible FROM Boards"));
        Assert.NotNull(await Value("SELECT ScoreboardInitializedAt FROM Boards"));
        await Run("INSERT INTO Votes VALUES('new', 'q', 'v', 1, 'p');");
        await Assert.ThrowsAsync<SqliteException>(() => Run(upgrade));
        await Run("ROLLBACK;");
        await Run("DELETE FROM Votes WHERE EXISTS(SELECT 1 FROM Boards WHERE ScoreboardInitializedAt IS NULL);");
        Assert.Equal(1L, await Value("SELECT SUM(Delta) FROM Votes"));
    }
}
