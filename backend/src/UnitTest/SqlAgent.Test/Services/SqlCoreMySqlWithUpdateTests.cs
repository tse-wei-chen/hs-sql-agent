using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// MySQL 8.0 single-table WITH UPDATE must stay in the closed typed F#
/// Mutation pipeline and must not acquire joined-update authorization.
/// </summary>
public sealed class SqlCoreMySqlWithUpdateTests
{
    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)")]
    [InlineData("WITH first_rows AS (SELECT 7 AS id), picked AS (SELECT id FROM first_rows) UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)")]
    [InlineData("WITH picked(id) AS (SELECT 7) UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)")]
    public void ScriptParsesMySqlWithUpdateAsTypedMutation(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql + "; SELECT 2",
            new SqlScriptParseOptions(SqlAgentToolType.MySQL, Profile(),
                TestContext.Current.CancellationToken));
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(2, parsed.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Mutation, parsed.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, parsed.Statements[0].Status);
        Assert.Equal(sql, parsed.Statements[0].Text);
        Assert.Equal(SqlScriptStatementKind.Query, parsed.Statements[1].Kind);

        var analyzed = SqlCoreFacade.AnalyzeScript(sql,
            new SqlScriptAnalysisOptions(SqlAgentToolType.MySQL,
                SqlCatalogState.Unknown(), Profile(),
                TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(analyzed.Statements).Effects.HasFlag(SqlEffectKind.WriteData));
    }

    [Fact]
    public void CompilesMySqlWithUpdateAsParameterizedSingleTargetMutation()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)";
        var command = Compile(sql, new SqlPlanValidationContext("mysql-cte-update",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UPDATE ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" SET ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" WHERE ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, p => Equals(p.Value, 7));
        Assert.Contains(command.Parameters, p => Equals(p.Value, "changed"));
        Assert.DoesNotContain("SELECT 7", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'changed'", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CteReadAndWriteTargetRequireIndependentPhysicalAllowlistProof()
    {
        const string sql = "WITH picked AS (SELECT id FROM secret_source) " +
                           "UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)";
        var missingRead = TryCompile(sql, ["users"]);
        var missingWrite = TryCompile(sql, ["secret_source"]);
        Assert.False(missingRead.Success);
        Assert.False(missingWrite.Success);
        Assert.NotEmpty(missingRead.TypedDiagnostics);
        Assert.NotEmpty(missingWrite.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed'")]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' WHERE TRUE")]
    public void CteCannotBypassSelectiveWherePolicy(string sql)
    {
        var result = TryCompile(sql, ["users"]);
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Fact]
    public void StrictSchemaChecksCteProjectionAndAssignment()
    {
        var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MySQL, "mysql-update-cte-schema",
        [
            SqlSchemaTable.WithTypes("users",
            [
                new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                new SqlSchemaColumn("name", SqlSchemaValueKind.Text)
            ])
        ]);
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)";
        Assert.Equal(SqlStatementKind.Update,
            Compile(sql, new SqlPlanValidationContext("mysql-with-update-schema")
                .WithSchemaSnapshot(snapshot)).Kind);
    }

    [Fact]
    public void StaticEffectsReferOnlyToPhysicalCteSourceAndMutationTarget()
    {
        const string sql = "WITH picked AS (SELECT id FROM source_rows WHERE id = 7) " +
                           "UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)";
        var analysis = SqlCoreFacade.AnalyzeScript(sql,
            new SqlScriptAnalysisOptions(SqlAgentToolType.MySQL,
                SqlCatalogState.Unknown(), Profile(),
                TestContext.Current.CancellationToken));
        var effects = Assert.Single(analysis.Statements).DetailedEffects;
        Assert.Contains(effects, e => e.Operation == SqlStatementEffectOperation.ReadTable &&
                                      e.ObjectName == "source_rows");
        Assert.Contains(effects, e => e.Operation == SqlStatementEffectOperation.Update &&
                                      e.ObjectName == "users");
        Assert.DoesNotContain(effects, e => e.ObjectName == "picked");
    }

    [Theory]
    [InlineData("WITH RECURSIVE picked AS (SELECT 7 AS id) UPDATE users SET name = 'x' WHERE id = 7")]
    [InlineData("WITH picked AS (DELETE FROM users) UPDATE users SET name = 'x' WHERE id = 7")]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'x' WHERE id = 7 RETURNING id")]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'x' FROM picked WHERE users.id = picked.id")]
    public void UnsupportedSourceGrammarRemainsFailClosed(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MySQL, Profile(),
                TestContext.Current.CancellationToken));
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(parsed.Statements).Status);
        Assert.NotEmpty(parsed.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherUnprovenDialectsDoNotInheritMySqlCteUpdate(SqlAgentToolType source)
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'x' " +
                           "WHERE id IN (SELECT id FROM picked)";
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(source, TestContext.Current.CancellationToken));
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(parsed.Statements).Status);
    }

    [Fact]
    public void ContextualUpdateCteNameStillParsesAsQuery()
    {
        const string sql = "WITH update AS (SELECT 7 AS id) SELECT id FROM update";
        var statement = Assert.Single(SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MySQL,
                TestContext.Current.CancellationToken)).Statements);
        Assert.Equal(SqlScriptStatementKind.Query, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status);
    }

    [Fact]
    public void MySql57CannotClaimCteUpdateSourceSyntax()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'x' WHERE id = 7";
        var oldProfile = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(5, 7));
        var parsed = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MySQL, oldProfile,
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parsed.Statements).Status);
    }

    [Fact]
    public void MySql57TargetAndCrossProviderCompilationMustBeRejected()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "UPDATE users SET name = 'x' WHERE id IN (SELECT id FROM picked)";
        var crossProvider = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MySQL, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("mysql-update-cross-provider"));
        Assert.False(crossProvider.Success);
        Assert.NotEmpty(crossProvider.TypedDiagnostics);

        var oldTarget = new SqlProviderCapabilityProfile(SqlAgentToolType.MySQL, new Version(5, 7));
        Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.MySQL, SqlAgentToolType.MySQL,
            new SqlPlanValidationContext("mysql-update-old-target"),
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
            new SqlPlanValidationContext("mysql-update-allowlist",
                new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase)));
}
