using Admin.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admin.Service.Data.Configs;

public sealed class DmlApprovalRequestStateConfig : IEntityTypeConfiguration<DmlApprovalRequestState>
{
    public void Configure(EntityTypeBuilder<DmlApprovalRequestState> builder)
    {
        builder.ToTable("DmlApprovalRequests");
        builder.HasKey(x => x.RequestId);
        builder.Property(x => x.RequestId).HasMaxLength(64);
        builder.Property(x => x.ApprovalFingerprint).IsRequired().HasMaxLength(64);
        builder.Property(x => x.EvidenceFingerprint).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(24).IsConcurrencyToken();
        builder.Property(x => x.ProtectedExecutionPayload).IsRequired();
        builder.Property(x => x.RequesterIdentity).IsRequired().HasMaxLength(256);
        builder.Property(x => x.TargetIdentity).IsRequired().HasMaxLength(256);
        builder.Property(x => x.DatabaseProvider).IsRequired().HasMaxLength(32);
        builder.Property(x => x.DatabaseIdentity).IsRequired().HasMaxLength(256);
        builder.Property(x => x.RequiredToolName).IsRequired().HasMaxLength(128);
        builder.Property(x => x.ExternalReference).HasMaxLength(500);
        builder.Property(x => x.ApproverIdentity).HasMaxLength(256);
        builder.Property(x => x.Reason).HasMaxLength(2000);
        builder.HasIndex(x => new { x.Status, x.ExpiresAt });
        builder.HasIndex(x => x.ExternalReference);
    }
}
