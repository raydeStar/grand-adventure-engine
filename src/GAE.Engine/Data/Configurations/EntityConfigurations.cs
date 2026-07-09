using System.Text.Json;
using GAE.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GAE.Engine.Data.Configurations;

/// <summary>Shared JSON serializer options for JSONB columns — camelCase, lenient.</summary>
internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}

public class PlayerConfiguration : IEntityTypeConfiguration<PlayerEntity>
{
    public void Configure(EntityTypeBuilder<PlayerEntity> builder)
    {
        builder.ToTable("players");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id");
        builder.Property(p => p.Name).HasColumnName("name").IsRequired();
        builder.Property(p => p.Gender).HasColumnName("gender");
        builder.Property(p => p.Race).HasColumnName("race").IsRequired();
        builder.Property(p => p.Class).HasColumnName("class").IsRequired();
        builder.Property(p => p.Faction).HasColumnName("faction").IsRequired();
        builder.Property(p => p.ActiveWorldId).HasColumnName("active_world_id")
            .HasDefaultValue(WorldDefaults.DefaultWorldId)
            .IsRequired();
        builder.Property(p => p.HomeWorldId).HasColumnName("home_world_id")
            .HasDefaultValue(WorldDefaults.DefaultWorldId)
            .IsRequired();
        builder.Property(p => p.Backstory).HasColumnName("backstory");
        builder.Property(p => p.DiscordId).HasColumnName("discord_id");
        builder.Property(p => p.ThreadId).HasColumnName("thread_id");
        builder.Property(p => p.HasCompletedDemo).HasColumnName("has_completed_demo");
        builder.Property(p => p.CurrentRoomId).HasColumnName("current_room_id");
        builder.Property(p => p.Hp).HasColumnName("hp");
        builder.Property(p => p.MaxHp).HasColumnName("max_hp");
        builder.Property(p => p.Mp).HasColumnName("mp");
        builder.Property(p => p.MaxMp).HasColumnName("max_mp");
        builder.Property(p => p.Gold).HasColumnName("gold");
        builder.Property(p => p.Xp).HasColumnName("xp");
        builder.Property(p => p.Level).HasColumnName("level");
        builder.Property(p => p.Str).HasColumnName("str");
        builder.Property(p => p.Dex).HasColumnName("dex");
        builder.Property(p => p.Con).HasColumnName("con");
        builder.Property(p => p.Int).HasColumnName("int");
        builder.Property(p => p.Wis).HasColumnName("wis");
        builder.Property(p => p.Cha).HasColumnName("cha");
        builder.Property(p => p.Luck).HasColumnName("luck");
        builder.Property(p => p.GameMode).HasColumnName("game_mode")
            .HasConversion<string>()
            .HasDefaultValue(GameMode.FullRpg)
            .IsRequired();
        builder.Property(p => p.CreatedAt).HasColumnName("created_at");
        builder.Property(p => p.LastActiveAt).HasColumnName("last_active_at");

        // JSONB columns for complex nested structures
        builder.Property(p => p.BlindAdventure).HasColumnName("blind_adventure").HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => v == null ? null : JsonSerializer.Deserialize<BlindAdventureSession>(v, JsonDefaults.Options));

        builder.Property(p => p.CyoaState).HasColumnName("cyoa_state").HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => v == null ? null : JsonSerializer.Deserialize<CyoaState>(v, JsonDefaults.Options));

        builder.Property(p => p.Equipment).HasColumnName("equipment").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<EquipmentLoadout>(v, JsonDefaults.Options) ?? new EquipmentLoadout());

        builder.Property(p => p.Inventory).HasColumnName("inventory").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<InventoryItem>>(v, JsonDefaults.Options) ?? new List<InventoryItem>());

        builder.Property(p => p.StatusEffects).HasColumnName("status_effects").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<StatusEffect>>(v, JsonDefaults.Options) ?? new List<StatusEffect>());

        builder.Property(p => p.Spellbook).HasColumnName("spellbook").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<LearnedSpell>>(v, JsonDefaults.Options) ?? new List<LearnedSpell>());

        builder.Property(p => p.QuestLog).HasColumnName("quest_log").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<QuestProgress>>(v, JsonDefaults.Options) ?? new List<QuestProgress>());

        builder.Property(p => p.Interaction).HasColumnName("interaction").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<InteractionState>(v, JsonDefaults.Options) ?? new InteractionState());

        // Lore & narrator
        builder.Property(p => p.DiscoveredLore).HasColumnName("discovered_lore").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<string>>(v, JsonDefaults.Options) ?? new List<string>());
        builder.Property(p => p.NarratorPresetId).HasColumnName("narrator_preset_id");

        // Indexes
        builder.HasIndex(p => p.DiscordId).IsUnique().HasDatabaseName("ix_players_discord_id")
            .HasFilter("discord_id IS NOT NULL");
        builder.HasIndex(p => p.CurrentRoomId).HasDatabaseName("ix_players_current_room_id");
    }
}

