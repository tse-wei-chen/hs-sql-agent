using Admin.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admin.Service.Data.Configs;

public class DbSemanticRelationshipConfig : IEntityTypeConfiguration<DbSemanticRelationship>
{
    public void Configure(EntityTypeBuilder<DbSemanticRelationship> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Property(x => x.SourceSchema).HasMaxLength(256);
        builder.Property(x => x.SourceTable).IsRequired().HasMaxLength(256);
        builder.Property(x => x.SourceColumn).IsRequired().HasMaxLength(256);
        builder.Property(x => x.TargetSchema).HasMaxLength(256);
        builder.Property(x => x.TargetTable).IsRequired().HasMaxLength(256);
        builder.Property(x => x.TargetColumn).IsRequired().HasMaxLength(256);
        builder.Property(x => x.Cardinality).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Direction).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.HasIndex(x => new { x.DbManagementId, x.Name }).IsUnique();
        builder.HasOne(x => x.DbManagement).WithMany().HasForeignKey(x => x.DbManagementId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class DbSemanticMetricConfig : IEntityTypeConfiguration<DbSemanticMetric>
{
    public void Configure(EntityTypeBuilder<DbSemanticMetric> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SchemaName).HasMaxLength(256);
        builder.Property(x => x.TableName).IsRequired().HasMaxLength(256);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(128);
        builder.Property(x => x.DisplayName).HasMaxLength(256);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.Formula).IsRequired().HasMaxLength(4000);
        builder.Property(x => x.Aggregation).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Grain).HasMaxLength(1000);
        builder.Property(x => x.Filter).HasMaxLength(2000);
        builder.Property(x => x.SynonymsJson).HasMaxLength(4000);
        builder.HasIndex(x => new { x.DbManagementId, x.SchemaName, x.TableName, x.Name }).IsUnique();
        builder.HasOne(x => x.DbManagement).WithMany().HasForeignKey(x => x.DbManagementId).OnDelete(DeleteBehavior.Cascade);
    }
}
