using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Oracle Database 12.1+ LATERAL inline views reuse the closed F#
/// LateralDerivedTable AST and the existing scope-aware binder.
/// </summary>
public sealed class SqlCoreOracleLateralDerived121Tests
{
    private const string Cross =
        "SELECT u.ID, q.AMOUNT FROM USERS u CROSS JOIN LATERAL " +
        "(SELECT o.AMOUNT AS AMOUNT FROM ORDERS o WHERE o.USER_ID = u.ID AND o.AMOUNT >= 5) q ORDER BY u.ID";
    private const string Inner =
        "SELECT u.ID, q.AMOUNT FROM USERS u INNER JOIN LATERAL " +
        "(SELECT o.AMOUNT AS AMOUNT FROM ORDERS o WHERE o.USER_ID = u.ID) q ON 1 = 1 ORDER BY u.ID";
    private const string Left =
        "SELECT u.ID, q.AMOUNT FROM USERS u LEFT JOIN LATERAL " +
        "(SELECT o.AMOUNT AS AMOUNT FROM ORDERS o WHERE o.USER_ID = u.ID) q ON 1 = 1 ORDER BY u.ID";

    [Theory]
    [InlineData(Cross, "CROSS JOIN LATERAL")]
    [InlineData(Inner, "INNER JOIN LATERAL")]
    [InlineData(Left, "LEFT JOIN LATERAL")]
    public void Oracle121CorrelatedLateralParsesAndCompilesNatively(string sql, string clause)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle, Oracle(12, 1));
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var source = Assert.IsType<DerivedTableSource>(Assert.Single(select.Joins).Source);
        Assert.True(source.IsLateral);
        var command = Compile(parsed, Oracle(12, 1));
        Assert.Equal(SqlStatementKind.Select, command.Kind);
        Assert.Contains(clause, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" ORDER BY ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(11, 2)]
    [InlineData(12, 0)]
    public void Pre121SourceMustFailClosed(int major, int minor)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(Cross, SqlAgentToolType.Oracle, Oracle(major, minor)));
        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12.1", error.Message, StringComparison.Ordinal);
        Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UndeclaredOracleSourceVersionCannotClaimLateral()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(Cross, SqlAgentToolType.Oracle));
        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pre121OracleTargetMustFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseQuery(Cross, SqlAgentToolType.Oracle, Oracle(12, 1));
        var error = Assert.Throws<SqlCompilationException>(() => Compile(parsed, Oracle(11, 2)));
        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UndeclaredOracleTargetVersionCannotClaimLateral()
    {
        var parsed = CoreSqlTextParser.ParseQuery(Cross, SqlAgentToolType.Oracle, Oracle(12, 1));
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(parsed, SqlAgentToolType.Oracle,
                new SqlPlanValidationContext("oracle-lateral-unknown-target"),
                new SqlExecutionPlanPolicy()));
        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT u.ID FROM USERS u RIGHT JOIN LATERAL (SELECT u.ID AS ID FROM DUAL) q ON 1=1")]
    [InlineData("SELECT u.ID FROM USERS u FULL JOIN LATERAL (SELECT u.ID AS ID FROM DUAL) q ON 1=1")]
    [InlineData("SELECT u.ID FROM USERS u JOIN LATERAL ORDERS o ON 1=1")]
    public void OracleUnprovenLateralJoinShapesRemainDenied(string sql)
    {
        var failure = Record.Exception(() =>
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle, Oracle(19, 0)));
        Assert.NotNull(failure);
        Assert.True(failure is SqlParseException or SqlCompilationException, failure.ToString());
    }

    [Fact]
    public void PlainOracleDerivedTableCannotReferencePreviousFromSource()
    {
        const string sql = "SELECT u.ID, q.ID FROM USERS u CROSS JOIN (SELECT u.ID AS ID FROM DUAL) q";
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Oracle, Oracle(19, 0));
        var error = Assert.Throws<InvalidOperationException>(() => Compile(parsed, Oracle(19, 0)));
        Assert.Contains("unknown table/alias qualifier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    public void OracleNativeLateralCannotBeLoweredAcrossProvider(SqlAgentToolType target)
    {
        var parsed = CoreSqlTextParser.ParseQuery(Cross, SqlAgentToolType.Oracle, Oracle(19, 0));
        var profile = target == SqlAgentToolType.Postgres
            ? new SqlProviderCapabilityProfile(target, new Version(16, 0))
            : new SqlProviderCapabilityProfile(target, new Version(8, 0, 36));
        var error = Assert.Throws<SqlCompilationException>(() => Compile(parsed, profile, target));
        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OracleMatrixSeparatesRuntimeVersions()
    {
        Assert.Equal(SqlCapabilityStatus.Rejected, Capability(SqlAgentToolType.Oracle, null).Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, Capability(SqlAgentToolType.Oracle, Oracle(11, 2)).Status);
        Assert.Equal(SqlCapabilityStatus.Supported, Capability(SqlAgentToolType.Oracle, Oracle(12, 1)).Status);
        Assert.Equal(SqlCapabilityStatus.Supported, Capability(SqlAgentToolType.Oracle, Oracle(19, 0)).Status);
        Assert.Equal(SqlCapabilityStatus.Supported, Capability(SqlAgentToolType.Postgres, null).Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, Capability(SqlAgentToolType.Sqlite, null).Status);
    }

    private static CompiledSqlCommand Compile(ParsedStatement parsed,
        SqlProviderCapabilityProfile profile, SqlAgentToolType target = SqlAgentToolType.Oracle) =>
        CoreSqlCompiler.CreateDefault().Compile(parsed, target,
            new SqlPlanValidationContext("oracle-lateral-121"),
            new SqlExecutionPlanPolicy(), profile);

    private static SqlCapability Capability(SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile) =>
        Assert.Single(SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities,
            x => x.Id == "select.lateral_derived");

    private static SqlProviderCapabilityProfile Oracle(int major, int minor) =>
        new(SqlAgentToolType.Oracle, ServerVersion: new Version(major, minor));
}
