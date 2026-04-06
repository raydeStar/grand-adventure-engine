using GAE.Engine.Worlds;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GAE.Engine.Data.Configurations;

/// <summary>EF Core configuration for the worlds table.</summary>
public class WorldConfiguration : IEntityTypeConfiguration<WorldEntity>
{
    public void Configure(EntityTypeBuilder<WorldEntity> builder)
    {
        builder.ToTable("worlds");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).HasColumnName("id");
        builder.Property(w => w.Name).HasColumnName("name").IsRequired();
        builder.Property(w => w.Description).HasColumnName("description");
        builder.Property(w => w.SpawnRoomId).HasColumnName("spawn_room_id").IsRequired();
        builder.Property(w => w.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(w => w.CreatedBy).HasColumnName("created_by");
        builder.Property(w => w.CreatedAt).HasColumnName("created_at");
        builder.Property(w => w.UpdatedAt).HasColumnName("updated_at");

        builder.Property(w => w.Rules).HasColumnName("rules").HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => System.Text.Json.JsonSerializer.Deserialize<GAE.Engine.Configuration.GameRulesConfig>(v, JsonDefaults.Options) ?? new());

        builder.Property(w => w.Tags).HasColumnName("tags").HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, JsonDefaults.Options) ?? new List<string>());

        builder.Property(w => w.Portals).HasColumnName("portals").HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => System.Text.Json.JsonSerializer.Deserialize<List<WorldPortal>>(v, JsonDefaults.Options) ?? new List<WorldPortal>());
    }
}

/// <summary>EF Core configuration for player_world_states.</summary>
public class PlayerWorldStateConfiguration : IEntityTypeConfiguration<PlayerWorldStateEntity>
{
    public void Configure(EntityTypeBuilder<PlayerWorldStateEntity> builder)
    {
        builder.ToTable("player_world_states");
        builder.HasKey(s => new { s.PlayerId, s.WorldId });
        builder.Property(s => s.PlayerId).HasColumnName("player_id");
        builder.Property(s => s.WorldId).HasColumnName("world_id");
        builder.Property(s => s.CurrentRoomId).HasColumnName("current_room_id").IsRequired();
        builder.Property(s => s.HasVisited).HasColumnName("has_visited").HasDefaultValue(false);
        builder.Property(s => s.FirstVisitedAt).HasColumnName("first_visited_at");
        builder.Property(s => s.LastVisitedAt).HasColumnName("last_visited_at");
    }
}

/// <summary>EF Core configuration for world_stat_snapshots.</summary>
public class WorldStatSnapshotConfiguration : IEntityTypeConfiguration<WorldStatSnapshotEntity>
{
    public void Configure(EntityTypeBuilder<WorldStatSnapshotEntity> builder)
    {
        builder.ToTable("world_stat_snapshots");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.PlayerId).HasColumnName("player_id").IsRequired();
        builder.Property(s => s.WorldId).HasColumnName("world_id").IsRequired();
        builder.Property(s => s.Class).HasColumnName("class");
        builder.Property(s => s.Race).HasColumnName("race");
        builder.Property(s => s.Level).HasColumnName("level");
        builder.Property(s => s.Hp).HasColumnName("hp");
        builder.Property(s => s.MaxHp).HasColumnName("max_hp");
        builder.Property(s => s.Mp).HasColumnName("mp");
        builder.Property(s => s.MaxMp).HasColumnName("max_mp");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.Stats).HasColumnName("stats").HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(v, JsonDefaults.Options) ?? new(StringComparer.OrdinalIgnoreCase));

        builder.HasIndex(s => new { s.PlayerId, s.WorldId }).IsUnique();
    }
}

/// <summary>EF Core configuration for stat_translation_history.</summary>
public class StatTranslationHistoryConfiguration : IEntityTypeConfiguration<StatTranslationHistoryEntity>
{
    public void Configure(EntityTypeBuilder<StatTranslationHistoryEntity> builder)
    {
        builder.ToTable("stat_translation_history");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).HasColumnName("id");
        builder.Property(h => h.PlayerId).HasColumnName("player_id").IsRequired();
        builder.Property(h => h.SourceWorldId).HasColumnName("source_world_id").IsRequired();
        builder.Property(h => h.DestinationWorldId).HasColumnName("destination_world_id").IsRequired();
        builder.Property(h => h.TranslationNotes).HasColumnName("translation_notes");
        builder.Property(h => h.CreatedAt).HasColumnName("created_at");
        builder.Property(h => h.TranslatedStats).HasColumnName("translated_stats").HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(v, JsonDefaults.Options) ?? new(StringComparer.OrdinalIgnoreCase));

        builder.HasIndex(h => new { h.PlayerId, h.SourceWorldId, h.DestinationWorldId });
    }
}

/// <summary>EF Core configuration for world_npc_states.</summary>
public class WorldNpcStateConfiguration : IEntityTypeConfiguration<WorldNpcStateEntity>
{
    public void Configure(EntityTypeBuilder<WorldNpcStateEntity> builder)
    {
        builder.ToTable("world_npc_states");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.NpcId).HasColumnName("npc_id").IsRequired();
        builder.Property(s => s.WorldId).HasColumnName("world_id").IsRequired();
        builder.Property(s => s.PlayerId).HasColumnName("player_id");
        builder.Property(s => s.DispositionState).HasColumnName("disposition_state").HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => System.Text.Json.JsonSerializer.Deserialize<GAE.Core.Models.NpcDispositionState>(v, JsonDefaults.Options) ?? new());
        builder.Property(s => s.KnowledgeScopeOverrides).HasColumnName("knowledge_scope_overrides").HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, JsonDefaults.Options));

        builder.HasIndex(s => new { s.NpcId, s.WorldId, s.PlayerId }).IsUnique();
    }
}