using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Source-dialect EXTRACT is not a portable synonym for the canonical date-part model.
/// In particular, MySQL native WEEK depends on default_week_format, whereas Core's
/// WEEK representation is ISO. Rejection must happen at Parse, not target rendering.
/// </summary>
public sealed class CoreExtractSourceDialectFenceTests
{
    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void NonExtractSourceDialects_RejectEvenBasicDatePartAtParse(SqlAgentToolType source)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            Parse(source, "YEAR"));
        Assert.Contains("EXTRACT", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlSourceWeek_RejectsUnprovenSessionDependentNumbering()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            Parse(SqlAgentToolType.MySQL, "WEEK"));
        Assert.Contains("WEEK", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("EPOCH")]
    [InlineData("CENTURY")]
    public void MySqlSource_RejectsBorrowedPostgresExtendedFieldsAtParse(string field)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            Parse(SqlAgentToolType.MySQL, field));
        Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("QUARTER")]
    [InlineData("WEEK")]
    [InlineData("EPOCH")]
    public void OracleSource_RejectsUnmodeledNativeExtractFieldsAtParse(string field)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            Parse(SqlAgentToolType.Oracle, field));
        Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "YEAR")]
    [InlineData(SqlAgentToolType.MySQL, "QUARTER")]
    [InlineData(SqlAgentToolType.Oracle, "YEAR")]
    [InlineData(SqlAgentToolType.Oracle, "HOUR")]
    [InlineData(SqlAgentToolType.Firebird, "WEEKDAY")]
    public void ProvenNativeSourceForms_RemainParseable(SqlAgentToolType source, string field)
    {
        Assert.NotNull(Parse(source, field));
    }

    private static object Parse(SqlAgentToolType source, string field) =>
        CoreSqlTextParser.ParseQuery(
            "SELECT EXTRACT(" + field + " FROM event_time) AS part_value FROM events",
            source);
}
