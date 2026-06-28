using Auth.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Auth.Service.Data.Configs;

public class MemberRoleConfig : IEntityTypeConfiguration<MemberRole>
{
    public void Configure(EntityTypeBuilder<MemberRole> builder)
    {
        builder.HasKey(x => new { x.MemberId, x.RoleId });

        builder.HasOne(x => x.Member)
            .WithMany(x => x.MemberRoles)
            .HasForeignKey(x => x.MemberId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Role)
            .WithMany(x => x.MemberRoles)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
