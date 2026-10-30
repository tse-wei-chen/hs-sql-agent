using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Native MySQL 8.0 read-only CTE DELETE must remain a typed single-table
/// mutation with enforced WHERE policy, source and target table permissions.
/// </summary>
public sealed class SqlCoreMySqlWithDeleteTests
{
    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE id IN (SELECT id FROM picked)")]
    [InlineData("WITH first_rows AS (SELECT 7 AS id), picked AS (SELECT id FROM first_rows) DELETE FROM users WHERE id IN (SELECT id FROM picked)")]
    [InlineData("WITH picked(id) AS (SELECT 7) DELETE FROM users WHERE id IN (SELECT id FROM picked)")]
    public void ScriptClassifiesMySqlReadOnlyWithDeleteAsTypedMutation(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql + "; SELECT 2",
            new SqlScriptParseOptions(SqlAgentToolType.MySQL, Profile(),
                TestContext.Current.CancellationToken));
        Assert.Empty(script.Diagnostics);
        Assert.Equal(2, script.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Mutation, script.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[0].Status);
        Assert.Equal(sql, script.Statements[0].Text);
        Assert.Equal(SqlScriptStatementKind.Query, script.Statements[1].Kind);
        var analysis = SqlCoreFacade.AnalyzeScript(sql,
            new SqlScriptAnalysisOptions(SqlAgentToolType.MySQL,
                SqlCatalogState.Unknown(), Profile(),
                TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(analysis.Statements).Effects.HasFlag(SqlEffectKind.WriteData));
    }

    [Fact]
    public void CompilesMySqlWithDeleteAndParameterizedWhere()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        var compiled = Compile(sql, new SqlPlanValidationContext("mysql-cte-delete",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.Equal(SqlStatementKind.Delete, compiled.Kind);
        Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DELETE FROM ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" WHERE ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, 7));
        Assert.DoesNotContain("SELECT 7", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CtePhysicalReadAndDeleteTargetMustBothBeAllowlisted()
    {
        const string sql = "WITH picked AS (SELECT id FROM secret_source) " +
                           "DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        var missingRead = TryCompile(sql, ["users"]);
        var missingWrite = TryCompile(sql, ["secret_source"]);
        Assert.False(missingRead.Success);
        Assert.False(missingWrite.Success);
        Assert.NotEmpty(missingRead.TypedDiagnostics);
        Assert.NotEmpty(missingWrite.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE TRUE")]
    public void CteCannotBypassBoundedDeletePolicy(string sql)
    {
        var result = TryCompile(sql, ["users"]);
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Fact]
    public void StrictTypedSchemaBindsCteOutputAndPhysicalDeleteTarget()
    {
        var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MySQL, "mysql-cte-delete-schema",
        [
            SqlSchemaTable.WithTypes("users",
            [
                new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                new SqlSchemaColumn("name", SqlSchemaValueKind.Text)
            ])
        ]);
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        Assert.Equal(SqlStatementKind.Delete, Compile(sql,
            new SqlPlanValidationContext("mysql-cte-delete-strict")
                .WithSchemaSnapshot(snapshot)).Kind);
    }

    [Fact]
    public void StaticEffectsRecordOnlyPhysicalReadsAndWrites()
    {
        const string sql = "WITH picked AS (SELECT id FROM input_rows WHERE id = 7) " +
                           "DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        var statement = Assert.Single(SqlCoreFacade.AnalyzeScript(sql,
            new SqlScriptAnalysisOptions(SqlAgentToolType.MySQL,
                SqlCatalogState.Unknown(), Profile(),
                TestContext.Current.CancellationToken)).Statements);
        Assert.Contains(statement.DetailedEffects, x => x.Operation == SqlStatementEffectOperation.ReadTable
                && x.ObjectName == "input_rows");
        Assert.Contains(statement.DetailedEffects, x => x.Operation == SqlStatementEffectOperation.Delete
                && x.ObjectName == "users");
        Assert.DoesNotContain(statement.DetailedEffects, x => x.ObjectName == "picked");
    }

    [Theory]
    [InlineData("WITH RECURSIVE picked AS (SELECT 7 AS id) DELETE FROM users WHERE id = 7")]
    [InlineData("WITH picked AS (DELETE FROM users) DELETE FROM users WHERE id = 7")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users USING picked WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE id = 7 RETURNING id")]
    public void UnprovenDeleteCteGrammarRemainsFailClosed(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MySQL, Profile(),
                TestContext.Current.CancellationToken));
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(parsed.Statements).Status);
        Assert.NotEmpty(parsed.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnprovenDialectsDoNotBorrowMySqlWithDelete(SqlAgentToolType source)
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        var syntax = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(source, TestContext.Current.CancellationToken));
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(syntax.Statements).Status);
    }

    [Fact]
    public void MySql57SourceCannotClaimCTEDelete()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) DELETE FROM users WHERE id = 7";
        var oldProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(5, 7));
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MySQL, oldProfile,
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parsed.Statements).Status);
    }

    [Fact]
    public void MySql57TargetAndCrossProviderCompilationRemainRejected()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "DELETE FROM users WHERE id IN (SELECT id FROM picked)";
        var translated = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MySQL, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("mysql-with-delete-cross"));
        Assert.False(translated.Success);
        Assert.NotEmpty(translated.TypedDiagnostics);

        var oldTarget = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(5, 7));
        Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mysql-with-delete-old-target"),
            new DmlCompilationPolicy(), Profile(), oldTarget,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)));
    }

    private static SqlProviderCapabilityProfile Profile() =>
        new(SqlAgentToolType.MySQL, new Version(8, 0, 36));

    private static CompiledSqlCommand Compile(string sql, SqlPlanValidationContext context)
    {
        var profile = Profile();
        return SqlCoreFacade.CompileDml(sql, SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            context, new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));
    }

    private static SqlCoreTryResult<CompiledSqlCommand> TryCompile(string sql, IEnumerable<string> tables) =>
        SqlCoreFacade.TryCompileDml(sql, SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mysql-cte-delete-policy",
                new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase)));
}
