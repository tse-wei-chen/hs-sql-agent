using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Regression contracts for the domain-specific policy/typestate split.
/// All source providers must retain their existing native renderer and safety behavior.
/// </summary>
public sealed class SqlCoreSeparatedPipelineTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void QueryPipeline_StillProducesANativeQueryCommand(SqlAgentToolType provider)
    {
        var command = SqlCoreFacade.CompileQuery(
            "SELECT id FROM users WHERE id = 1",
            provider,
            provider,
            new SqlPlanValidationContext("separated-pipeline-v1"),
            new SqlExecutionPlanPolicy(5));

        Assert.Equal(SqlStatementKind.Query, command.Kind);
        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(command.PlanFingerprint);
        Assert.NotNull(command.CompileEvidence);
        Assert.Contains(command.Parameters, p => Equals(p.Value, 1));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void MutationPipeline_StillProducesAParameterizedCommand(SqlAgentToolType provider)
    {
        var command = SqlCoreFacade.CompileDml(
            "UPDATE users SET name = 'Ada' WHERE id = 1",
            provider,
            provider,
            new SqlPlanValidationContext("separated-pipeline-v1"));

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'Ada'", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, p => Equals(p.Value, "Ada"));
        Assert.Contains(command.Parameters, p => Equals(p.Value, 1));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void MutationPipeline_StillDeniesUnboundedUpdate(SqlAgentToolType provider)
    {
        var denied = Assert.Throws<UnauthorizedAccessException>(() =>
            SqlCoreFacade.CompileDml(
                "UPDATE users SET name = 'Ada'",
                provider,
                provider,
                new SqlPlanValidationContext("separated-pipeline-v1")));

        Assert.Contains("WHERE", denied.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MutationPipeline_ExplicitFullTablePermissionRemainsEffective()
    {
        var command = CoreDmlCompiler.CreateDefault().Compile(
            HsSqlAgent.SqlCore.SqlParsing.CoreSqlTextParser.ParseDml(
                "UPDATE users SET name = 'Ada'",
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("separated-pipeline-full-table-v1"),
            new DmlCompilationPolicy(false, true, true, false));

        Assert.Equal(SqlStatementKind.Update, command.Kind);
    }

    [Fact]
    public void QueryPipeline_RejectsMutationAtPublicApiBoundary()
    {
        Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileQuery(
                "UPDATE users SET name = 'Ada' WHERE id = 1",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("separated-pipeline-api-v1"),
                new SqlExecutionPlanPolicy(5)));
    }
}
