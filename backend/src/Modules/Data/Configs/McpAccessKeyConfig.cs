using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modules.Data.Entites;

namespace Modules.Data.Configs;

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
        builder.Property(x => x.SqlConnectionString).HasMaxLength(4000);
        builder.Property(x => x.CreatedBy).HasMaxLength(64);
        builder.Property(x => x.RevokedBy).HasMaxLength(64);
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => x.KeyHash).IsUnique();
        builder.HasIndex(x => x.KeyPrefix);
        builder.HasIndex(x => x.IsActive);
    }
}
