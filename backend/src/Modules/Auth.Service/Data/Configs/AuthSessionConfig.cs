using Auth.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Auth.Service.Data.Configs;

public class AuthSessionConfig : IEntityTypeConfiguration<AuthSession>
{
    public void Configure(EntityTypeBuilder<AuthSession> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CurrentRefreshTokenHash).IsRequired().HasMaxLength(64).IsConcurrencyToken();
        builder.Property(x => x.RevocationReason).HasMaxLength(128);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(512);
        builder.HasIndex(x => x.MemberId);
        builder.HasIndex(x => x.TokenFamilyId);
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne(x => x.Member)
            .WithMany(x => x.AuthSessions)
            .HasForeignKey(x => x.MemberId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
