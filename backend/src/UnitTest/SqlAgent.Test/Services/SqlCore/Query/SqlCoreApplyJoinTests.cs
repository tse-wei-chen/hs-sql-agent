using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreApplyJoinTests
{
    private const string Tail = "(SELECT o.AMOUNT AS AMOUNT FROM ORDERS o WHERE o.USER_ID = u.ID AND o.AMOUNT >= 5) q ORDER BY u.ID";
    private static string Statement(string apply) => "SELECT u.ID, q.AMOUNT FROM USERS u " + apply + " " + Tail;
    private static SqlProviderCapabilityProfile Profile(SqlAgentToolType p, int major, int minor = 0)
        => new(p, ServerVersion: new Version(major, minor));

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, "CROSS APPLY")]
    [InlineData(SqlAgentToolType.MsSqlServer, "OUTER APPLY")]
    [InlineData(SqlAgentToolType.Oracle, "CROSS APPLY")]
    [InlineData(SqlAgentToolType.Oracle, "OUTER APPLY")]
    public void NativeApplyRoundTripsAsTypedJoin(SqlAgentToolType provider, string apply)
    {
        var profile = Profile(provider, provider == SqlAgentToolType.Oracle ? 19 : 16);
        var parsed = CoreSqlTextParser.ParseQuery(Statement(apply), provider, profile);
        var stmt = Assert.IsType<SelectStatement>(parsed.Statement);
        var join = Assert.Single(stmt.Joins);
        Assert.Equal(apply, join.Kind);
        Assert.IsType<DerivedTableSource>(join.Source);
        var result = SqlCoreFacade.CompileQuery(
            Statement(apply), provider, provider,
            new SqlPlanValidationContext("apply-native"),
            new SqlExecutionPlanPolicy(0), profile, profile);
        Assert.Equal(SqlStatementKind.Select, result.Kind);
        Assert.Contains(apply + " (", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" JOIN ", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Parameters, p => Convert.ToInt64(p.Value) == 5L);
    }

    [Fact]
    public void SqlServerApplyRequiresNoOnAndRecognizesCorrelatedScope()
    {
        const string sql = "SELECT u.ID, q.ID FROM USERS u OUTER APPLY (SELECT u.ID AS ID) q";
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MsSqlServer);
        var command = SqlCoreFacade.CompileQuery(sql, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("apply-correlation"), new SqlExecutionPlanPolicy(0));
        Assert.Equal("OUTER APPLY", Assert.Single(Assert.IsType<SelectStatement>(parsed.Statement).Joins).Kind);
        Assert.Contains("OUTER APPLY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherSourceDialectsRejectApply(SqlAgentToolType source)
    {
        var error = Record.Exception(() => CoreSqlTextParser.ParseQuery(Statement("CROSS APPLY"), source));
        Assert.NotNull(error);
        Assert.True(error is SqlParseException or SqlCompilationException, error.ToString());
    }

    [Theory]
    [InlineData("CROSS APPLY (SELECT u.ID AS ID) q ON 1 = 1")]
    [InlineData("OUTER APPLY (SELECT u.ID AS ID) q USING (ID)")]
    [InlineData("CROSS APPLY LATERAL (SELECT u.ID AS ID) q")]
    [InlineData("OUTER APPLY ORDERS o")]
    public void InvalidApplyShapesFailClosed(string suffix)
    {
        var error = Record.Exception(() => CoreSqlTextParser.ParseQuery(
            "SELECT u.ID FROM USERS u " + suffix, SqlAgentToolType.MsSqlServer));
        Assert.NotNull(error);
        Assert.True(error is SqlParseException or SqlCompilationException, error.ToString());
    }

    [Theory]
    [InlineData(11, 2)]
    [InlineData(12, 0)]
    public void OracleLegacySourceVersionFailsClosed(int major, int minor)
    {
        var error = Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseQuery(
            Statement("OUTER APPLY"), SqlAgentToolType.Oracle, Profile(SqlAgentToolType.Oracle, major, minor)));
        Assert.Contains("select.apply_join", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OracleUndeclaredSourceVersionFailsClosed()
    {
        var error = Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseQuery(
            Statement("CROSS APPLY"), SqlAgentToolType.Oracle));
        Assert.Contains("select.apply_join", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OracleOldTargetVersionFailsClosed()
    {
        var source = Profile(SqlAgentToolType.Oracle, 19);
        var parsed = CoreSqlTextParser.ParseQuery(Statement("OUTER APPLY"), SqlAgentToolType.Oracle, source);
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(parsed, SqlAgentToolType.Oracle,
                new SqlPlanValidationContext("oracle-apply-old-target"),
                new SqlExecutionPlanPolicy(0), Profile(SqlAgentToolType.Oracle, 11, 2)));
        Assert.Contains("select.apply_join", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle, SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlAgentToolType.Oracle)]
    public void CrossProviderApplyRemainsFailClosed(SqlAgentToolType source, SqlAgentToolType target)
    {
        var profile = Profile(source, source == SqlAgentToolType.Oracle ? 19 : 16);
        var targetProfile = Profile(target, target == SqlAgentToolType.Oracle ? 19 : 16);
        var parsed = CoreSqlTextParser.ParseQuery(Statement("CROSS APPLY"), source, profile);
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(parsed, target,
                new SqlPlanValidationContext("apply-cross-provider"),
                new SqlExecutionPlanPolicy(0), targetProfile));
        Assert.Contains("select.apply_join", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderMatrixDistinguishesApplySupport()
    {
        SqlCapability Cap(SqlAgentToolType p, SqlProviderCapabilityProfile? profile) =>
            Assert.Single(SqlCapabilityMatrix.ForProvider(p, profile).Capabilities, x => x.Id == "select.apply_join");
        Assert.Equal(SqlCapabilityStatus.Supported, Cap(SqlAgentToolType.MsSqlServer, null).Status);
        Assert.Equal(SqlCapabilityStatus.Supported, Cap(SqlAgentToolType.Oracle, Profile(SqlAgentToolType.Oracle, 12, 1)).Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, Cap(SqlAgentToolType.Oracle, Profile(SqlAgentToolType.Oracle, 11, 2)).Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, Cap(SqlAgentToolType.Oracle, null).Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, Cap(SqlAgentToolType.Postgres, null).Status);
    }
}
