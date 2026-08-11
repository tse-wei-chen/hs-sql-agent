using Auth.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Auth.Service.Data.Configs;

public class ExternalIdentityConfig : IEntityTypeConfiguration<ExternalIdentity>
{
    public void Configure(EntityTypeBuilder<ExternalIdentity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Subject).IsRequired().HasMaxLength(512);
        builder.HasIndex(x => new { x.Provider, x.Subject }).IsUnique();
        builder.HasOne(x => x.Member).WithMany(x => x.ExternalIdentities).HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ExternalLoginCodeConfig : IEntityTypeConfiguration<ExternalLoginCode>
{
    public void Configure(EntityTypeBuilder<ExternalLoginCode> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CodeHash).IsRequired().HasMaxLength(64);
        builder.Property(x => x.UsedAt).IsConcurrencyToken();
        builder.HasIndex(x => x.CodeHash).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class MfaRecoveryCodeConfig : IEntityTypeConfiguration<MfaRecoveryCode>
{
    public void Configure(EntityTypeBuilder<MfaRecoveryCode> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CodeHash).IsRequired().HasMaxLength(64);
        builder.Property(x => x.UsedAt).IsConcurrencyToken();
        builder.HasIndex(x => new { x.MemberId, x.CodeHash }).IsUnique();
        builder.HasOne(x => x.Member).WithMany(x => x.MfaRecoveryCodes).HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
    }
}
