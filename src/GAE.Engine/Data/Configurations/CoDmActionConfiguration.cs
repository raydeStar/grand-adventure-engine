using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GAE.Engine.Data.Configurations;

/// <summary>Persists Co-DM proposals, idempotency receipts, approval state, and the human audit trail.</summary>
public class CoDmActionEntity
{
    public string Id { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string ActionKind { get; set; } = string.Empty;
    public string TargetPlayerId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "[]";
    public string PayloadJson { get; set; } = "{}";
    public string? ApprovalTokenHash { get; set; }
    public string Status { get; set; } = "pending";
    public string ProposedBy { get; set; } = string.Empty;
    public string? DecidedBy { get; set; }
    public string? ResultSummary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public long Version { get; set; }
}

/// <summary>Maps the durable Co-DM review queue and guards request IDs and optimistic transitions.</summary>
public class CoDmActionConfiguration : IEntityTypeConfiguration<CoDmActionEntity>
{
    public void Configure(EntityTypeBuilder<CoDmActionEntity> builder)
    {
        builder.ToTable("co_dm_actions");
        builder.HasKey(action => action.Id);
        builder.Property(action => action.Id).HasColumnName("id");
        builder.Property(action => action.RequestId).HasColumnName("request_id").IsRequired();
        builder.Property(action => action.ActionType).HasColumnName("action_type").IsRequired();
        builder.Property(action => action.ActionKind).HasColumnName("action_kind").IsRequired();
        builder.Property(action => action.TargetPlayerId).HasColumnName("target_player_id").IsRequired();
        builder.Property(action => action.Title).HasColumnName("title").IsRequired();
        builder.Property(action => action.Summary).HasColumnName("summary").IsRequired();
        builder.Property(action => action.Rationale).HasColumnName("rationale").IsRequired();
        builder.Property(action => action.EvidenceJson).HasColumnName("evidence_json").HasColumnType("jsonb").IsRequired();
        builder.Property(action => action.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
        builder.Property(action => action.ApprovalTokenHash).HasColumnName("approval_token_hash");
        builder.Property(action => action.Status).HasColumnName("status").IsRequired();
        builder.Property(action => action.ProposedBy).HasColumnName("proposed_by").IsRequired();
        builder.Property(action => action.DecidedBy).HasColumnName("decided_by");
        builder.Property(action => action.ResultSummary).HasColumnName("result_summary");
        builder.Property(action => action.CreatedAt).HasColumnName("created_at");
        builder.Property(action => action.DecidedAt).HasColumnName("decided_at");
        builder.Property(action => action.Version).HasColumnName("version").IsConcurrencyToken();

        builder.HasIndex(action => new { action.ProposedBy, action.RequestId }).IsUnique();
        builder.HasIndex(action => new { action.Status, action.CreatedAt });
        builder.HasIndex(action => action.TargetPlayerId);
    }
}
