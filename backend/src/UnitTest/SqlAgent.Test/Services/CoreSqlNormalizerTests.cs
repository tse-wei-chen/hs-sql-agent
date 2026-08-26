using Xunit;

namespace SqlAgent.Test.Services;

public class CoreSqlNormalizerTests
{
    [Fact]
    public void Normalize_SemanticFunction_RenamesAcrossDialectsWithoutAmbientState()
    {
        var dto = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "LEN",
                    Arguments = [new FieldSelectCondition { FieldName = "name" }]
                }
            ]
        };
        var parsed = new ParsedStatement(QueryDefinitionCoreMapper.Map(dto), SqlAgentToolType.MsSqlServer);
        var bound = new SqlAstBinder().Bind(parsed);

        var normalized = CoreSqlNormalizer.CreateDefault().Normalize(bound, SqlAgentToolType.Postgres);

        Assert.Equal(SqlAgentToolType.MsSqlServer, normalized.SourceDialect);
        Assert.Equal(SqlAgentToolType.Postgres, normalized.TargetProvider);
        var select = Assert.IsType<SelectStatement>(normalized.Statement);
        var function = Assert.IsType<FunctionCallExpr>(select.Select[0].Expression);
        Assert.Equal("LENGTH", function.Name.Parts[0].Value);
    }

    [Fact]
    public void Normalize_CurrentTimestampTemplate_BecomesCanonicalSemanticFunction()
    {
        var dto = new QueryDefinition
        {
            TableName = "users",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "NOW",
                    Arguments = []
                }
            ]
        };
        var parsed = new ParsedStatement(QueryDefinitionCoreMapper.Map(dto), SqlAgentToolType.Postgres);
        var bound = new SqlAstBinder().Bind(parsed);

        var normalized = CoreSqlNormalizer.CreateDefault().Normalize(bound, SqlAgentToolType.MsSqlServer);

        var select = Assert.IsType<SelectStatement>(normalized.Statement);
        var function = Assert.IsType<FunctionCallExpr>(select.Select[0].Expression);
        Assert.Equal("CORE_CURRENT_TIMESTAMP", function.Name.Parts[0].Value);
        Assert.Empty(function.Arguments);
    }

    [Theory]
    [InlineData("CURRENT_DATE", "CORE_CURRENT_DATE")]
    [InlineData("CURRENT_TIME", "CORE_CURRENT_TIME")]
    public void Normalize_CurrentTemporalKeywords_BecomeCanonicalSemanticFunctions(
        string sourceName,
        string canonicalName)
    {
        var dto = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [new FunctionSelectCondition { FunctionName = sourceName, Arguments = [] }]
        };
        var parsed = new ParsedStatement(QueryDefinitionCoreMapper.Map(dto), SqlAgentToolType.Sqlite);
        var bound = new SqlAstBinder().Bind(parsed);

        var normalized = CoreSqlNormalizer.CreateDefault().Normalize(bound, SqlAgentToolType.MsSqlServer);

        var select = Assert.IsType<SelectStatement>(normalized.Statement);
        var function = Assert.IsType<FunctionCallExpr>(select.Select[0].Expression);
        Assert.Equal(canonicalName, function.Name.Parts[0].Value);
    }
}
