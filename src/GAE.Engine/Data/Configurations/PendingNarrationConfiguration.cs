using GAE.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GAE.Engine.Data.Configurations;

/// <summary>EF Core entity for the deferred-narration queue.</summary>
public class PendingNarrationEntity
{
    public string Id { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string WorldId { get; set; } = WorldDefaults.DefaultWorldId;
    public string? RoomId { get; set; }
    public long Sequence { get; set; }
    public string Operation { get; set; } = "action";
    public string ContextJson { get; set; } = string.Empty;
    public string PlaceholderNarration { get; set; } = string.Empty;
    public string? Narration { get; set; }
    public PendingNarrationStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// EF Core configuration for the deferred-narration queue.
///
/// The indexes exist for the two queries the worker actually runs: claim the next due item for any
/// player in sequence order, and look up outstanding work for one player.
/// </summary>
public class PendingNarrationConfiguration : IEntityTypeConfiguration<PendingNarrationEntity>
{
    public void Configure(EntityTypeBuilder<PendingNarrationEntity> builder)
    {
        builder.ToTable("pending_narrations");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasColumnName("id");
        builder.Property(n => n.ActionId).HasColumnName("action_id").IsRequired();
        builder.Property(n => n.PlayerId).HasColumnName("player_id").IsRequired();
        builder.Property(n => n.WorldId).HasColumnName("world_id").IsRequired();
        builder.Property(n => n.RoomId).HasColumnName("room_id");
        builder.Property(n => n.Sequence).HasColumnName("sequence");
        builder.Property(n => n.Operation).HasColumnName("operation").IsRequired();
        builder.Property(n => n.ContextJson).HasColumnName("context_json").HasColumnType("jsonb").IsRequired();
        builder.Property(n => n.PlaceholderNarration).HasColumnName("placeholder_narration");
        builder.Property(n => n.Narration).HasColumnName("narration");
        builder.Property(n => n.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        builder.Property(n => n.AttemptCount).HasColumnName("attempt_count");
        builder.Property(n => n.NextAttemptAt).HasColumnName("next_attempt_at");
        builder.Property(n => n.CreatedAt).HasColumnName("created_at");
        builder.Property(n => n.CompletedAt).HasColumnName("completed_at");
        builder.Property(n => n.LastError).HasColumnName("last_error");

        // The worker's claim query: outstanding items, oldest first within a player.
        builder.HasIndex(n => new { n.Status, n.NextAttemptAt, n.PlayerId, n.Sequence });
        builder.HasIndex(n => n.ActionId);
    }
}
