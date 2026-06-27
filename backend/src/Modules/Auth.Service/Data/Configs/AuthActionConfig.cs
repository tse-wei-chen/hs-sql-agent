using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Auth.Service.Data.Entites;

namespace Auth.Service.Data.Configs;

public class AuthActionConfig : IEntityTypeConfiguration<AuthAction>
{
    public void Configure(EntityTypeBuilder<AuthAction> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.HasIndex(x => x.Code).IsUnique();

        builder.HasData(
            new AuthAction { Id = 1, Code = "view", Name = "view" },
            new AuthAction { Id = 2, Code = "create", Name = "create" },
            new AuthAction { Id = 3, Code = "edit", Name = "edit" },
            new AuthAction { Id = 4, Code = "delete", Name = "delete" },
            new AuthAction { Id = 5, Code = "revoke", Name = "revoke" }
        );
    }
}
