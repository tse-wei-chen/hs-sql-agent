using Auth.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Auth.Service.Data.Configs;

public class MemberConfig : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Username).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Mail).IsRequired().HasMaxLength(320);
        builder.Property(x => x.PasswordHash).IsRequired().HasMaxLength(512);
        builder.Property(x => x.IsActive).IsRequired().HasDefaultValue(true);
        builder.Property(x => x.SecurityVersion).IsRequired().HasDefaultValue(1);
        builder.HasIndex(x => x.Mail).IsUnique();
    }
}
