using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQL Server nonrecursive statement-level WITH INSERT SELECT is a typed DML
/// statement. Source and target version, lexical scope and physical write
/// target must be proved independently of syntactic CTE names.
/// </summary>
public sealed class SqlCoreSqlServerWithInsertSelectTests
{
    private const string Insert =
        "WITH picked AS (SELECT 7 AS id) " +
        "INSERT INTO users(id) SELECT id FROM picked";

    private static SqlProviderCapabilityProfile Profile() =>
        new(SqlAgentToolType.MsSqlServer, new Version(16, 0));

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) INSERT INTO users(id) SELECT id FROM picked")]
    [InlineData("WITH seed AS (SELECT 7 AS id), picked AS (SELECT id FROM seed) INSERT INTO users(id) SELECT id FROM picked")]
    [InlineData("WITH seed AS (SELECT 7 AS id), picked(id) AS (SELECT id FROM seed) INSERT INTO users(id) SELECT id FROM picked")]
    [InlineData("WITH picked AS (SELECT 7 AS id) INSERT INTO users(id) OUTPUT INSERTED.id SELECT id FROM picked")]
    public void ParseAnalyzeAndCompileCteInsertSelect(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql + "; SELECT 2;",
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer, Profile(),
                TestContext.Current.CancellationToken));
        Assert.Empty(script.Diagnostics);
        Assert.Equal(2, script.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Mutation, script.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[0].Status);
        Assert.Equal(sql, script.Statements[0].Text);
        Assert.Equal(SqlScriptStatementKind.Query, script.Statements[1].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, script.Statements[1].Status);

        var analysis = SqlCoreFacade.AnalyzeScript(sql,
            new SqlScriptAnalysisOptions(SqlAgentToolType.MsSqlServer,
                SqlCatalogState.Unknown(), Profile(),
                TestContext.Current.CancellationToken));
        var statement = Assert.Single(analysis.Statements);
        Assert.True(statement.Effects.HasFlag(SqlEffectKind.WriteData));

        var validation = new SqlPlanValidationContext("sqlserver-cte-insert",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" });
        // A SQL Server OUTPUT without INTO requires a no-enabled-triggers
        // assurance for the exact physical INSERT target and operation.
        if (sql.Contains("OUTPUT ", StringComparison.OrdinalIgnoreCase))
            validation = validation.WithDmlResultRowAssurance(
                DmlResultRowAssurance.NoEnabledTriggers("users", DmlOperation.Insert));
        var command = Compile(sql, validation);
        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(command.Sql.IndexOf("WITH ", StringComparison.OrdinalIgnoreCase)
            < command.Sql.IndexOf("INSERT INTO", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(command.Parameters, p => Equals(p.Value, 7));
        Assert.DoesNotContain("SELECT 7", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sql.Contains("OUTPUT INSERTED.id", StringComparison.Ordinal),
            command.Sql.Contains("OUTPUT ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InsertOutputWithoutNoEnabledTriggerAssuranceFailsClosed()
    {
        const string sql = "WITH picked AS (SELECT 7 AS id) " +
            "INSERT INTO users(id) OUTPUT INSERTED.id SELECT id FROM picked";
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-cte-insert-output-unproven",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.False(result.Success);
        Assert.Contains(result.TypedDiagnostics, diagnostic =>
            diagnostic.Message.Contains("enabled trigger", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CteReadsAndInsertTargetNeedIndependentAllowlistEntries()
    {
        const string sql = "WITH picked AS (SELECT id FROM private_source WHERE id >= 2) " +
            "INSERT INTO users(id) SELECT id FROM picked";
        foreach (var allowed in new[] { new[] { "users" }, new[] { "private_source" } })
        {
            var result = SqlCoreFacade.TryCompileDml(sql,
                SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("sqlserver-cte-insert-allowlist",
                    new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase)));
            Assert.False(result.Success);
            Assert.NotEmpty(result.TypedDiagnostics);
        }

        var effects = Assert.Single(SqlCoreFacade.AnalyzeScript(sql,
            SqlAgentToolType.MsSqlServer, SqlCatalogState.Unknown(), Profile()).Statements).DetailedEffects;
        Assert.Contains(effects, e => e.ObjectName == "private_source" &&
            e.Operation == SqlStatementEffectOperation.ReadTable);
        Assert.Contains(effects, e => e.ObjectName == "users" &&
            e.Operation == SqlStatementEffectOperation.Insert);
        Assert.DoesNotContain(effects, e => e.ObjectName == "picked");
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT id FROM private_source) INSERT INTO picked(id) SELECT id FROM picked")]
    [InlineData("WITH picked AS (SELECT id FROM private_source) INSERT INTO [picked](id) SELECT id FROM picked")]
    [InlineData("WITH users AS (SELECT id FROM private_source) INSERT INTO users(id) SELECT id FROM users")]
    [InlineData("WITH Users AS (SELECT id FROM private_source) INSERT INTO [users](id) SELECT id FROM Users")]
    public void CteNameMustNotMasqueradeAsPhysicalInsertTarget(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer, Profile(),
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(script.Statements).Status);
        Assert.NotEmpty(script.Diagnostics);
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-cte-insert-write-target",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "users", "picked", "private_source" }));
        Assert.False(result.Success);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT 7 AS id) INSERT INTO users(id) VALUES (7)")]
    [InlineData("WITH picked AS (SELECT 7 AS id) INSERT INTO users(id) DEFAULT VALUES")]
    [InlineData("WITH picked AS (DELETE FROM users) INSERT INTO users(id) SELECT id FROM picked")]
    [InlineData("WITH RECURSIVE picked AS (SELECT 7 AS id) INSERT INTO users(id) SELECT id FROM picked")]
    [InlineData("WITH picked AS (SELECT 7 AS id) INSERT INTO users(id) WITH inner_rows AS (SELECT id FROM picked) SELECT id FROM inner_rows")]
    public void UnmodeledStatementCteInsertFormsStayFailClosed(string sql)
    {
        var script = SqlCoreFacade.ParseScript(sql,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer, Profile(),
                TestContext.Current.CancellationToken));
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(script.Statements).Status);
        Assert.NotEmpty(script.Diagnostics);
    }

    [Fact]
    public void SourceSqlServer2000CannotClaimStatementLevelCteInsert()
    {
        var old = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(8, 0));
        var parsed = SqlCoreFacade.ParseScript(Insert,
            new SqlScriptParseOptions(SqlAgentToolType.MsSqlServer, old,
                TestContext.Current.CancellationToken));
        Assert.Equal(SqlScriptParseStatus.Invalid, Assert.Single(parsed.Statements).Status);
        Assert.NotEmpty(parsed.Diagnostics);
    }

    [Fact]
    public void TargetSqlServer2000CannotCompileCteInsertFromModernSource()
    {
        var old = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(8, 0));
        var exception = Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(
            Insert, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("sqlserver-cte-insert-old-target",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }),
            new DmlCompilationPolicy(), Profile(), old,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)));
        Assert.Contains("sqlserver.with_mutation_cte", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("WITH insert AS (SELECT 7 AS id) SELECT id FROM insert")]
    [InlineData("WITH picked AS (SELECT 7 AS id) SELECT id FROM picked")]
    public void OrdinarySqlServerWithQueryIsNeverReclassifiedAsMutation(string sql)
    {
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer, Profile());
        var statement = Assert.Single(parsed.Statements);
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(SqlScriptStatementKind.Query, statement.Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, statement.Status);
    }

    [Fact]
    public void OriginalInsertSelectWithoutWithStillWorksWithLegacyProfile()
    {
        var parsed = SqlCoreFacade.ParseScript(
            "INSERT INTO users(id) SELECT 7 AS id", SqlAgentToolType.MsSqlServer,
            new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(8, 0)));
        Assert.Equal(SqlScriptParseStatus.Parsed, Assert.Single(parsed.Statements).Status);
    }

    private static CompiledSqlCommand Compile(string sql, SqlPlanValidationContext context)
    {
        var profile = Profile();
        return SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            context, new DmlCompilationPolicy(), profile, profile,
            new DmlConflictTargetAssurance(ImmutableArray<string>.Empty));
    }
}
