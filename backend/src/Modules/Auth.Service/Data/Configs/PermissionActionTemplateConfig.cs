using Auth.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Auth.Service.Data.Configs;

public class PermissionActionTemplateConfig : IEntityTypeConfiguration<PermissionActionTemplate>
{
    public void Configure(EntityTypeBuilder<PermissionActionTemplate> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PermissionId).IsRequired();
        builder.Property(x => x.ActionId).IsRequired();

        builder.HasIndex(x => new { x.PermissionId, x.ActionId }).IsUnique();

        builder.HasOne(x => x.Permission)
            .WithMany(x => x.PermissionActionTemplates)
            .HasForeignKey(x => x.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Action)
            .WithMany(x => x.PermissionActionTemplates)
            .HasForeignKey(x => x.ActionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasData(
            new PermissionActionTemplate { Id = 1, PermissionId = 1, ActionId = 1},
            new PermissionActionTemplate { Id = 2, PermissionId = 2, ActionId = 1},
            new PermissionActionTemplate { Id = 3, PermissionId = 2, ActionId = 2},
            new PermissionActionTemplate { Id = 4, PermissionId = 2, ActionId = 5},
            new PermissionActionTemplate { Id = 5, PermissionId = 3, ActionId = 1},
            new PermissionActionTemplate { Id = 6, PermissionId = 3, ActionId = 2},
            new PermissionActionTemplate { Id = 7, PermissionId = 3, ActionId = 3},
            new PermissionActionTemplate { Id = 8, PermissionId = 3, ActionId = 4},
            new PermissionActionTemplate { Id = 9, PermissionId = 4, ActionId = 1},
            new PermissionActionTemplate { Id = 10, PermissionId = 4, ActionId = 2},
            new PermissionActionTemplate { Id = 11, PermissionId = 4, ActionId = 3},
            new PermissionActionTemplate { Id = 12, PermissionId = 4, ActionId = 4},
            new PermissionActionTemplate { Id = 13, PermissionId = 5, ActionId = 1},
            new PermissionActionTemplate { Id = 14, PermissionId = 6, ActionId = 1},
            new PermissionActionTemplate { Id = 15, PermissionId = 6, ActionId = 2},
            new PermissionActionTemplate { Id = 16, PermissionId = 6, ActionId = 3},
            new PermissionActionTemplate { Id = 17, PermissionId = 6, ActionId = 4},
            new PermissionActionTemplate { Id = 18, PermissionId = 7, ActionId = 1},
            new PermissionActionTemplate { Id = 19, PermissionId = 7, ActionId = 2},
            new PermissionActionTemplate { Id = 20, PermissionId = 7, ActionId = 3},
            new PermissionActionTemplate { Id = 21, PermissionId = 7, ActionId = 4},
            new PermissionActionTemplate { Id = 22, PermissionId = 8, ActionId = 1},
            new PermissionActionTemplate { Id = 23, PermissionId = 8, ActionId = 3},
            new PermissionActionTemplate { Id = 24, PermissionId = 9, ActionId = 1},
            new PermissionActionTemplate { Id = 25, PermissionId = 9, ActionId = 3},
            new PermissionActionTemplate { Id = 26, PermissionId = 2, ActionId = 3},
            new PermissionActionTemplate { Id = 27, PermissionId = 5, ActionId = 3},
            new PermissionActionTemplate { Id = 28, PermissionId = 5, ActionId = 6},
            new PermissionActionTemplate { Id = 29, PermissionId = 10, ActionId = 1},
            new PermissionActionTemplate { Id = 30, PermissionId = 10, ActionId = 3}
        );
    }
}
