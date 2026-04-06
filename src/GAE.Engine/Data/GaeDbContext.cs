using GAE.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GAE.Engine.Data;

/// <summary>
/// EF Core database context for all GAE persistent state.
/// Uses PostgreSQL with JSONB columns for complex nested structures.
/// </summary>
public class GaeDbContext : DbContext
{
    public GaeDbContext(DbContextOptions<GaeDbContext> options) : base(options) { }

    // ── State tables ──
    public DbSet<PlayerEntity> Players => Set<PlayerEntity>();
    public DbSet<RoomEntity> Rooms => Set<RoomEntity>();
    public DbSet<PlayerRoomEntity> PlayerRooms => Set<PlayerRoomEntity>();
    public DbSet<StoryEntryEntity> StoryEntries => Set<StoryEntryEntity>();
    public DbSet<CombatStateEntity> CombatStates => Set<CombatStateEntity>();
    public DbSet<PartyQuestEntity> PartyQuests => Set<PartyQuestEntity>();
    public DbSet<GameEventEntity> GameEvents => Set<GameEventEntity>();
    public DbSet<ConversationLogEntity> ConversationLogs => Set<ConversationLogEntity>();

    // ── Content registry tables ──
    public DbSet<ContentRegistryEntity> ContentRegistry => Set<ContentRegistryEntity>();
    public DbSet<GameConfigEntity> GameConfig => Set<GameConfigEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GaeDbContext).Assembly);
    }
}

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations</c> when the startup project
/// cannot build a service provider (e.g. due to missing runtime services).
/// </summary>
public class GaeDbContextDesignTimeFactory : IDesignTimeDbContextFactory<GaeDbContext>
{
    public GaeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GaeDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=gae;Username=gae_app;Password=gae_dev_password",
                npgsql => npgsql.MigrationsAssembly("GAE.Engine"))
            .Options;
        return new GaeDbContext(options);
    }
}
