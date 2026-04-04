using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modules.Data.Entites;

namespace Modules.Data.Configs;

public class SuperUserConfig : IEntityTypeConfiguration<SuperUser>
{
    public void Configure(EntityTypeBuilder<SuperUser> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Username).IsRequired();
        builder.Property(e => e.Mail).IsRequired();
        builder.Property(e => e.PasswordHash).IsRequired();
    }
}