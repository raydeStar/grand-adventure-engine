# Database Migration — Project Scope Document

**Project:** Grand Adventure Engine (GAE)
**Author:** Mark Hall / Sir Thaddeus
**Date:** 2026-04-05
**Status:** Draft — Ready for GHC Review
**Prerequisite for:** Multi-World System, Lore Book

---

## 1. Executive Summary

This project migrates the Grand Adventure Engine from its current **file-based event-sourced persistence** (JSONL journal + checkpoint snapshots) to **PostgreSQL** via **Entity Framework Core** with code-first migrations.

The current system is architecturally sound — append-only journal, checkpoint/replay recovery, single-writer decorator pattern — but it stores everything in flat files and in-memory dictionaries. As the engine evolves toward multi-world support, cross-realm entity references, and admin tooling, the relational query capabilities and transactional guarantees of a proper database become essential.

The migration preserves the existing `IStateManager` interface contract. All consumers (GameEngine, DashboardController, SignalR hub) continue calling the same methods. Only the implementation behind the interface changes.

---

## 2. Goals and Non-Goals

### Goals

- Replace `InMemoryStateManager` + `JournaledStateManager` + file-based journal/checkpoint with a PostgreSQL-backed implementation
- Use EF Core code-first with migrations for schema management
- Preserve the `IStateManager` interface — zero breaking changes to callers
- Migrate existing on-disk state (journal.jsonl, checkpoints) into the new database as a one-time import
- Use PostgreSQL JSONB columns for flexible/semi-structured data (NPC memory flags, dialogue dictionaries, narrator metadata, stat bonuses)
- Maintain the conversation logger (`IConversationLogger`) — migrate to a database table
- Provide direct database access for admin backend queries (the whole reason Mark wants this)
- Maintain thread-safety guarantees for concurrent Discord + Dashboard access
- Add database health check to the existing `/dashboard/health` endpoint

### Non-Goals