public class RoomConfiguration : IEntityTypeConfiguration<RoomEntity>
{
    public void Configure(EntityTypeBuilder<RoomEntity> builder)
    {
        builder.ToTable("rooms");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Name).HasColumnName("name").IsRequired();
        builder.Property(r => r.Description).HasColumnName("description");
        builder.Property(r => r.IsTemplate).HasColumnName("is_template").HasDefaultValue(true);
        builder.Property(r => r.IsDiscovered).HasColumnName("is_discovered");
        builder.Property(r => r.AsciiArt).HasColumnName("ascii_art");
        builder.Property(r => r.DiscoveredAt).HasColumnName("discovered_at");

        builder.Property(r => r.WorldIds).HasColumnName("world_ids").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<string>>(v, JsonDefaults.Options) ?? new List<string> { WorldDefaults.DefaultWorldId })
            .HasDefaultValueSql("'[\"default-world\"]'::jsonb");

        builder.Property(r => r.Exits).HasColumnName("exits").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonDefaults.Options) ?? new Dictionary<string, string>());

        builder.Property(r => r.Npcs).HasColumnName("npcs").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<Npc>>(v, JsonDefaults.Options) ?? new List<Npc>());

        builder.Property(r => r.Items).HasColumnName("items").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<InventoryItem>>(v, JsonDefaults.Options) ?? new List<InventoryItem>());

        builder.Property(r => r.EnvironmentTags).HasColumnName("environment_tags").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<string>>(v, JsonDefaults.Options) ?? new List<string>());

        builder.HasIndex(r => r.IsTemplate).HasDatabaseName("ix_rooms_is_template")
            .HasFilter("is_template = true");
    }
}

public class PlayerRoomConfiguration : IEntityTypeConfiguration<PlayerRoomEntity>
{
    public void Configure(EntityTypeBuilder<PlayerRoomEntity> builder)
    {
        builder.ToTable("player_rooms");
        builder.HasKey(pr => pr.Id);
        builder.Property(pr => pr.Id).HasColumnName("id");
        builder.Property(pr => pr.PlayerId).HasColumnName("player_id").IsRequired();
        builder.Property(pr => pr.RoomId).HasColumnName("room_id").IsRequired();
        builder.Property(pr => pr.WorldId).HasColumnName("world_id")
            .HasDefaultValue(WorldDefaults.DefaultWorldId)
            .IsRequired();
        builder.Property(pr => pr.Name).HasColumnName("name").IsRequired();
        builder.Property(pr => pr.Description).HasColumnName("description");
        builder.Property(pr => pr.IsDiscovered).HasColumnName("is_discovered");
        builder.Property(pr => pr.AsciiArt).HasColumnName("ascii_art");
        builder.Property(pr => pr.DiscoveredAt).HasColumnName("discovered_at");

        builder.Property(pr => pr.Exits).HasColumnName("exits").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonDefaults.Options) ?? new Dictionary<string, string>());

        builder.Property(pr => pr.Npcs).HasColumnName("npcs").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<Npc>>(v, JsonDefaults.Options) ?? new List<Npc>());

        builder.Property(pr => pr.Items).HasColumnName("items").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<InventoryItem>>(v, JsonDefaults.Options) ?? new List<InventoryItem>());

        builder.Property(pr => pr.EnvironmentTags).HasColumnName("environment_tags").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<List<string>>(v, JsonDefaults.Options) ?? new List<string>());

        builder.HasIndex(pr => pr.PlayerId).HasDatabaseName("ix_player_rooms_player_id");
        builder.HasIndex(pr => new { pr.PlayerId, pr.RoomId, pr.WorldId }).IsUnique()
            .HasDatabaseName("ix_player_rooms_player_room");
    }
}

public class StoryEntryConfiguration : IEntityTypeConfiguration<StoryEntryEntity>
{
    public void Configure(EntityTypeBuilder<StoryEntryEntity> builder)
    {
        builder.ToTable("story_entries");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.ActionId).HasColumnName("action_id");
        builder.Property(s => s.RawInput).HasColumnName("raw_input");
        builder.Property(s => s.PlayerId).HasColumnName("player_id");
        builder.Property(s => s.WorldId).HasColumnName("world_id")
            .HasDefaultValue(WorldDefaults.DefaultWorldId)
            .IsRequired();
        builder.Property(s => s.RoomId).HasColumnName("room_id");
        builder.Property(s => s.MechanicalSummary).HasColumnName("mechanical_summary");
        builder.Property(s => s.Narration).HasColumnName("narration");
        builder.Property(s => s.Timestamp).HasColumnName("timestamp");

