using Auth.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Auth.Service.Data.Configs;

public class PermissionConfig : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Path).IsRequired().HasMaxLength(250);
        builder.HasIndex(x => x.Path).IsUnique();

        builder.HasData(
            new Permission { Id = 1, Name = "Overview", Path = "/home" },
            new Permission { Id = 2, Name = "MCP Keys", Path = "/runtime/mcp-keys" },
            new Permission { Id = 3, Name = "Custom Tools", Path = "/runtime/custom-tools" },
            new Permission { Id = 4, Name = "DB Management", Path = "/runtime/db-management" },
            new Permission { Id = 5, Name = "Audit", Path = "/runtime/audit" },
            new Permission { Id = 6, Name = "Role Management", Path = "/auth/role" },
            new Permission { Id = 7, Name = "User Management", Path = "/auth/user" }
        );
    }
}
