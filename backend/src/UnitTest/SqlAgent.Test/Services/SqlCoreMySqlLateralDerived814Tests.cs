using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// MySQL 8.0.14+ native LATERAL must keep the existing closed F#
/// LateralDerivedTable and outer-scope binder; no SQL text interpolation.
/// </summary>
public sealed class SqlCoreMySqlLateralDerived814Tests
{
    private const string CrossJoin =
        "SELECT u.id, q.id AS selected_id FROM users u " +
        "CROSS JOIN LATERAL (SELECT u.id AS id) q ORDER BY u.id";
    private const string InnerJoin =
        "SELECT u.id, q.id AS selected_id FROM users u " +
        "INNER JOIN LATERAL (SELECT u.id AS id) q ON TRUE ORDER BY u.id";
    private const string LeftJoin =
        "SELECT u.id, q.id AS selected_id FROM users u " +
        "LEFT JOIN LATERAL (SELECT u.id AS id WHERE u.id = 2) q ON TRUE ORDER BY u.id";

    [Theory]
    [InlineData(CrossJoin, "CROSS JOIN LATERAL")]
    [InlineData(InnerJoin, "INNER JOIN LATERAL")]
    [InlineData(LeftJoin, "LEFT JOIN LATERAL")]
    public void MySql814CorrelatedJoinIsTypedAndRenderedNatively(string sql, string clause)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MySQL, Profile(8, 0, 14));
        var selection = Assert.IsType<SelectStatement>(parsed.Statement);
        Assert.True(Assert.IsType<DerivedTableSource>(Assert.Single(selection.Joins).Source).IsLateral);
        var command = Compile(parsed, Profile(8, 0, 14));
        Assert.Equal(SqlStatementKind.Select, command.Kind);
        Assert.Contains(clause, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" ORDER BY ", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(8, 0, 13)]
    [InlineData(8, 0, 0)]
    [InlineData(5, 7, 44)]
    public void OlderMySqlCannotParseLateral(int major, int minor, int patch)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(CrossJoin, SqlAgentToolType.MySQL, Profile(major, minor, patch)));
        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8.0.14", error.Message, StringComparison.Ordinal);
        Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySql814SourceCannotCompileToOldMySqlTarget()
    {
        var parsed = CoreSqlTextParser.ParseQuery(CrossJoin, SqlAgentToolType.MySQL, Profile(8, 0, 14));
        var error = Assert.Throws<SqlCompilationException>(() => Compile(parsed, Profile(8, 0, 13)));
        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlMissingProfileCannotClaimLateralSupport()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(CrossJoin, SqlAgentToolType.MySQL));
        Assert.Contains("select.lateral_derived", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void UnprovenSourceDialectsRemainRejected(SqlAgentToolType source)
    {
        Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseQuery(CrossJoin, source));
    }

    [Fact]
    public void CrossProviderLateralStillFailsClosed()
    {
        var parsed = CoreSqlTextParser.ParseQuery(CrossJoin, SqlAgentToolType.MySQL, Profile(8, 0, 14));
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres, new Version(16, 0)),
                SqlAgentToolType.Postgres));
        Assert.Contains("lateral", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT u.id FROM users u RIGHT JOIN LATERAL (SELECT u.id AS id) q ON TRUE")]
    [InlineData("SELECT u.id FROM users u FULL JOIN LATERAL (SELECT u.id AS id) q ON TRUE")]
    [InlineData("SELECT u.id FROM users u JOIN LATERAL users q ON TRUE")]
    public void InvalidLateralJoinShapesFailClosed(string sql)
    {
        // Unsupported FULL JOIN may fail earlier at source capability
        // validation (SqlCompilationException); RIGHT JOIN LATERAL is
        // rejected by the lateral grammar (SqlParseException).
        var error = Record.Exception(() =>
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MySQL, Profile(8, 0, 14)));
        Assert.NotNull(error);
        Assert.True(error is SqlParseException or SqlCompilationException, error.ToString());
    }

    [Fact]
    public void NonLateralDerivedTableCannotCorrelateWithPreviousSource()
    {
        const string sql = "SELECT u.id, q.id FROM users u CROSS JOIN (SELECT u.id AS id) q";
        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MySQL, Profile(8, 0, 14));
        var error = Assert.Throws<InvalidOperationException>(() => Compile(parsed, Profile(8, 0, 14)));
        Assert.Contains("unknown table/alias qualifier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityMatrixSeparatesOldAndNewMySqlVersions()
    {
        Assert.Equal(SqlCapabilityStatus.Rejected, Capability(SqlAgentToolType.MySQL, null).Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, Capability(SqlAgentToolType.MySQL, Profile(8, 0, 13)).Status);
        Assert.Equal(SqlCapabilityStatus.Supported, Capability(SqlAgentToolType.MySQL, Profile(8, 0, 14)).Status);
        Assert.Equal(SqlCapabilityStatus.Supported, Capability(SqlAgentToolType.Postgres, null).Status);
        Assert.Equal(SqlCapabilityStatus.Rejected, Capability(SqlAgentToolType.Sqlite, null).Status);
    }

    private static CompiledSqlCommand Compile(ParsedStatement parsed,
        SqlProviderCapabilityProfile profile, SqlAgentToolType target = SqlAgentToolType.MySQL) =>
        CoreSqlCompiler.CreateDefault().Compile(parsed, target,
            new SqlPlanValidationContext("mysql-lateral-814"),
            new SqlExecutionPlanPolicy(), profile);

    private static SqlCapability Capability(SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile) =>
        Assert.Single(SqlCapabilityMatrix.ForProvider(provider, profile).Capabilities,
            x => x.Id == "select.lateral_derived");

    private static SqlProviderCapabilityProfile Profile(int major, int minor, int patch) =>
        new(SqlAgentToolType.MySQL, ServerVersion: new Version(major, minor, patch));
}