        builder.HasIndex(s => s.PlayerId).HasDatabaseName("ix_story_entries_player_id");
        builder.HasIndex(s => s.WorldId).HasDatabaseName("ix_story_entries_world_id");
        builder.HasIndex(s => s.RoomId).HasDatabaseName("ix_story_entries_room_id");
        builder.HasIndex(s => s.Timestamp).IsDescending().HasDatabaseName("ix_story_entries_timestamp");
    }
}

public class CombatStateConfiguration : IEntityTypeConfiguration<CombatStateEntity>
{
    public void Configure(EntityTypeBuilder<CombatStateEntity> builder)
    {
        builder.ToTable("combat_states");
        builder.HasKey(c => new { c.RoomId, c.WorldId });
        builder.Property(c => c.RoomId).HasColumnName("room_id");
        builder.Property(c => c.WorldId).HasColumnName("world_id")
            .HasDefaultValue(WorldDefaults.DefaultWorldId)
            .IsRequired();
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");

        builder.Property(c => c.State).HasColumnName("state").HasColumnType("jsonb").IsRequired()
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<CombatState>(v, JsonDefaults.Options) ?? new());
    }
}

public class PartyQuestConfiguration : IEntityTypeConfiguration<PartyQuestEntity>
{
    public void Configure(EntityTypeBuilder<PartyQuestEntity> builder)
    {
        builder.ToTable("party_quests");
        builder.HasKey(p => p.GroupId);
        builder.Property(p => p.GroupId).HasColumnName("group_id");
        builder.Property(p => p.CreatedAt).HasColumnName("created_at");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");

        builder.Property(p => p.State).HasColumnName("state").HasColumnType("jsonb").IsRequired()
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<PartyQuestProgress>(v, JsonDefaults.Options) ?? new PartyQuestProgress());
    }
}

public class GameEventConfiguration : IEntityTypeConfiguration<GameEventEntity>
{
    public void Configure(EntityTypeBuilder<GameEventEntity> builder)
    {
        builder.ToTable("game_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(e => e.EventId).HasColumnName("event_id");
        builder.Property(e => e.ActionId).HasColumnName("action_id");
        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id");
        builder.Property(e => e.Type).HasColumnName("type");
        builder.Property(e => e.PlayerId).HasColumnName("player_id");
        builder.Property(e => e.RoomId).HasColumnName("room_id");
        builder.Property(e => e.Summary).HasColumnName("summary");
        builder.Property(e => e.Narration).HasColumnName("narration");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, JsonDefaults.Options),
                v => JsonSerializer.Deserialize<Dictionary<string, object?>>(v, JsonDefaults.Options) ?? new());

        builder.HasIndex(e => e.Type).HasDatabaseName("ix_game_events_type");
        builder.HasIndex(e => e.PlayerId).HasDatabaseName("ix_game_events_player_id");
        builder.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_game_events_created_at");
    }
}

public class ConversationLogConfiguration : IEntityTypeConfiguration<ConversationLogEntity>
{
    public void Configure(EntityTypeBuilder<ConversationLogEntity> builder)
    {
        builder.ToTable("conversation_logs");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(c => c.LogId).HasColumnName("log_id");
        builder.Property(c => c.Operation).HasColumnName("operation");
        builder.Property(c => c.PlayerId).HasColumnName("player_id");
        builder.Property(c => c.RoomId).HasColumnName("room_id");
        builder.Property(c => c.Model).HasColumnName("model");
        builder.Property(c => c.SystemPrompt).HasColumnName("system_prompt");
        builder.Property(c => c.UserPrompt).HasColumnName("user_prompt");
        builder.Property(c => c.Response).HasColumnName("response");
        builder.Property(c => c.Temperature).HasColumnName("temperature");
        builder.Property(c => c.MaxTokens).HasColumnName("max_tokens");
        builder.Property(c => c.LatencyMs).HasColumnName("latency_ms");
        builder.Property(c => c.Success).HasColumnName("success");
        builder.Property(c => c.ErrorMessage).HasColumnName("error_message");
        builder.Property(c => c.Timestamp).HasColumnName("timestamp");

        builder.HasIndex(c => c.PlayerId).HasDatabaseName("ix_conversation_logs_player_id");
        builder.HasIndex(c => c.Timestamp).HasDatabaseName("ix_conversation_logs_timestamp");
    }
}
