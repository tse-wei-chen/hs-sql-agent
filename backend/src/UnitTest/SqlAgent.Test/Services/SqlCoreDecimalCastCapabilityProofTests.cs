using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Closed SQL Decimal(p,s) types must respect source and target dialect
/// precision/scale capability, including same-dialect CAST fast paths.
/// A successful typed parse is never itself an executable target proof.
/// </summary>
public sealed class SqlCoreDecimalCastCapabilityProofTests
{
    public static IEnumerable<object[]> InvalidSourceShapes()
    {
        yield return [SqlAgentToolType.Postgres, "NUMERIC(1001,0)", "1000"];
        yield return [SqlAgentToolType.MySQL, "DECIMAL(66,0)", "65"];
        yield return [SqlAgentToolType.MySQL, "DECIMAL(65,31)", "30"];
        yield return [SqlAgentToolType.MsSqlServer, "DECIMAL(39,0)", "38"];
        yield return [SqlAgentToolType.Oracle, "NUMBER(39,0)", "38"];
        yield return [SqlAgentToolType.Firebird, "DECIMAL(39,0)", "38"];
    }

    public static IEnumerable<object[]> InvalidCrossTargets()
    {
        yield return [SqlAgentToolType.MsSqlServer, "DECIMAL(39,0)", "38"];
        yield return [SqlAgentToolType.MySQL, "NUMERIC(66,0)", "65"];
        yield return [SqlAgentToolType.MySQL, "NUMERIC(40,31)", "30"];
        yield return [SqlAgentToolType.Oracle, "NUMERIC(39,0)", "38"];
        yield return [SqlAgentToolType.Firebird, "NUMERIC(39,0)", "38"];
        // SQLite NUMERIC is affinity, not DECIMAL(p,s) scale enforcement.
        yield return [SqlAgentToolType.Sqlite, "NUMERIC(18,4)", "affinity"];
    }

    private static string Sql(string type) =>
        $"SELECT CAST(amount AS {type}) AS converted FROM orders";

    private static CompiledSqlCommand Compile(
        SqlAgentToolType source, SqlAgentToolType target, string type) =>
        SqlCoreFacade.CompileQuery(Sql(type), source, target,
            new SqlPlanValidationContext("decimal-cast-proof-v1"),
            new SqlExecutionPlanPolicy());

    [Theory]
    [MemberData(nameof(InvalidSourceShapes))]
    public void SourceShape_IsRejectedBeforeCompilationEvenForSameDialect(
        SqlAgentToolType source, string type, string limit)
    {
        var diagnostic = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(Sql(type), source));
        Assert.Equal("SQL_SOURCE_TYPE_REJECTED", diagnostic.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.SourceValidation, diagnostic.Diagnostic?.Stage);
        Assert.Contains(limit, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidCrossTargets))]
    public void CrossTargetShape_FailsClosedBeforeRendering(
        SqlAgentToolType target, string type, string limit)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(SqlAgentToolType.Postgres, target, type));

        Assert.Contains(limit, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DECIMAL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "NUMERIC(1000,0)")]
    [InlineData(SqlAgentToolType.MySQL, "DECIMAL(65,30)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "DECIMAL(38,38)")]
    [InlineData(SqlAgentToolType.Oracle, "NUMBER(38,38)")]
    [InlineData(SqlAgentToolType.Firebird, "DECIMAL(18,18)")]
    [InlineData(SqlAgentToolType.Sqlite, "NUMERIC")]
    public void NativeSupportedBoundary_StillCompiles(
        SqlAgentToolType provider, string type)
    {
        var command = Compile(provider, provider, type);
        Assert.Contains(type, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "NUMERIC(65,30)")]
    [InlineData(SqlAgentToolType.MsSqlServer, "NUMERIC(38,38)")]
    [InlineData(SqlAgentToolType.Oracle, "NUMERIC(38,38)")]
    public void PostgreSqlSource_LowersToTargetMaximumWithoutClamping(
        SqlAgentToolType target, string sourceType)
    {
        var command = Compile(SqlAgentToolType.Postgres, target, sourceType);
        var rendered = target == SqlAgentToolType.Oracle
            ? sourceType.Replace("NUMERIC", "NUMBER", StringComparison.Ordinal)
            : sourceType.Replace("NUMERIC", "DECIMAL", StringComparison.Ordinal);
        Assert.Contains(rendered, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirebirdExtendedDecimal_RequiresVersion4ForSourceAndTarget()
    {
        const string type = "DECIMAL(20,4)";
        var sourceSql = Sql(type);
        var sourceError = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(sourceSql, SqlAgentToolType.Firebird));
        Assert.Contains("numeric.decimal_extended", sourceError.Message,
            StringComparison.OrdinalIgnoreCase);

        var oldProfile = FirebirdProfile(3);
        var oldSource = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(sourceSql, SqlAgentToolType.Firebird, oldProfile));
        Assert.Contains("numeric.decimal_extended", oldSource.Message,
            StringComparison.OrdinalIgnoreCase);

        var targetError = Assert.Throws<SqlCompilationException>(() =>
            Compile(SqlAgentToolType.Postgres, SqlAgentToolType.Firebird, type));
        Assert.Contains("numeric.decimal_extended", targetError.Message,
            StringComparison.OrdinalIgnoreCase);

        var v4 = FirebirdProfile(4);
        var own = SqlCoreFacade.CompileQuery(sourceSql,
            SqlAgentToolType.Firebird, SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("decimal-firebird4-v1"),
            new SqlExecutionPlanPolicy(), v4, v4);
        Assert.Contains(type, own.Sql, StringComparison.OrdinalIgnoreCase);

        var cross = SqlCoreFacade.CompileQuery(sourceSql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("decimal-firebird4-cross-v1"),
            new SqlExecutionPlanPolicy(), v4);
        Assert.Contains(type, cross.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void UnboundedPostgreSqlNumeric_DoesNotAcquireImplicitTargetPrecision(
        SqlAgentToolType target)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(SqlAgentToolType.Postgres, target, "NUMERIC"));
        Assert.Contains("Unbounded", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("precision", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptClassification_RecordsInvalidSourceCastWithoutThrowing()
    {
        const string sql = "SELECT CAST(amount AS DECIMAL(39,0)) FROM orders; SELECT 1";
        var result = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Statements.Count);
        Assert.Equal(HsSqlAgent.SqlCore.Script.SqlScriptParseStatus.Invalid,
            result.Statements[0].Status);
        Assert.Equal("SQL_SOURCE_TYPE_REJECTED", result.Statements[0].Diagnostic?.Code);
        Assert.Equal(HsSqlAgent.SqlCore.Script.SqlScriptParseStatus.Parsed,
            result.Statements[1].Status);
    }

    [Fact]
    public void InvalidTargetDecimalShape_CannotBeAuthorizedThroughDml()
    {
        var sql = "DELETE FROM orders WHERE id = CAST(7 AS DECIMAL(39,0))";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml(sql,
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("decimal-dml-v1"),
                TestContext.Current.CancellationToken));
        Assert.Contains("38", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlProviderCapabilityProfile FirebirdProfile(int major) =>
        new(SqlAgentToolType.Firebird, new Version(major, 0));
}
