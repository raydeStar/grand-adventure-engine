using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GAE.Engine.Data;

/// <summary>
/// A self-registered dashboard account. Passwords are stored as PBKDF2 hashes, never in clear text.
/// The two configuration-backed accounts (user/admin) are not stored here.
/// </summary>
public class DashboardUserEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = string.Empty;
    /// <summary>Lower-cased, trimmed username used for uniqueness and lookup.</summary>
    public string NormalizedUsername { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "user";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public class DashboardUserConfiguration : IEntityTypeConfiguration<DashboardUserEntity>
{
    public void Configure(EntityTypeBuilder<DashboardUserEntity> builder)
    {
        builder.ToTable("dashboard_users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id");
        builder.Property(u => u.Username).HasColumnName("username").IsRequired();
        builder.Property(u => u.NormalizedUsername).HasColumnName("normalized_username").IsRequired();
        builder.HasIndex(u => u.NormalizedUsername).IsUnique();
        builder.Property(u => u.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").IsRequired();
        builder.Property(u => u.Role).HasColumnName("role").IsRequired();
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");
        builder.Property(u => u.LastLoginAt).HasColumnName("last_login_at");
    }
}
