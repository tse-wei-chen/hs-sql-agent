using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Read-only PostgreSQL statement CTEs preceding DELETE must retain the
/// typed Mutation policy, physical read dependencies, and native SQL scope.
/// </summary>
public sealed class SqlCorePostgresWithDeleteTests
{
    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users USING picked p WHERE users.id = p.id")]
    [InlineData("WITH first_rows AS (SELECT 7 AS id), picked AS (SELECT id FROM first_rows) DELETE FROM users USING picked p WHERE users.id = p.id")]
    [InlineData("WITH picked(id) AS (SELECT 7) DELETE FROM users AS u USING picked p WHERE u.id = p.id RETURNING u.id")]
    public void ScriptParsesReadOnlyWithDeleteAsMutation(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql + "; SELECT 2", SqlAgentToolType.Postgres);
        Assert.Empty(script.Diagnostics);
        Assert.Equal(2, script.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Mutation, script.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[0].Status);
        Assert.Equal(SqlScriptStatementKind.Query, script.Statements[1].Kind);
        var analyzed = SqlCoreFacade.AnalyzeScript(sql,
            SqlAgentToolType.Postgres, SqlCatalogState.Unknown());
        Assert.True(Assert.Single(analyzed.Statements).Effects.HasFlag(SqlEffectKind.WriteData));
    }

    [Fact]
    public void CompilesWithDeleteUsingWithoutWhitelistingVirtualCte()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "DELETE FROM users USING picked p WHERE users.id = p.id RETURNING users.id";
        var command = SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-with-delete",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.Equal(SqlStatementKind.Delete, command.Kind);
        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DELETE FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" USING ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" RETURNING ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, p => Equals(p.Value, 7));
        Assert.DoesNotContain("SELECT 7", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithDeleteCannotBypassTargetWhitelist()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "DELETE FROM secrets USING picked p WHERE secrets.id = p.id";
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("with-delete-deny",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users USING picked")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users USING picked WHERE TRUE")]
    public void CteCannotBypassDeleteWherePolicy(string sql)
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, new SqlPlanValidationContext("with-delete-policy")));
    }

    [Fact]
    public void StrictSchemaProvesVirtualCteButNotAsPhysicalWriteTarget()
    {
        var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.Postgres, "delete-cte-schema-v1",
        [
            SqlSchemaTable.WithTypes("users",
            [
                new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                new SqlSchemaColumn("name", SqlSchemaValueKind.Text)
            ])
        ]);
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "DELETE FROM users USING picked p WHERE users.id = p.id";
        var command = SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("strict-delete-cte-schema").WithSchemaSnapshot(snapshot));
        Assert.Equal(SqlStatementKind.Delete, command.Kind);
    }

    [Fact]
    public void StaticEffectsDoNotReportVirtualCteAsPhysicalTable()
    {
        const string sql = "WITH picked AS (SELECT id FROM inputs WHERE id = 2) " +
                           "DELETE FROM users USING picked p WHERE users.id = p.id";
        var effect = Assert.Single(SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres).Statements)
            .DetailedEffects;
        Assert.Contains(effect, x => x.Operation == SqlStatementEffectOperation.ReadTable &&
                                     x.ObjectName == "inputs");
        Assert.Contains(effect, x => x.Operation == SqlStatementEffectOperation.Delete &&
                                     x.ObjectName == "users");
        Assert.DoesNotContain(effect, x => x.ObjectName == "picked");
    }

    [Theory]
    [InlineData("WITH x AS (UPDATE users SET name = 'x' RETURNING id) DELETE FROM users WHERE id = 1")]
    [InlineData("WITH RECURSIVE x AS (SELECT 1 AS id) DELETE FROM users WHERE id = 1")]
    [InlineData("WITH x AS (SELECT 1 AS id) MERGE INTO users u USING (VALUES (1)) s(id) ON u.id=s.id WHEN MATCHED THEN DELETE")]
    public void UnsupportedWithDeleteFormsRemainFailClosed(string sql)
    {
        var syntax = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(syntax.Statements).Status);
        Assert.NotEmpty(syntax.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherSourceDialectsCannotBorrowWithDeleteSyntax(SqlAgentToolType source)
    {
        const string sql = "WITH x AS (SELECT 7 AS id) " +
                           "DELETE FROM users USING x WHERE users.id = x.id";
        var script = SqlCoreFacade.ParseScript(sql, source);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(script.Statements).Status);
    }

    [Fact]
    public void ContextualDeleteCteNameRemainsQuery()
    {
        const string sql = "WITH delete AS (SELECT 1 AS id) SELECT id FROM delete";
        var result = Assert.Single(SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres).Statements);
        Assert.Equal(SqlScriptStatementKind.Query, result.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, result.Status);
    }
}
