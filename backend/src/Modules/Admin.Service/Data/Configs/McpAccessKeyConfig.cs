using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Admin.Service.Data.Entites;

namespace Admin.Service.Data.Configs;

public class McpAccessKeyConfig : IEntityTypeConfiguration<McpAccessKey>
{
    public void Configure(EntityTypeBuilder<McpAccessKey> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.KeyPrefix).IsRequired().HasMaxLength(16);
        builder.Property(x => x.KeyHash).IsRequired().HasMaxLength(128);
        builder.Property(x => x.AllowedTools).HasMaxLength(2048);
        builder.Property(x => x.CorsAllowedOrigins).HasMaxLength(4000);
        builder.Property(x => x.SqlProvider).HasMaxLength(32);
        builder.Property(x => x.DbManagementId);
        builder.Property(x => x.TableWhitelist);
        builder.Property(x => x.RateLimitMode)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(Admin.Service.Models.McpKeyRateLimitMode.Inherit);
        builder.Property(x => x.PermitLimitOverride);
        builder.Property(x => x.WindowSecondsOverride);
        builder.Property(x => x.CreatedBy).HasMaxLength(64);
        builder.Property(x => x.RevokedBy).HasMaxLength(64);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.RevokedAt);

        builder.HasIndex(x => x.KeyHash).IsUnique();
        builder.HasIndex(x => x.KeyPrefix);
        builder.HasIndex(x => x.IsActive);
    }
}
