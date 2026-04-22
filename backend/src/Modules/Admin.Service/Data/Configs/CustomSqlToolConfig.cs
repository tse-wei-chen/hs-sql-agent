using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Admin.Service.Data.Entites;

namespace Admin.Service.Data.Configs;
public class CustomSqlToolConfig : IEntityTypeConfiguration<CustomSqlTool>
{
    public void Configure(EntityTypeBuilder<CustomSqlTool> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Description)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(x => x.DefinitionJson)
            .IsRequired();

        builder.Property(x => x.Type)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(x => x.ParametersJson);

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.Property(x => x.LastModifiedAt);
    }
}
