using Auth.Service.Authorization;
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
            new Permission { Id = 1, Name = "Overview", Path = PermissionCanonicalPaths.Overview },
            new Permission { Id = 2, Name = "MCP Keys", Path = PermissionCanonicalPaths.McpKeys },
            new Permission { Id = 3, Name = "Custom Tools", Path = PermissionCanonicalPaths.CustomTools },
            new Permission { Id = 4, Name = "DB Management", Path = PermissionCanonicalPaths.DbManagement },
            new Permission { Id = 5, Name = "Audit", Path = PermissionCanonicalPaths.Audit },
            new Permission { Id = 6, Name = "Role Management", Path = PermissionCanonicalPaths.Roles },
            new Permission { Id = 7, Name = "User Management", Path = PermissionCanonicalPaths.Users },
            new Permission { Id = 8, Name = "Semantic Layer", Path = PermissionCanonicalPaths.DbSemantic },
            new Permission { Id = 9, Name = "Security Policy", Path = PermissionCanonicalPaths.Security },
            new Permission { Id = 10, Name = "Operability", Path = PermissionCanonicalPaths.Operability }
        );
    }
}
