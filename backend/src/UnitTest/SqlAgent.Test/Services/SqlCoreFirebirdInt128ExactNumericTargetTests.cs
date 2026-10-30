using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Firebird 4+ INT128-backed NUMERIC(precision,scale) is a versioned,
/// exact fixed-point contract. A DECFLOAT(34) rounding path must never
/// be substituted for 35-38-digit fixed-point values.
/// </summary>
public sealed class SqlCoreFirebirdInt128ExactNumericTargetTests
{
    public static IEnumerable<object[]> ValidValues()
    {
        // The actual ADO.NET driver has proven typed BigInteger/INT128
        // semantics only for scale-0 values. Fractional values must not
        // silently enter a CLR Decimal or DECFLOAT conversion.
        yield return [new string('9', 38), new string('9', 38), 38, 0];
        yield return ["12345678901234567890123456789012345678",
            "12345678901234567890123456789012345678", 38, 0];
        yield return ["1e+30", "1" + new string('0', 30), 31, 0];
        yield return ["-1e+30", "-1" + new string('0', 30), 31, 0];
    }

    public static IEnumerable<object[]> InvalidValues()
    {
        yield return [new string('9', 39)];
        yield return ["1e-39"];
        yield return [new string('9', 20) + "." + new string('1', 20)];
        yield return ["1e+38"];
        yield return ["1e-1000"];
        yield return ["1e-29"];
        yield return ["-1e-37"];
        yield return ["-0.12345678901234567890123456789"];
        yield return ["-1.00000000000000000000000000001"];
    }

    private static SqlProviderCapabilityProfile Profile(int major) =>
        new(SqlAgentToolType.Firebird, new Version(major, 0));

    private static CompiledSqlCommand Compile(string value, int? major = 5) =>
        SqlCoreFacade.CompileQuery("SELECT " + value + " AS amount FROM RDB$DATABASE",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-int128-exact-v1"),
            new SqlExecutionPlanPolicy(), major is null ? null : Profile(major.Value));

    [Theory]
    [MemberData(nameof(ValidValues))]
    public void Query_UsesBoundFixedDecimalAndVersionProvenINT128(
        string value, string expected, int precision, int scale)
    {
        var command = Compile(value);
        Assert.Contains($"AS NUMERIC({precision},{scale}))", command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value.TrimStart('-') + " AS amount", command.Sql,
            StringComparison.OrdinalIgnoreCase);
        var binding = Assert.Single(command.Parameters);
        Assert.Equal(expected, Assert.IsType<System.Numerics.BigInteger>(binding.Value).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(binding.Name, command.Sql, StringComparison.Ordinal);
        Assert.Contains("AS INT128)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DECFLOAT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ValidValues))]
    public void Dml_UsesSameBoundINT128ShapeWithoutSourceLiteral(
        string value, string expected, int precision, int scale)
    {
        var result = SqlCoreFacade.CompileDml(
            "DELETE FROM metrics WHERE amount = " + value,
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-int128-dml-v1"),
            new DmlCompilationPolicy(), null, Profile(5), null);
        Assert.Contains($"AS NUMERIC({precision},{scale}))", result.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, Assert.IsType<System.Numerics.BigInteger>(Assert.Single(result.Parameters).Value).ToString(
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [MemberData(nameof(ValidValues))]
    public void TargetRequiresExplicitFirebird4PlusProfile(
        string value, string expected, int precision, int scale)
    {
        foreach (var major in new int?[] { null, 3 })
        {
            var error = Assert.Throws<SqlCompilationException>(() => Compile(value, major));
            Assert.Contains("numeric.decimal_extended", error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        var v4 = Compile(value, 4);
        Assert.Contains($"NUMERIC({precision},{scale})", v4.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void TargetRefusesValuesOutsideINT128PrecisionOrScale(string value)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(value));
        Assert.Contains("numeric.exact_literal", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("38", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirebirdSourceRemainsFailClosedForUnprovenHighPrecisionLexer()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery("SELECT 1e-29 FROM RDB$DATABASE",
                SqlAgentToolType.Firebird, SqlAgentToolType.Firebird,
                new SqlPlanValidationContext("firebird-exact-source-v1"),
                new SqlExecutionPlanPolicy(), Profile(5), Profile(5)));
        Assert.Equal("SQL_LEXICAL_NUMERIC_PRECISION_LOSS", error.Diagnostic?.Code);
    }

    [Fact]
    public void PostgreSqlUnboundedAndSQLiteUnprovenContractsAreUnchanged()
    {
        var sql = "SELECT 1e-1000";
        var command = SqlCoreFacade.CompileQuery(sql, SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres, new SqlPlanValidationContext("pg-numeric-backward-v1"),
            new SqlExecutionPlanPolicy());
        Assert.Contains("AS NUMERIC)", command.Sql, StringComparison.OrdinalIgnoreCase);
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(sql, SqlAgentToolType.Postgres,
                SqlAgentToolType.Sqlite, new SqlPlanValidationContext("sqlite-unproven-v1"),
                new SqlExecutionPlanPolicy()));
        Assert.Contains("numeric.exact_literal", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
