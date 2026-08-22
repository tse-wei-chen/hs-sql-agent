using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Core.Normalization;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
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
    public void Normalize_TemplateTranslation_FailsClosedUntilCoreTranslatorExists()
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

        var ex = Assert.Throws<SqlCompilationException>(
            () => CoreSqlNormalizer.CreateDefault().Normalize(bound, SqlAgentToolType.MsSqlServer));

        Assert.Contains("Template", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
