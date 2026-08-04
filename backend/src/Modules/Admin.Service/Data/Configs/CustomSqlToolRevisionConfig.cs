using Admin.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Admin.Service.Data.Configs;

public class CustomSqlToolRevisionConfig : IEntityTypeConfiguration<CustomSqlToolRevision>
{
    public void Configure(EntityTypeBuilder<CustomSqlToolRevision> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.CustomSqlToolId, x.RevisionNumber }).IsUnique();
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Description).IsRequired().HasMaxLength(500);
        builder.Property(x => x.SqlTemplate).IsRequired();
        builder.Property(x => x.Type).IsRequired().HasMaxLength(20);
        builder.Property(x => x.DiffJson).IsRequired();
        builder.Property(x => x.PublishedBy).HasMaxLength(200);
        builder.Property(x => x.PublishedAt).IsRequired();

        builder.HasOne(x => x.CustomSqlTool)
            .WithMany(x => x.Revisions)
            .HasForeignKey(x => x.CustomSqlToolId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.DbManagement)
            .WithMany()
            .HasForeignKey(x => x.DbManagementId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
