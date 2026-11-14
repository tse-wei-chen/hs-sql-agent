using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreNaturalJoinCapabilityTests
{
    [Fact]
    public void Parse_PostgresNaturalJoin_IsStructured()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM alpha NATURAL JOIN beta",
            SqlAgentToolType.Postgres);
        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var join = Assert.Single(select.Joins);

        Assert.True(join.IsNatural);
        Assert.Equal("INNER", join.Kind, ignoreCase: true);
        Assert.Null(join.Predicate);
        Assert.True(join.UsingColumns.IsDefaultOrEmpty);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void Compile_NaturalJoin_UsesNativeProviderSyntax(SqlAgentToolType provider)
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM alpha NATURAL LEFT JOIN beta",
            provider);

        var command = Compile(parsed, provider);

        Assert.Contains("NATURAL LEFT JOIN", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresNaturalJoinLateral_PreservesNativeCorrelationAndNaturalMatch()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT u.id FROM users u NATURAL JOIN LATERAL (SELECT u.id AS id) q",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        var join = Assert.Single(select.Joins);
        Assert.True(join.IsNatural);
        var lateral = Assert.IsType<DerivedTableSource>(join.Source);
        Assert.True(lateral.IsLateral);

        var command = Compile(parsed, SqlAgentToolType.Postgres);

        Assert.Contains("NATURAL JOIN LATERAL", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresNaturalLeftJoinLateral_PreservesNativeCorrelationAndOuterJoinShape()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT u.id FROM users u NATURAL LEFT JOIN LATERAL (SELECT u.id AS id) q",
            SqlAgentToolType.Postgres);

        var command = Compile(parsed, SqlAgentToolType.Postgres);

        Assert.Contains("NATURAL LEFT JOIN LATERAL", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("RIGHT")]
    [InlineData("FULL")]
    public void Parse_PostgresNaturalRightOrFullJoinLateral_RemainsFailClosed(string joinKind)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                $"SELECT u.id FROM users u NATURAL {joinKind} JOIN LATERAL (SELECT u.id AS id) q",
                SqlAgentToolType.Postgres));

        Assert.Contains("RIGHT/FULL JOIN LATERAL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_SqlServerNaturalJoin_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM alpha NATURAL JOIN beta",
                SqlAgentToolType.MsSqlServer));

        Assert.Contains("join.natural", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MsSqlServer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresNaturalJoinToSqlServer_FailsBeforeRender()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM alpha NATURAL JOIN beta",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(parsed, SqlAgentToolType.MsSqlServer));

        Assert.Contains("join.natural", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MsSqlServer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_NaturalJoinCannotCarryOn()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM alpha NATURAL JOIN beta ON alpha.id = beta.id",
                SqlAgentToolType.Postgres));

        Assert.Contains("NATURAL JOIN", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON or USING", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_NaturalJoinCannotCarryUsing()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM alpha NATURAL JOIN beta USING (id)",
                SqlAgentToolType.Postgres));

        Assert.Contains("NATURAL JOIN", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON or USING", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MySqlNaturalFullJoin_StillUsesExistingFullJoinGate()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT id FROM alpha NATURAL FULL JOIN beta",
                SqlAgentToolType.MySQL));

        Assert.Contains("join.full", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matrix_NaturalJoin_TracksNativeProviderSupport()
    {
        foreach (var provider in new[]
        {
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Oracle
        })
        {
            Assert.Equal(
                SqlCapabilityStatus.Translated,
                Capability(provider).Status);
        }

        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Capability(SqlAgentToolType.MsSqlServer).Status);
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Capability(SqlAgentToolType.Firebird).Status);
    }

    private static SqlCapability Capability(SqlAgentToolType provider) =>
        Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "join.natural");

    private static CompiledSqlCommand Compile(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("natural-join-v1"),
            new SqlExecutionPlanPolicy());
}
