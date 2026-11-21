using Fireside;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fireside.Tests;

public sealed class SqliteUpgradeTests
{
    [Fact]
    public async Task Legacy_columns_upgrade_idempotently_without_changing_existing_votes()
    {
        await using var site = new Site();
        using var client = await site.Client();
        await using var scope = site.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiresideDb>();
        var question = await db.Questions.FirstAsync();
        var vote = new Vote { Id = Guid.NewGuid(), QuestionId = question.Id, ParticipantId = Guid.NewGuid(), Delta = 3 };
        db.Votes.Add(vote);
        await db.SaveChangesAsync();
        foreach (var statement in new[] {
            "ALTER TABLE Panellists DROP COLUMN Type",
            "ALTER TABLE Boards DROP COLUMN ScoreEpoch",
            "ALTER TABLE Votes DROP COLUMN ScoreEpoch",
            "ALTER TABLE ScoreAdjustments DROP COLUMN ScoreEpoch" })
            await db.Database.ExecuteSqlRawAsync(statement);
        await DatabaseSetup.UpgradeSqlite(site.Services);
        await DatabaseSetup.UpgradeSqlite(site.Services);
        db.ChangeTracker.Clear();
        Assert.All(await db.Panellists.ToListAsync(), person => Assert.Equal("guest", person.Type));
        Assert.Equal(0, (await db.Boards.SingleAsync()).ScoreEpoch);
        var preserved = await db.Votes.SingleAsync(v => v.Id == vote.Id);
        Assert.Equal(3, preserved.Delta);
        Assert.Equal(0, preserved.ScoreEpoch);
    }
}
