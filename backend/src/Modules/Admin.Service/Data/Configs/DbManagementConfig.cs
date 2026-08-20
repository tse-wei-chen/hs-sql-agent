using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Admin.Service.Data.Entites;

namespace Admin.Service.Data.Configs;

public class DbManagementConfig : IEntityTypeConfiguration<DbManagement>
{
    public void Configure(EntityTypeBuilder<DbManagement> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.SqlProvider).HasMaxLength(50);
        builder.Property(x => x.Host).HasMaxLength(100);
        builder.Property(x => x.Port).HasMaxLength(10);
        builder.Property(x => x.Username).HasMaxLength(100);
        builder.Property(x => x.PasswordHash).HasMaxLength(256);
        builder.Property(x => x.Database).HasMaxLength(100);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.BootstrapId).HasMaxLength(100);
        builder.HasIndex(x => x.BootstrapId).IsUnique();
        builder.Property(x => x.UpdatedAt).IsRequired();
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);
    }
}
