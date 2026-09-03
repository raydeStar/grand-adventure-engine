using GAE.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GAE.Engine.Data.Configurations;

/// <summary>Durable command gate kept separate from the player row to avoid lost-update races.</summary>
public class PlayerCommandHoldEntity
{
    public string PlayerId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string HeldBy { get; set; } = string.Empty;
    public DateTimeOffset HeldAt { get; set; }
    public string? SourceActionId { get; set; }

    /// <summary>Projects the persistence record into the transport-safe command-control model.</summary>
    public PlayerCommandHold ToDomain() => new()
    {
        PlayerId = PlayerId,
        Reason = Reason,
        HeldBy = HeldBy,
        HeldAt = HeldAt,
        SourceActionId = SourceActionId
    };
}

/// <summary>Maps one active command hold per player and removes it with the owning player.</summary>
public class PlayerCommandHoldConfiguration : IEntityTypeConfiguration<PlayerCommandHoldEntity>
{
    public void Configure(EntityTypeBuilder<PlayerCommandHoldEntity> builder)
    {
        builder.ToTable("player_command_holds");
        builder.HasKey(hold => hold.PlayerId);
        builder.Property(hold => hold.PlayerId).HasColumnName("player_id");
        builder.Property(hold => hold.Reason).HasColumnName("reason").IsRequired();
        builder.Property(hold => hold.HeldBy).HasColumnName("held_by").IsRequired();
        builder.Property(hold => hold.HeldAt).HasColumnName("held_at");
        builder.Property(hold => hold.SourceActionId).HasColumnName("source_action_id");
        builder.HasIndex(hold => hold.HeldAt);
        builder.HasOne<PlayerEntity>()
            .WithOne()
            .HasForeignKey<PlayerCommandHoldEntity>(hold => hold.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
