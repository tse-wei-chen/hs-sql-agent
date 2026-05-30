using Admin.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admin.Service.Data.Configs;

public class DbSemanticConfig : IEntityTypeConfiguration<DbSemantic>
{
    public void Configure(EntityTypeBuilder<DbSemantic> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TableName).IsRequired().HasMaxLength(256);
        builder.Property(x => x.ColumnName).HasMaxLength(256);
        builder.Property(x => x.SchemaName).HasMaxLength(256);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.DisplayName).HasMaxLength(256);

        builder.HasOne(x => x.DbManagement)
            .WithMany()
            .HasForeignKey(x => x.DbManagementId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint: one description per table/column in a DB
        builder.HasIndex(x => new { x.DbManagementId, x.SchemaName, x.TableName, x.ColumnName }).IsUnique();
    }
}
