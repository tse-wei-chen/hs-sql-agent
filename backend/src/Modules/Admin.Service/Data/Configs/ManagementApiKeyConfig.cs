using Admin.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admin.Service.Data.Configs;

public sealed class ManagementApiKeyConfig : IEntityTypeConfiguration<ManagementApiKey>
{
    public void Configure(EntityTypeBuilder<ManagementApiKey> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.KeyPrefix).IsRequired().HasMaxLength(24);
        builder.Property(x => x.KeyHash).IsRequired().HasMaxLength(64);
        builder.Property(x => x.PermissionsJson).IsRequired().HasMaxLength(4000);
        builder.Property(x => x.CreatedBy).HasMaxLength(128);
        builder.Property(x => x.RevokedBy).HasMaxLength(128);
        builder.HasIndex(x => x.KeyPrefix);
        builder.HasIndex(x => x.KeyHash).IsUnique();
    }
}
