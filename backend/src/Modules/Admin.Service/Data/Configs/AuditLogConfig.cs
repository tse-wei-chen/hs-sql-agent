using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Admin.Service.Data.Entites;

namespace Admin.Service.Data.Configs;

public class AuditLogConfig : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EventId).IsRequired();
        builder.Property(x => x.ActorType).IsRequired().HasMaxLength(32);
        builder.Property(x => x.ActorId).HasMaxLength(64);
        builder.Property(x => x.Action).IsRequired().HasMaxLength(128);
        builder.Property(x => x.Target).IsRequired().HasMaxLength(256);
        builder.Property(x => x.Result).IsRequired().HasMaxLength(32);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(512);
        builder.Property(x => x.RequestId).HasMaxLength(128);
        builder.Property(x => x.SessionId).HasMaxLength(128);
        builder.Property(x => x.DatabaseName).HasMaxLength(256);
        builder.Property(x => x.ToolName).HasMaxLength(128);
        builder.Property(x => x.Operation).HasMaxLength(32);
        builder.Property(x => x.ApprovalStatus).HasMaxLength(32);
        builder.Property(x => x.ErrorCategory).HasMaxLength(128);
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => x.EventId).IsUnique();
        builder.HasIndex(x => x.CreatedAt);
        builder.HasIndex(x => x.Action);
        builder.HasIndex(x => x.AccessKeyId);
        builder.HasIndex(x => x.DbManagementId);
        builder.HasIndex(x => x.ToolName);
    }
}
