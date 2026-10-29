using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL statement-level read-only CTEs can feed INSERT SELECT.
/// A CTE is not a mutation target, an executable permission, or a shortcut
/// around the existing strict Mutation parser/policy/parameterization.
/// </summary>
public sealed class SqlCorePostgresWithInsertSelectTests
{
    [Theory]
    [InlineData("WITH source_rows AS (SELECT 42 AS id) INSERT INTO users(id) SELECT id FROM source_rows")]
    [InlineData("WITH first_rows AS (SELECT 42 AS id), source_rows AS (SELECT id FROM first_rows) INSERT INTO users(id) SELECT id FROM source_rows")]
    [InlineData("WITH source_rows(id) AS (SELECT 42) INSERT INTO users(id) SELECT id FROM source_rows")]
    public void ParsesReadOnlyCtePrefixedInsertSelectAsMutation(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql + "; SELECT 2", SqlAgentToolType.Postgres);
        Assert.Equal(2, script.Statements.Count);
        Assert.Empty(script.Diagnostics);
        Assert.Equal(SqlScriptStatementKind.Mutation, script.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[0].Status);
        Assert.Equal(SqlScriptStatementKind.Query, script.Statements[1].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[1].Status);
        Assert.Equal(sql, script.Statements[0].Text);
        Assert.Equal(1, script.BatchCount);

        var analysis = SqlCoreFacade.AnalyzeScript(sql,
            SqlAgentToolType.Postgres, SqlCatalogState.Unknown());
        var statement = Assert.Single(analysis.Statements);
        Assert.Equal(SqlScriptStatementKind.Mutation, statement.Syntax.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, statement.Syntax.Status);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.WriteData));
    }

    [Fact]
    public void MutationCompilerRendersWithBeforeInsertAndParameterizesCteLiteral()
    {
        const string sql =
            "WITH source_rows AS (SELECT 42 AS id) INSERT INTO users(id) SELECT id FROM source_rows";
        var validation = new SqlPlanValidationContext(
            "cte-insert-positive", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" });
        var result = SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres, validation);

        Assert.StartsWith("WITH ", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT INTO", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Sql.IndexOf("WITH ", StringComparison.OrdinalIgnoreCase) <
                    result.Sql.IndexOf("INSERT INTO", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("SELECT 42", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Parameters, parameter => Equals(parameter.Value, 42));
    }

    [Fact]
    public void CteCannotBypassMutationTargetWhitelist()
    {
        const string sql =
            "WITH source_rows AS (SELECT 42 AS id) INSERT INTO secrets(id) SELECT id FROM source_rows";
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("cte-insert-deny",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH x AS (SELECT 1) INSERT INTO users(id) VALUES (1)")]
    [InlineData("WITH x AS (SELECT 1) INSERT INTO users(id) DEFAULT VALUES")]
    [InlineData("WITH x AS (SELECT 1) INSERT INTO users(id) SELECT 1 RETURNING id")]
    [InlineData("WITH x AS (SELECT 1) INSERT INTO users(id) SELECT 1 ON CONFLICT (id) DO NOTHING")]
    [InlineData("WITH x AS (SELECT 1) UPDATE users SET id = 1 WHERE id = 2")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM users WHERE id = 1")]
    [InlineData("WITH x AS (DELETE FROM users RETURNING id) INSERT INTO archive(id) SELECT id FROM x")]
    public void UnmodeledWithMutationFormsRemainFailClosed(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(script.Statements).Status);
        Assert.NotEmpty(script.Diagnostics);
        Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("cte-insert-negative")));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherSourceDialectsDoNotInheritPostgresWithInsertGrammar(SqlAgentToolType provider)
    {
        const string sql =
            "WITH x AS (SELECT 1 AS id) INSERT INTO users(id) SELECT id FROM x";
        var script = SqlCoreFacade.ParseScript(sql, provider);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(script.Statements).Status);
        Assert.NotEmpty(script.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ExistingWithSelectIsStillAQuery(SqlAgentToolType provider)
    {
        const string sql = "WITH x AS (SELECT 1 AS id) SELECT id FROM x";
        var statement = Assert.Single(SqlCoreFacade.ParseScript(sql, provider).Statements);
        Assert.Equal(SqlScriptStatementKind.Query, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status);
    }
}
