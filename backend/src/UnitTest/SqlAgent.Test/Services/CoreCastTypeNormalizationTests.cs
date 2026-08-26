using Xunit;

namespace SqlAgent.Test.Services;

public class CoreCastTypeNormalizationTests
{
    [Fact]
    public void Compile_PostgresTimestamp_ToSqlServer_UsesDatetime2NotRowversionTimestamp()
    {
        var command = Compile(
            "SELECT CAST(created_at AS TIMESTAMP) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains("AS DATETIME2", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AS TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerTimestamp_ToPostgres_PreservesRowversionBinarySemantics()
    {
        var command = Compile(
            "SELECT CAST(rv AS TIMESTAMP) FROM audit_log",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);

        Assert.Contains("AS BYTEA", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AS TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerDatetime2_ToPostgres_UsesTemporalTimestamp()
    {
        var command = Compile(
            "SELECT CAST(created_at AS DATETIME2) FROM orders",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);

        Assert.Contains("AS TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_OracleDate_ToPostgres_PreservesOracleTimeOfDaySemantics()
    {
        var command = Compile(
            "SELECT CAST(created_at AS DATE) FROM orders",
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Postgres);

        Assert.Contains("AS TIMESTAMP", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DateOnly_ToOracle_RemainsDate()
    {
        var command = Compile(
            "SELECT CAST(order_date AS DATE) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle);

        Assert.Contains("AS DATE", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Boolean_ToMySql_UsesSupportedSignedCastTarget()
    {
        var command = Compile(
            "SELECT CAST(is_active AS BOOLEAN) FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL);

        Assert.Contains("AS SIGNED", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_Uuid_ToSqlServer_UsesUniqueIdentifier()
    {
        var command = Compile(
            "SELECT CAST(user_id AS UUID) FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains("AS UNIQUEIDENTIFIER", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlUnsigned_ToSqlServer_PreservesUnsignedRange()
    {
        var command = Compile(
            "SELECT CAST(value AS UNSIGNED) FROM metrics",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains("AS DECIMAL(20,0)", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ExactNumericPrecision_ToOracle_PreservesPrecisionAndScale()
    {
        var command = Compile(
            "SELECT CAST(amount AS NUMERIC(18,4)) FROM orders",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle);

        Assert.Contains("AS NUMBER(18,4)", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SameDialectVendorCast_RemainsSupportedWithoutPortableMapping()
    {
        var command = Compile(
            "SELECT CAST(address AS INET) FROM hosts",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Contains("AS INET", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_UnmodeledVendorCastAcrossDialects_FailsBeforeLowering()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                "SELECT CAST(address AS INET) FROM hosts",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MySQL));

        Assert.Contains("no cross-dialect Core semantic mapping", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_UnboundedNumeric_ToBoundedProvider_RequiresExplicitPrecision()
    {
        var ex = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                "SELECT CAST(amount AS NUMERIC) FROM orders",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MsSqlServer));

        Assert.Contains("specify precision and scale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider)
    {
        var parsed = CoreSqlTextParser.ParseQuery(sql, sourceDialect);
        return CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
    }
}
