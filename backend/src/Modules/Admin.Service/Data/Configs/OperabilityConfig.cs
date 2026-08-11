using Admin.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admin.Service.Data.Configs;

public class DbHealthStateConfig : IEntityTypeConfiguration<DbHealthState>
{
    public void Configure(EntityTypeBuilder<DbHealthState> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(24);
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.HasIndex(x => x.DbManagementId).IsUnique();
        builder.HasOne<DbManagement>().WithOne().HasForeignKey<DbHealthState>(x => x.DbManagementId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class RateLimitMetricConfig : IEntityTypeConfiguration<RateLimitMetric>
{
    public void Configure(EntityTypeBuilder<RateLimitMetric> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Layer).IsRequired().HasMaxLength(32);
        builder.Property(x => x.ToolName).HasMaxLength(128);
        builder.HasIndex(x => x.BucketStart);
        builder.HasIndex(x => x.AccessKeyId);
        builder.HasIndex(x => x.DbManagementId);
    }
}

public class OutboundDeliveryConfig : IEntityTypeConfiguration<OutboundDelivery>
{
    public void Configure(EntityTypeBuilder<OutboundDelivery> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Category).IsRequired().HasMaxLength(32);
        builder.Property(x => x.DedupeKey).IsRequired().HasMaxLength(250);
        builder.Property(x => x.TargetUrl).IsRequired().HasMaxLength(2000);
        builder.Property(x => x.Payload).IsRequired();
        builder.Property(x => x.Status).IsRequired().HasMaxLength(24).IsConcurrencyToken();
        builder.Property(x => x.LastAttemptAt).IsConcurrencyToken();
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.HasIndex(x => x.DedupeKey).IsUnique();
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt });
    }
}