- Changing the `IStateManager` interface signature (that's a separate concern if needed for multi-world)
- Migrating the content registries to the database (spells, classes, races, items, monsters remain YAML-seeded and in-memory — they're read-only reference data)
- Implementing multi-world schema (that comes in the next scope doc, building on this foundation)
- Adding an ORM to the frontend/dashboard JS — the API layer remains the data boundary
- Rewriting the event sourcing pattern (we keep GameEvent as an audit log table, not as the primary state mechanism)

---

## 3. Current Architecture (What We're Replacing)

### 3.1 Persistence Stack

```
┌──────────────────────────────────┐
│         IStateManager            │  ← Interface (unchanged)
├──────────────────────────────────┤
│      JournaledStateManager       │  ← Decorator: journals every write
│  ┌────────────────────────────┐  │
│  │   InMemoryStateManager     │  │  ← ConcurrentDictionary store
│  └────────────────────────────┘  │
├──────────────────────────────────┤
│  FileStateJournal (journal.jsonl)│  ← Append-only JSONL event log
│  FileStateCheckpointStore        │  ← Periodic full-state snapshots
│  StateReplayService              │  ← Startup: checkpoint + replay
└──────────────────────────────────┘
```

### 3.2 Data Structures Currently In Memory

| Store | Key | Value | Approx Size |
|---|---|---|---|
| `_players` | `string playerId` | `PlayerCharacter` | Small (< 100 players) |
| `_rooms` | `string roomId` | `Room` (templates) | Medium (dozens of rooms) |
| `_playerRooms` | `"playerId:roomId"` | `Room` (cloned instances) | Medium-Large (players × rooms visited) |
| `_combats` | `string roomId` | `CombatState` | Small (active combats only) |
| `_storyEntries` | sequential | `List<StoryEntry>` | Large (unbounded, grows forever) |

### 3.3 Files on Disk

| File | Format | Purpose |
|---|---|---|
| `/data/journal.jsonl` | JSON Lines | Append-only event log (GameEvent per line) |
| `/data/checkpoints/checkpoint-{seq}.json` | JSON | Full state snapshots |
| `/data/conversations.jsonl` | JSON Lines | LLM conversation logs |

### 3.4 DI Registration (current)

```csharp
// Current chain: IStateManager → JournaledStateManager → InMemoryStateManager
builder.Services.AddSingleton<InMemoryStateManager>();
builder.Services.AddSingleton<IStateJournal>(sp => new FileStateJournal(...));
builder.Services.AddSingleton<IStateCheckpointStore>(sp => new FileStateCheckpointStore(...));
builder.Services.AddSingleton<StateReplayService>();
builder.Services.AddSingleton<JournaledStateManager>();
builder.Services.AddSingleton<IStateManager>(sp => sp.GetRequiredService<JournaledStateManager>());
builder.Services.AddSingleton<IConversationLogger>(sp => new FileConversationLogger(...));
```

---

## 4. Target Architecture

### 4.1 New Persistence Stack

```
┌──────────────────────────────────┐
│         IStateManager            │  ← Interface (unchanged)
├──────────────────────────────────┤
│      EfCoreStateManager          │  ← New: direct DB reads/writes
│  ┌────────────────────────────┐  │
│  │     GaeDbContext            │  │  ← EF Core DbContext
│  │     (Npgsql provider)      │  │
│  └────────────────────────────┘  │
├──────────────────────────────────┤
│  PostgreSQL                      │  ← Tables, indexes, JSONB columns
│  ┌────────────────────────────┐  │
│  │  game_events (audit log)   │  │  ← Optional: keep event history
│  └────────────────────────────┘  │
└──────────────────────────────────┘
```

### 4.2 New DI Registration (target)

```csharp
// Database context
builder.Services.AddDbContext<GaeDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.MigrationsAssembly("GAE.Engine")));

// New implementation: IStateManager → EfCoreStateManager
builder.Services.AddScoped<IStateManager, EfCoreStateManager>();

// Conversation logger → database table
builder.Services.AddScoped<IConversationLogger, EfCoreConversationLogger>();
```

**Note the shift from Singleton to Scoped.** The current in-memory manager is a singleton because it holds state. The EF Core manager is scoped because `DbContext` is scoped by design — each request gets its own context with its own change tracker. Thread safety is now handled by PostgreSQL's transaction isolation, not ConcurrentDictionary.

---

## 5. Database Schema

### 5.1 Entity-Relationship Overview

```
┌──────────────┐     ┌──────────────┐     ┌──────────────────┐
│   players     │────<│ player_rooms  │>────│     rooms        │
│               │     │ (cloned inst) │     │   (templates)    │
└──────┬───────┘     └──────────────┘     └──────────────────┘
       │                                          │
       │              ┌──────────────┐            │
       └─────────────<│ story_entries │>───────────┘
                      └──────────────┘
       │
       └─────────────<│ combat_states │
                      └──────────────┘

┌──────────────┐     ┌──────────────────────┐
│ game_events   │     │ conversation_logs     │
│ (audit trail) │     │ (LLM exchange history)│
└──────────────┘     └──────────────────────┘
```

### 5.2 Table Definitions

#### `players`

| Column | Type | Notes |
|---|---|---|
| `id` | `text` PK | GUID string (matches current `PlayerCharacter.Id`) |
| `name` | `text NOT NULL` | |
| `race` | `text NOT NULL` | |
| `class` | `text NOT NULL` | |
| `backstory` | `text` | |
| `discord_id` | `text` | UNIQUE index (nullable) |
| `thread_id` | `bigint` | Discord thread ID |
| `has_completed_demo` | `boolean` | |
| `current_room_id` | `text` | FK → rooms.id (nullable, no cascade) |
| `hp` | `integer` | |
| `max_hp` | `integer` | |
| `mp` | `integer` | |
| `max_mp` | `integer` | |
| `gold` | `integer` | |
| `xp` | `integer` | |
| `level` | `integer` | |
| `str` | `integer` | |
| `dex` | `integer` | |
| `con` | `integer` | |
| `int` | `integer` | |
| `wis` | `integer` | |
| `cha` | `integer` | |
| `luck` | `integer` | |
| `equipment` | `jsonb` | Serialized `EquipmentLoadout` |
| `inventory` | `jsonb` | Serialized `List<InventoryItem>` |
| `status_effects` | `jsonb` | Serialized `List<StatusEffect>` |
| `spellbook` | `jsonb` | Serialized `List<LearnedSpell>` |
| `interaction` | `jsonb` | Serialized `InteractionState` |
| `created_at` | `timestamptz` | |
| `last_active_at` | `timestamptz` | |

**Indexes:** `ix_players_discord_id` (unique), `ix_players_current_room_id`

**Design note:** Stats are individual columns (not JSONB) because they're queried, sorted, and filtered frequently. Equipment, inventory, spellbook, and status effects are JSONB because they're complex nested structures that are always loaded/saved as a whole and rarely queried individually.

#### `rooms`

| Column | Type | Notes |
|---|---|---|
| `id` | `text` PK | Room identifier ("spawn", "forest_clearing", etc.) |
| `name` | `text NOT NULL` | |
| `description` | `text` | |
| `exits` | `jsonb` | Serialized `Dictionary<string, string>` (direction → room ID) |
| `npcs` | `jsonb` | Serialized `List<Npc>` |
| `items` | `jsonb` | Serialized `List<InventoryItem>` |
| `environment_tags` | `jsonb` | Serialized `List<string>` |
| `is_template` | `boolean DEFAULT true` | Distinguishes templates from player instances |

**Index:** `ix_rooms_is_template` (partial index where `is_template = true`)

#### `player_rooms`

| Column | Type | Notes |
|---|---|---|
| `id` | `text` PK | Composite format: `"playerId:roomId"` (matches current key scheme) |
| `player_id` | `text NOT NULL` | FK → players.id (CASCADE delete) |
| `room_id` | `text NOT NULL` | FK → rooms.id (template reference) |
| `name` | `text NOT NULL` | |
| `description` | `text` | |
| `exits` | `jsonb` | |
| `npcs` | `jsonb` | |
| `items` | `jsonb` | |
| `environment_tags` | `jsonb` | |

**Indexes:** `ix_player_rooms_player_id`, unique constraint on `(player_id, room_id)`

#### `story_entries`

| Column | Type | Notes |
|---|---|---|
| `id` | `text` PK | GUID |
| `player_id` | `text` | FK → players.id (SET NULL on delete) |
| `room_id` | `text` | |
| `entry_type` | `text` | |
| `content` | `text` | |
| `metadata` | `jsonb` | Additional narrator/engine metadata |
| `created_at` | `timestamptz` | |

**Indexes:** `ix_story_entries_player_id`, `ix_story_entries_room_id`, `ix_story_entries_created_at` (descending — most queries want newest first)

#### `combat_states`

| Column | Type | Notes |
|---|---|---|
| `room_id` | `text` PK | FK → rooms.id |
| `state` | `jsonb NOT NULL` | Full serialized `CombatState` |
| `created_at` | `timestamptz` | |
| `updated_at` | `timestamptz` | |

**Design note:** Combat state is transient and complex (turn order, combatant list, conditions). JSONB blob is the right call — it's loaded/saved as a unit, never partially queried.

#### `game_events` (audit log — replaces journal.jsonl)

| Column | Type | Notes |
|---|---|---|
| `id` | `bigserial` PK | Auto-incrementing (replaces SequenceNumber) |
| `event_id` | `uuid` | |
| `action_id` | `text` | |
| `correlation_id` | `text` | |
| `type` | `smallint` | GameEventType enum value |
| `player_id` | `text` | |
| `room_id` | `text` | |
| `summary` | `text` | |
| `narration` | `text` | |
| `data` | `jsonb` | Event payload |
| `created_at` | `timestamptz DEFAULT now()` | |

**Indexes:** `ix_game_events_type`, `ix_game_events_player_id`, `ix_game_events_created_at`

**Note:** This table is **append-only** — write, never update or delete. It replaces the JSONL journal as a queryable audit trail. It is NOT used for state recovery (that's EF Core's job now), but it's invaluable for admin debugging, analytics, and future replay features.

#### `conversation_logs` (replaces conversations.jsonl)

| Column | Type | Notes |
|---|---|---|
| `id` | `bigserial` PK | |
| `player_id` | `text` | |
| `interaction_mode` | `text` | |
| `prompt` | `text` | Full system + user prompt sent to LLM |
| `response` | `text` | Raw LLM response |
| `model` | `text` | Model name used |
| `token_count` | `integer` | |
| `latency_ms` | `integer` | |
| `created_at` | `timestamptz DEFAULT now()` | |

**Indexes:** `ix_conversation_logs_player_id`, `ix_conversation_logs_created_at`

---

## 6. EF Core Implementation

### 6.1 New Project Structure

No new project needed. The `GaeDbContext` lives in `GAE.Engine` (where the current state manager lives), and entity configurations go alongside it:

```
src/GAE.Engine/
├── Data/
│   ├── GaeDbContext.cs
│   ├── Configurations/
│   │   ├── PlayerConfiguration.cs
│   │   ├── RoomConfiguration.cs
│   │   ├── PlayerRoomConfiguration.cs
│   │   ├── StoryEntryConfiguration.cs
│   │   ├── CombatStateConfiguration.cs
│   │   ├── GameEventConfiguration.cs
│   │   └── ConversationLogConfiguration.cs
│   ├── EfCoreStateManager.cs
│   └── EfCoreConversationLogger.cs
├── Migrations/
│   └── (EF Core auto-generated)
```

### 6.2 GaeDbContext

```csharp
public class GaeDbContext : DbContext
{
    public DbSet<PlayerCharacter> Players => Set<PlayerCharacter>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<PlayerRoom> PlayerRooms => Set<PlayerRoom>();
    public DbSet<StoryEntry> StoryEntries => Set<StoryEntry>();
    public DbSet<CombatState> CombatStates => Set<CombatState>();
    public DbSet<GameEvent> GameEvents => Set<GameEvent>();
    public DbSet<ConversationLog> ConversationLogs => Set<ConversationLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(GaeDbContext).Assembly);
    }
}
```

### 6.3 EfCoreStateManager (implements IStateManager)

Key implementation decisions:

- **GetPlayerAsync / GetRoomAsync / etc.** → Simple `DbSet.FindAsync()` or `FirstOrDefaultAsync()` calls
- **SavePlayerAsync / SaveRoomAsync / etc.** → Upsert pattern: check `Exists()`, then `Add()` or `Update()` + `SaveChangesAsync()`
- **GetPlayerRoomAsync** → Check `PlayerRooms` for existing clone. If missing, load template from `Rooms`, deep-clone, save to `PlayerRooms`, return
- **Story entries** → `AddAsync()` + `SaveChangesAsync()`. Queries use `.OrderByDescending(e => e.CreatedAt).Take(limit)`
- **Bulk deletes** → `ExecuteDeleteAsync()` for EF Core 7+ bulk operations (no loading into memory)
- **Audit logging** → Optionally still write to `game_events` table on mutations (configurable, off by default for performance)

### 6.4 JSONB Column Configuration Example

```csharp
public class PlayerConfiguration : IEntityTypeConfiguration<PlayerCharacter>
{
    public void Configure(EntityTypeBuilder<PlayerCharacter> builder)
    {
        builder.ToTable("players");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Equipment)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<EquipmentLoadout>(v, JsonDefaults.Options)!);

        builder.Property(p => p.Inventory)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<InventoryItem>>(v, JsonDefaults.Options)!);

        // ... similar for StatusEffects, Spellbook, Interaction
    }
}
```

---

## 7. Data Migration Strategy

### 7.1 One-Time Import Tool

A CLI command or startup migration step that:

1. Loads the latest checkpoint file (if exists)
2. Replays remaining journal entries on top of it (using existing `StateReplayService`)
3. Populates the `InMemoryStateManager` with full current state
4. Iterates all in-memory collections and writes to PostgreSQL via `GaeDbContext`
5. Optionally imports `journal.jsonl` into the `game_events` table for historical audit
6. Optionally imports `conversations.jsonl` into `conversation_logs`

### 7.2 Implementation

```csharp
public class DataMigrationService
{
    // Inject: InMemoryStateManager (loaded via replay), GaeDbContext

    public async Task MigrateAsync(CancellationToken ct)
    {
        // 1. Replay from files into memory (existing mechanism)
        await _replayService.ReplayAsync(ct);

        // 2. Snapshot current in-memory state
        var snapshot = _inMemoryManager.TakeSnapshot();

        // 3. Bulk insert into PostgreSQL
        await _db.Players.AddRangeAsync(snapshot.Players, ct);
        await _db.Rooms.AddRangeAsync(snapshot.Rooms, ct);
        await _db.PlayerRooms.AddRangeAsync(snapshot.PlayerRooms, ct);
        await _db.StoryEntries.AddRangeAsync(snapshot.StoryEntries, ct);
        await _db.CombatStates.AddRangeAsync(snapshot.CombatStates, ct);
        await _db.SaveChangesAsync(ct);

        // 4. Import journal history (optional)
        await ImportJournalHistoryAsync(ct);
    }
}
```

### 7.3 Rollback Safety

- The old file-based system is **not deleted** during migration — files remain on disk as a backup
- A feature flag (`UsePostgres: true/false` in appsettings) toggles between old and new implementations
- If the flag is false, the DI container wires up the original `JournaledStateManager` chain
- This allows instant rollback without code changes

---

## 8. Configuration

### 8.1 appsettings.json additions

```json
{
  "ConnectionStrings": {
    "GameDatabase": "Host=localhost;Port=5432;Database=gae;Username=gae_app;Password=<secret>"
  },
  "Persistence": {
    "UsePostgres": true,
    "AuditLogEnabled": true,
    "AuditLogRetentionDays": 90
  }
}
```

### 8.2 Docker Compose addition

```yaml
services:
  postgres:
    image: postgres:17
    environment:
      POSTGRES_DB: gae
      POSTGRES_USER: gae_app
      POSTGRES_PASSWORD: ${GAE_DB_PASSWORD}
    ports:
      - "5432:5432"
    volumes:
      - pgdata:/var/lib/postgresql/data

volumes:
  pgdata:
```

---

## 9. What Gets Removed

After successful migration and a confidence period:

| File | Status |
|---|---|
| `GAE.Engine/State/InMemoryStateManager.cs` | **Keep** (useful for unit tests with in-memory provider) |
| `GAE.Engine/State/JournaledStateManager.cs` | **Remove** (replaced by EfCoreStateManager) |
| `GAE.Engine/State/FileStateJournal.cs` | **Remove** |
| `GAE.Engine/State/FileStateCheckpointStore.cs` | **Remove** |
| `GAE.Engine/State/StateReplayService.cs` | **Remove** (migration tool uses it, then it's done) |
| `GAE.Engine/State/FileConversationLogger.cs` | **Remove** |
| `/data/journal.jsonl` | **Archive** (keep backup, stop writing) |
| `/data/checkpoints/` | **Archive** |
| `/data/conversations.jsonl` | **Archive** |

The `InMemoryStateManager` survives because it's perfect for unit testing with EF Core's `UseInMemoryDatabase()` provider or as a test double.

---

## 10. Impact on Existing Features

### 10.1 Zero-Change Consumers

These files call `IStateManager` and require **no changes**:

- `GAE.Engine/GameEngine.cs` — all 3400+ lines of game logic
- `GAE.Dashboard.Api/Controllers/DashboardController.cs` — all admin endpoints
- `GAE.Dashboard.Api/Hubs/GameHub.cs` — SignalR hub
- `GAE.Discord/` — Discord bot commands

### 10.2 Minor Changes

| File | Change |
|---|---|
| `Program.cs` | Replace DI registration chain (see Section 4.2) |
| `Program.cs` | Add `app.MigrateDatabase()` call on startup (runs pending EF Core migrations) |
| `Program.cs` | Remove journal replay on startup |
| `Program.cs` | Remove checkpoint flush on shutdown |
| `appsettings.json` | Add connection string and persistence config |
| `docker-compose.yml` | Add PostgreSQL service |
| `GAE.Engine.csproj` | Add Npgsql.EntityFrameworkCore.PostgreSQL package |
| `GAE.Dashboard.Api.csproj` | Add Microsoft.EntityFrameworkCore.Design (for migrations CLI) |

### 10.3 SignalR Broadcasting

The existing `IGameEventBroadcaster` implementation (see `GAE.Core/Interfaces/IGameEventBroadcaster.cs`) currently subscribes to `JournaledStateManager` events. Post-migration, `EfCoreStateManager` should raise the same events (or the broadcaster hooks into `DbContext.SaveChanges` via interceptors). This needs attention to preserve real-time updates.

---

## 11. Testing Strategy

| Layer | Test Type | What |
|---|---|---|
| `EfCoreStateManager` | Unit (xUnit) | All `IStateManager` methods against EF Core InMemory provider |
| `EfCoreStateManager` | Integration | Same tests against a real PostgreSQL instance (Docker testcontainer) |
| `GaeDbContext` | Unit | Entity configurations, JSONB serialization round-trips |
| `DataMigrationService` | Integration | Import from real journal.jsonl → verify database state matches |
| `GameEngine` | Existing tests | Run full existing test suite against new state manager — must all pass |
| `DashboardController` | Existing tests | Admin endpoint tests against new backend |
| Performance | Benchmark | Compare response times: file-based vs. PostgreSQL for typical operations |

---

## 12. NuGet Packages Required

| Package | Version | Purpose |
|---|---|---|
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.x | PostgreSQL EF Core provider (match .NET 10) |
| `Microsoft.EntityFrameworkCore.Design` | 10.x | Migration tooling (dev dependency) |
| `Microsoft.EntityFrameworkCore.Tools` | 10.x | `dotnet ef` CLI support |
| `Testcontainers.PostgreSql` | latest | Integration test PostgreSQL containers |

---

## 13. Implementation Phases

### Phase 1: Schema and DbContext (Est. 3-4 days)

- [ ] Add NuGet packages to GAE.Engine and GAE.Dashboard.Api
- [ ] Create `GaeDbContext` with all `DbSet<>` properties
- [ ] Create entity configurations for all 7 tables (JSONB mappings, indexes, constraints)
- [ ] Generate initial EF Core migration
- [ ] Add PostgreSQL to docker-compose.yml
- [ ] Verify migration applies cleanly to a fresh database
- [ ] Unit tests for entity configurations and JSONB round-trips

### Phase 2: EfCoreStateManager (Est. 4-5 days)

- [ ] Implement `EfCoreStateManager : IStateManager` — all methods
- [ ] Implement `EfCoreConversationLogger : IConversationLogger`
- [ ] Wire up DI registration with feature flag toggle
- [ ] Port existing `IStateManager` unit tests to run against both implementations
- [ ] Integration tests against real PostgreSQL via testcontainers
- [ ] Verify SignalR event broadcasting still works (interceptor or explicit raise)

### Phase 3: Data Migration (Est. 2-3 days)

- [ ] Build `DataMigrationService` (replay → snapshot → bulk insert)
- [ ] Build journal.jsonl → game_events import
- [ ] Build conversations.jsonl → conversation_logs import
- [ ] Test migration with real data from `/data/` directory
- [ ] Verify migrated data integrity (row counts, spot-check records)

### Phase 4: Integration and Cleanup (Est. 2-3 days)

- [ ] Run full existing test suite against PostgreSQL backend
- [ ] Run the game end-to-end: create character → explore → combat → trade → verify DB state
- [ ] Test admin endpoints against real database (mutations, search, registry)
- [ ] Performance benchmarking (file vs. DB for common operations)
- [ ] Update Program.cs startup sequence (remove replay, add migration apply)
- [ ] Update AGENTS.md and HANDOFF.md documentation
- [ ] Archive old file-based state classes (don't delete yet)

**Total estimated effort: 11-15 days**

---

## 14. Resolved Design Decisions

These were reviewed and decided by the project owner on 2026-04-05:

1. **Connection pooling:** Use Npgsql defaults (pool size 100). More than sufficient for Discord + Dashboard concurrent load. Revisit only if monitoring shows pool exhaustion.

2. **Audit log retention:** **Keep forever.** The `game_events` table is append-only and never pruned. Storage is cheap, and full history is valuable for analytics, debugging, narrative replay features, and future lore book integration. Add periodic `REINDEX` maintenance if index bloat becomes measurable.

3. **EF Core version:** **EF Core 10** to match .NET 10 target framework. Keep versions aligned for maximum compatibility and access to latest features.

4. **Database hosting:** **Docker Compose.** PostgreSQL runs alongside the app in the existing Docker setup. Connection string: `Host=postgres;Port=5432;Database=gae;Username=gae_app;Password=${GAE_DB_PASSWORD}`. Backups via `pg_dump` cron job inside a sidecar container or host-level Docker volume snapshots.

5. **Backup strategy:** Docker volume snapshots for daily backups, plus a scheduled `pg_dump` for portable SQL backups. Store dumps in a mounted host directory outside the container. No need for point-in-time recovery at this scale.

---

*"The difference between a journal and a database, sir, is the difference between a butler's diary and a butler's filing cabinet. Both remember everything — but only one can find it when you need it."*
*— Sir Thaddeus, on the merits of proper indexing*
