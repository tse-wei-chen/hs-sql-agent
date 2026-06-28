using Auth.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Auth.Service.Data.Configs;

public class TokenBlacklistEntryConfig : IEntityTypeConfiguration<TokenBlacklistEntry>
{
    public void Configure(EntityTypeBuilder<TokenBlacklistEntry> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Jti).IsRequired().HasMaxLength(128);
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.RevokedAt).IsRequired();
        builder.HasIndex(x => x.Jti).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
    }
}
