using Xunit;

namespace SqlAgent.Test.Services;

public class CoreAliasSemanticsTests
{
    [Fact]
    public void StructuredDtoAliases_AreUnquotedButProjectionSpellingIsExplicit()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            Alias = "UserScope",
            SelectColumns =
            [
                new FieldSelectCondition
                {
                    FieldName = "id",
                    Alias = "DisplayName"
                }
            ]
        };

        var select = Assert.IsType<SelectStatement>(QueryDefinitionCoreMapper.Map(definition));
        var sourceAlias = Assert.IsType<NamedTableSource>(select.From).Alias;
        var projectionAlias = Assert.Single(select.Select).Alias;

        Assert.NotNull(sourceAlias);
        Assert.Equal("UserScope", sourceAlias.Value);
        Assert.False(sourceAlias.WasQuoted);
        Assert.False(sourceAlias.PreserveSpelling);

        Assert.NotNull(projectionAlias);
        Assert.Equal("DisplayName", projectionAlias.Value);
        Assert.False(projectionAlias.WasQuoted);
        Assert.True(projectionAlias.PreserveSpelling);
    }
}
