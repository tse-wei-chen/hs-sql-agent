using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// PostgreSQL read-only statement CTEs preceding INSERT SELECT must compose
/// with native RETURNING and ON CONFLICT without inventing conflict-key
/// assurances, bypassing target policy, or widening other dialects.
/// </summary>
public sealed class SqlCorePostgresWithInsertModifiersTests
{
    [Theory]
    [InlineData("WITH source_rows AS (SELECT 17 AS id) INSERT INTO users(id) SELECT id FROM source_rows RETURNING id")]
    [InlineData("WITH source_rows AS (SELECT 17 AS id) INSERT INTO users(id) SELECT id FROM source_rows ON CONFLICT (id) DO NOTHING")]
    [InlineData("WITH source_rows AS (SELECT 17 AS id) INSERT INTO users(id) SELECT id FROM source_rows ON CONFLICT (id) DO NOTHING RETURNING id")]
    [InlineData("WITH source_rows AS (SELECT 17 AS id) INSERT INTO users(id) SELECT id FROM source_rows ON CONFLICT (id) DO UPDATE SET id = EXCLUDED.id RETURNING id")]
    public void AdmittedPostgresInsertModifiersRemainTypedMutations(string sql)
    {
        var syntax = SqlCoreFacade.ParseScript(sql + "; SELECT 2;", SqlAgentToolType.Postgres);
        Assert.Empty(syntax.Diagnostics);
        Assert.Equal(2, syntax.Statements.Count);
        Assert.Equal(SqlScriptStatementKind.Mutation, syntax.Statements[0].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, syntax.Statements[0].Status);
        Assert.Equal(SqlScriptStatementKind.Query, syntax.Statements[1].Kind);
        Assert.Equal(SqlScriptParseStatus.Parsed, syntax.Statements[1].Status);
        Assert.Equal(sql, syntax.Statements[0].Text);
    }

    [Theory]
    [InlineData(" RETURNING id", false)]
    [InlineData(" ON CONFLICT (id) DO NOTHING", true)]
    [InlineData(" ON CONFLICT (id) DO NOTHING RETURNING id", true)]
    public void ReturningAndDoNothingCompileThroughOriginalMutationPolicy(string suffix, bool hasConflict)
    {
        var sql = "WITH source_rows AS (SELECT 17 AS id) " +
                  "INSERT INTO users(id) SELECT id FROM source_rows" + suffix;
        var command = SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("with-insert-modifiers",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));

        Assert.StartsWith("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(command.Parameters, p => Equals(p.Value, 17));
        Assert.DoesNotContain("SELECT 17", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(hasConflict,
            command.Sql.Contains("ON CONFLICT", StringComparison.OrdinalIgnoreCase));
        if (suffix.Contains("RETURNING", StringComparison.Ordinal))
            Assert.Contains("RETURNING", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoWriteTargetPermissionIsIntroducedByCteModifiers()
    {
        var sql = "WITH source_rows AS (SELECT 17 AS id) " +
                  "INSERT INTO secrets(id) SELECT id FROM source_rows " +
                  "ON CONFLICT (id) DO NOTHING RETURNING id";
        var result = SqlCoreFacade.TryCompileDml(sql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("with-insert-deny",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" }));
        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.NotEmpty(result.TypedDiagnostics);
    }

    [Fact]
    public void ConflictUpdateSourceCardinalityIsNeverGuessedFromReadOnlyCte()
    {
        var sql = "WITH source_rows AS (SELECT 17 AS id) " +
                  "INSERT INTO users(id) SELECT id FROM source_rows " +
                  "ON CONFLICT (id) DO UPDATE SET id = EXCLUDED.id RETURNING id";
        var error = Assert.ThrowsAny<Exception>(() =>
            SqlCoreFacade.CompileDml(sql,
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("with-insert-no-source-uniqueness",
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" })));
        Assert.Contains("assurance", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ON CONFLICT DO UPDATE SET id = EXCLUDED.id")]
    [InlineData("ON CONFLICT (id) DO UPDATE SET id = NOW()")]
    [InlineData("ON CONFLICT (id) DO NOTHING WHERE id > 1")]
    public void UnmodeledConflictSemanticsRemainInvalid(string clause)
    {
        var sql = "WITH source_rows AS (SELECT 17 AS id) " +
                  "INSERT INTO users(id) SELECT id FROM source_rows " + clause;
        var script = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(script.Statements).Status);
        Assert.NotEmpty(script.Diagnostics);
        Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileDml(sql, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres, new SqlPlanValidationContext("with-insert-unsupported")));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherSourceDialectsCannotBorrowPostgresWithInsertModifiers(SqlAgentToolType source)
    {
        const string sql = "WITH source_rows AS (SELECT 17 AS id) " +
                           "INSERT INTO users(id) SELECT id FROM source_rows RETURNING id";
        var script = SqlCoreFacade.ParseScript(sql, source);
        Assert.NotEqual(SqlScriptParseStatus.Parsed, Assert.Single(script.Statements).Status);
    }
}
