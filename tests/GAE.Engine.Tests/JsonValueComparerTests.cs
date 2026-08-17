using GAE.Core.Models;
using GAE.Engine.Data;
using Microsoft.EntityFrameworkCore;

namespace GAE.Engine.Tests;

public class JsonValueComparerTests
{
    [Fact]
    public void PlayerCollections_InPlaceMutation_IsDetected()
    {
        using var db = CreateContext();
        var player = CreatePlayer();
        db.Attach(player);

        player.DiscoveredLore.Add("lore-moonfall-fair");
        db.ChangeTracker.DetectChanges();

        Assert.True(db.Entry(player).Property(candidate => candidate.DiscoveredLore).IsModified);
    }

    [Fact]
    public void RoomDictionary_InPlaceMutation_IsDetected()
    {
        using var db = CreateContext();
        var room = new RoomEntity
        {
            Id = "comparer-room",
            Name = "The Comparator's Closet",
            Description = "Even collections leave footprints here."
        };
        db.Attach(room);

        room.Exits["west"] = "moonfall_gate";
        db.ChangeTracker.DetectChanges();

        Assert.True(db.Entry(room).Property(candidate => candidate.Exits).IsModified);
    }

    [Fact]
    public void RoomNestedNpcMutation_IsDetected()
    {
        using var db = CreateContext();
        var room = new RoomEntity
        {
            Id = "nested-comparer-room",
            Name = "The Suspicious Parlour",
            Description = "The furniture remembers every insult.",
            Npcs = [new Npc { Id = "watcher", Name = "Watcher", Disposition = "neutral" }]
        };
        db.Attach(room);

        room.Npcs[0].Disposition = "suspicious";
        db.ChangeTracker.DetectChanges();

        Assert.True(db.Entry(room).Property(candidate => candidate.Npcs).IsModified);
    }

    private static GaeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GaeDbContext>()
            .UseNpgsql("Host=localhost;Database=metadata_only;Username=unused;Password=unused")
            .Options;
        return new GaeDbContext(options);
    }

    private static PlayerEntity CreatePlayer()
        => new()
        {
            Id = "comparer-player",
            Name = "Ledger",
            Race = "human",
            Class = "wizard",
            CreatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow
        };
}
