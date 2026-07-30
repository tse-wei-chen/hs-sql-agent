using Admin.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admin.Service.Data.Configs;

public class SecurityPolicySettingsConfig : IEntityTypeConfiguration<SecurityPolicySettings>
{
    public void Configure(EntityTypeBuilder<SecurityPolicySettings> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UpdatedBy).HasMaxLength(64);
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.HasData(new SecurityPolicySettings
        {
            Id = SecurityPolicySettings.SingletonId,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
    }
}
