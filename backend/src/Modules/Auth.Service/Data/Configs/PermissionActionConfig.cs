using Auth.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Auth.Service.Data.Configs;

public class PermissionActionConfig : IEntityTypeConfiguration<PermissionAction>
{
    public void Configure(EntityTypeBuilder<PermissionAction> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.RoleId, x.PermissionId, x.ActionId }).IsUnique();

        builder.HasOne(x => x.Role)
            .WithMany(x => x.PermissionActions)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Permission)
            .WithMany(x => x.PermissionActions)
            .HasForeignKey(x => x.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Action)
            .WithMany(x => x.PermissionActions)
            .HasForeignKey(x => x.ActionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
