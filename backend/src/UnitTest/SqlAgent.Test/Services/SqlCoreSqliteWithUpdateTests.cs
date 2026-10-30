using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQLite ordinary statement-level WITH UPDATE must preserve the closed F#
/// Update.Ctes mutation pipeline, not reinterpret the WITH as a SELECT.
/// </summary>
public sealed class SqlCoreSqliteWithUpdateTests
{
    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)")]
    [InlineData("WITH first_rows AS (SELECT 7 AS id), picked AS (SELECT id FROM first_rows) UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)")]
    [InlineData("WITH picked(id) AS (SELECT 7) UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked) RETURNING id")]
    public void ScriptParsesOrdinaryWithUpdateAsMutation(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql + "; SELECT 2", SqlAgentToolType.Sqlite, SqliteProfile());
        Assert.Equal(2, parsed.Statements.Count);
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(SqlScriptStatementKind.Mutation, parsed.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, parsed.Statements[0].Status);
        Assert.Equal(sql, parsed.Statements[0].Text);
        Assert.Equal(SqlScriptStatementKind.Query, parsed.Statements[1].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, parsed.Statements[1].Status);

        var analyzed = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Sqlite,
            SqlCatalogState.Unknown(), SqliteProfile());
        Assert.True(Assert.Single(analyzed.Statements).Effects.HasFlag(SqlEffectKind.WriteData));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" RETURNING id")]
    public void CompilesWithUpdateAndPreservesParameterBindingAndReturning(string suffix)
    {
        var sql = "WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' " +
                  "WHERE id IN (SELECT id FROM picked)" + suffix;
        var command = Compile(sql, new SqlPlanValidationContext("sqlite-cte-update",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UPDATE ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" SET ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" WHERE ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT 7", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, x => Equals(x.Value, 7));
        Assert.Contains(command.Parameters, x => Equals(x.Value, "changed"));
        Assert.Equal(suffix.Contains("RETURNING", StringComparison.Ordinal),
            command.Sql.Contains("RETURNING", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DoesNotGrantAccessToPhysicalCteSourceOrTarget()
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
    public void OrdinaryCteDoesNotBypassBoundedUpdatePolicy(string sql)
    {
        var result = TryCompile(sql, ["users"]);
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Fact]
    public void CtePrefixedSqliteUpdateFromIsNotYetAuthorized()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "UPDATE users SET name = 'changed' FROM picked p " +
                           "WHERE users.id = p.id";
        var rejected = TryCompile(sql, ["users"]);
        Assert.False(rejected.Success);
        Assert.NotEmpty(rejected.TypedDiagnostics);
    }

    [Fact]
    public void TypedSchemaSnapshotProvesCteProjectionAndActualAssignment()
    {
        var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.Sqlite, "sqlite-cte-update-schema",
        [
            SqlSchemaTable.WithTypes("users",
            [
                new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                new SqlSchemaColumn("name", SqlSchemaValueKind.Text)
            ])
        ]);
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
                           "UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)";
        var command = Compile(sql, new SqlPlanValidationContext("sqlite-update-strict")
            .WithSchemaSnapshot(snapshot));
        Assert.Equal(SqlStatementKind.Update, command.Kind);
    }

    [Fact]
    public void StaticEffectsContainOnlyPhysicalReadAndUpdateTargets()
    {
        const string sql = "WITH picked AS (SELECT id FROM source_rows WHERE id = 7) " +
                           "UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)";
        var effect = Assert.Single(SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Sqlite,
            SqlCatalogState.Unknown(), SqliteProfile()).Statements).DetailedEffects;
        Assert.Contains(effect, e => e.Operation == SqlStatementEffectOperation.ReadTable &&
                                     e.ObjectName == "source_rows");
        Assert.Contains(effect, e => e.Operation == SqlStatementEffectOperation.Update &&
                                     e.ObjectName == "users");
        Assert.DoesNotContain(effect, e => e.ObjectName == "picked");
    }

    [Theory]
    [InlineData("WITH RECURSIVE picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' WHERE id = 7")]
    [InlineData("WITH picked AS (DELETE FROM users RETURNING id) UPDATE users SET name = 'changed' WHERE id = 7")]
    [InlineData("WITH picked AS (SELECT 7 AS id) UPDATE users AS u SET name = 'changed' WHERE u.id = 7")]
    public void UnsupportedMutationCteFormsStayFailClosed(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Sqlite, SqliteProfile());
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(parsed.Statements).Status);
        Assert.NotEmpty(parsed.Diagnostics);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherDialectsDoNotInheritSqliteUpdateGrammar(SqlAgentToolType source)
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' " +
                           "WHERE id IN (SELECT id FROM picked)";
        var parsed = SqlCoreFacade.ParseScript(sql, source);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(parsed.Statements).Status);
    }

    [Fact]
    public void ContextualUpdateCteNameRemainsQuery()
    {
        const string sql = "WITH update AS (SELECT 7 AS id) SELECT id FROM update";
        var statement = Assert.Single(SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Sqlite).Statements);
        Assert.Equal(SqlScriptStatementKind.Query, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status);
    }

    [Fact]
    public void SQLitePreCteSourceProfileRemainsRejected()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' WHERE id = 7";
        var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, new Version(3, 8, 2));
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Sqlite, profile);
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parsed.Statements).Status);
    }

    [Fact]
    public void SQLitePreReturningSourceProfileRemainsRejected()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' WHERE id = 7 RETURNING id";
        var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, new Version(3, 34));
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Sqlite, profile);
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parsed.Statements).Status);
    }

    [Fact]
    public void NativeOnlyCteCompilationAndTargetVersionProofRemainEnforced()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) UPDATE users SET name = 'changed' " +
                           "WHERE id IN (SELECT id FROM picked)";
        var across = SqlCoreFacade.TryCompileDml(sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("sqlite-update-cross"));
        Assert.False(across.Success);
        Assert.NotEmpty(across.TypedDiagnostics);

        var oldTarget = new SqlProviderCapabilityProfile(SqlAgentToolType.Sqlite, new Version(3, 8, 2));
        Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("sqlite-update-old-target"),
                new DmlCompilationPolicy(), SqliteProfile(), oldTarget,
                new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)));
    }

    private static SqlProviderCapabilityProfile SqliteProfile() =>
        new(SqlAgentToolType.Sqlite, new Version(3, 35));

    private static CompiledSqlCommand Compile(string sql, SqlPlanValidationContext context)
    {
        var profile = SqliteProfile();
        return SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            context, new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));
    }

    private static SqlCoreTryResult<CompiledSqlCommand> TryCompile(string sql, IEnumerable<string> tables) =>
        SqlCoreFacade.TryCompileDml(sql, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite,
            new SqlPlanValidationContext("sqlite-update-deny",
                new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase)));
}
