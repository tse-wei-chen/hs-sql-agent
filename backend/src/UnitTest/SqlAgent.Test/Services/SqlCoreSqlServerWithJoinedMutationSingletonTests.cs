using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// A SQL Server CTE-prefixed UPDATE/DELETE may join a CTE only when its
/// closed, nonrecursive relational graph proves at most one source row.
/// This is a mutation-cardinality proof, not a CTE name / policy exemption.
/// </summary>
public sealed class SqlCoreSqlServerWithJoinedMutationSingletonTests
{
    private static SqlProviderCapabilityProfile Profile() =>
        new(SqlAgentToolType.MsSqlServer, new Version(16, 0));

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET label = 'changed' FROM picked WHERE users.id = picked.id", SqlStatementKind.Update)]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users FROM picked WHERE users.id = picked.id", SqlStatementKind.Delete)]
    [InlineData("WITH seed AS (SELECT 7 AS id), picked AS (SELECT id FROM seed) UPDATE users SET label = 'changed' FROM picked WHERE users.id = picked.id", SqlStatementKind.Update)]
    [InlineData("WITH seed AS (SELECT 7 AS id), picked(id) AS (SELECT id FROM seed) DELETE FROM users FROM picked WHERE users.id = picked.id", SqlStatementKind.Delete)]
    [InlineData("WITH seed AS (SELECT 7 AS id), filtered AS (SELECT id FROM seed WHERE id > 0), picked AS (SELECT id FROM filtered) UPDATE users SET label = 'changed' FROM picked WHERE users.id = picked.id", SqlStatementKind.Update)]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET label = 'changed' FROM picked p WHERE users.id = p.id", SqlStatementKind.Update)]
    public void SingletonCteJoinedMutationParsesAndCompiles(string sql, SqlStatementKind kind)
    {
        var parsed = SqlCoreFacade.ParseScript(sql + "; SELECT 2;",
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer,
                Profile(), TestContext.Current.CancellationToken));
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(2, parsed.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Mutation, parsed.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, parsed.Statements[0].Status);
        Assert.Equal(SqlScriptStatementKind.Query, parsed.Statements[1].Kind);
        var compiled = Compile(sql);
        Assert.Equal(kind, compiled.Kind);
        Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(kind == SqlStatementKind.Update ? "UPDATE " : "DELETE FROM ",
            compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" FROM ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, 7));
        Assert.DoesNotContain("SELECT 7", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id UNION ALL SELECT 7 AS id) UPDATE users SET label = 'changed' FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT 7 AS id UNION ALL SELECT 7 AS id) DELETE FROM users FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id FROM source_rows) UPDATE users SET label = 'changed' FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id FROM source_rows) DELETE FROM users FROM picked WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT 7 AS id), expanded AS (SELECT picked.id FROM picked, source_rows) UPDATE users SET label = 'changed' FROM expanded WHERE users.id = expanded.id")]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET label = 'changed' FROM picked, extra_source WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users FROM picked, extra_source WHERE users.id = picked.id")]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET label = 'changed' FROM extra_source WHERE users.id = extra_source.id")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users FROM extra_source WHERE users.id = extra_source.id")]
    public void UnprovenCTEMultiplicityNeverCompiles(string sql)
    {
        // This is syntactically native SQL Server, but not an at-most-one-row
        // guarantee. Reject at capability validation, not raw-string rewrite.
        var result = SqlCoreFacade.TryCompileDml(sql, SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-cte-joined-negative",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "users", "source_rows", "extra_source" }));
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET label = 'changed' FROM picked")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users FROM picked")]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET label = 'changed' FROM picked WHERE 1 = 1")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM users FROM picked WHERE 1 = 1")]
    public void SingletonDoesNotBypassBoundedWherePolicy(string sql)
    {
        var result = SqlCoreFacade.TryCompileDml(sql, SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer, new SqlPlanValidationContext("sqlserver-cte-no-unbounded"));
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE picked SET id = 8 FROM picked WHERE id = 7")]
    [InlineData("WITH picked AS (SELECT 7 AS id) DELETE FROM picked FROM picked WHERE id = 7")]
    public void CteAliasCannotBeThePhysicalWriteTarget(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer, Profile());
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parsed.Statements).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SQLServer2000CannotProvideJoinedCteSourceProof(bool update)
    {
        var sql = update
            ? "WITH picked AS (SELECT 7 AS id) UPDATE users SET label = 'changed' FROM picked WHERE users.id = picked.id"
            : "WITH picked AS (SELECT 7 AS id) DELETE FROM users FROM picked WHERE users.id = picked.id";
        var old = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(8, 0));
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer, old,
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parsed.Statements).Status);
        Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-cte-joined-old-target",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new DmlCompilationPolicy(), Profile(), old,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JoinedCteMutationRemainsNativeOnly(bool update)
    {
        var sql = update
            ? "WITH picked AS (SELECT 7 AS id) UPDATE users SET label = 'changed' FROM picked WHERE users.id = picked.id"
            : "WITH picked AS (SELECT 7 AS id) DELETE FROM users FROM picked WHERE users.id = picked.id";
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("sqlserver-cte-joined-cross"));
        Assert.False(result.Success);
    }

    [Fact]
    public void StatementEffectsRemainPhysicalAndExcludeVirtualCte()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "UPDATE users SET label = 'changed' FROM picked WHERE users.id = picked.id";
        var statement = Assert.Single(SqlCoreFacade.AnalyzeScript(sql,
            SqlAgentToolType.MsSqlServer, SqlCatalogState.Unknown(), Profile()).Statements);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.WriteData));
        Assert.Contains(statement.DetailedEffects, e =>
            e.ObjectName == "users" && e.Operation == SqlStatementEffectOperation.Update);
        Assert.DoesNotContain(statement.DetailedEffects, e =>
            e.ObjectName == "picked" && e.Operation == SqlStatementEffectOperation.ReadTable);
    }

    private static CompiledSqlCommand Compile(string sql)
    {
        var profile = Profile();
        return SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-cte-joined-singleton",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));
    }
}
