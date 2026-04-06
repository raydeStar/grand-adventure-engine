using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GAE.Engine.Data.Configurations;

/// <summary>EF Core configuration for the content_registry table — stores all game content as JSONB.</summary>
public class ContentRegistryConfiguration : IEntityTypeConfiguration<ContentRegistryEntity>
{
    public void Configure(EntityTypeBuilder<ContentRegistryEntity> builder)
    {
        builder.ToTable("content_registry");
        builder.HasKey(e => new { e.ContentType, e.Id });

        builder.Property(e => e.ContentType).HasColumnName("content_type").IsRequired();
        builder.Property(e => e.Id).HasColumnName("id").IsRequired();
        builder.Property(e => e.Name).HasColumnName("name").IsRequired();
        builder.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb").IsRequired();

        builder.HasIndex(e => e.ContentType).HasDatabaseName("ix_content_registry_type");
        builder.HasIndex(e => new { e.ContentType, e.Name }).HasDatabaseName("ix_content_registry_type_name");
    }
}

/// <summary>EF Core configuration for the game_config table — stores singleton config objects.</summary>
public class GameConfigConfiguration : IEntityTypeConfiguration<GameConfigEntity>
{
    public void Configure(EntityTypeBuilder<GameConfigEntity> builder)
    {
        builder.ToTable("game_config");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired();
        builder.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
    }
}
