using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DialectDmlGrammarMatrixTests
{
    public static IEnumerable<object?[]> SixDialectDmlMatrix() =>
        DmlGrammarMatrixCases.All()
            .Select(item => new object?[]
            {
                item.Name,
                item.Dialect,
                item.Sql,
                item.ExpectedKind,
                item.RenderedFragments,
                item.AllowedTables,
                item.ExpectedParameter
            });

    [Fact]
    public void DmlGrammarCoverage_HasStableCommonNativeAndCartesianFloor()
    {
        var cartesianMatrix =
            DmlPredicateGrammarMatrixTests.UpdatePredicateGrammarMatrix().Count()
            + DmlPredicateGrammarMatrixTests.DeletePredicateGrammarMatrix().Count()
            + DmlPredicateGrammarMatrixTests.InsertSelectGrammarMatrix().Count();

        Assert.Equal(42, DmlGrammarMatrixCases.ExpectedCaseCount);
        Assert.Equal(32, DialectNativeDmlCapabilityMatrixTests.NativeDmlCaseCount);
        Assert.Equal(360, cartesianMatrix);
        Assert.Equal(
            434,
            DmlGrammarMatrixCases.ExpectedCaseCount +
            DialectNativeDmlCapabilityMatrixTests.NativeDmlCaseCount +
            cartesianMatrix);
    }

    [Fact]
    public void SixDialectDmlMatrix_HasStableCoverage()
    {
        var cases = DmlGrammarMatrixCases.All().ToArray();

        Assert.Equal(DmlGrammarMatrixCases.ExpectedCaseCount, cases.Length);
        Assert.Equal(42, cases.Length);
        Assert.Equal(
            42,
            cases.Select(item => item.Name)
                .Distinct(StringComparer.Ordinal)
                .Count());

        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            Assert.Equal(
                7,
                cases.Count(item => item.Dialect == dialect));
        }
    }

    [Theory]
    [MemberData(nameof(SixDialectDmlMatrix))]
    public void SixDialectDmlMatrix_ParsesBindsValidatesCompilesAndRenders(
        string name,
        SqlAgentToolType dialect,
        string sql,
        SqlStatementKind expectedKind,
        string renderedFragments,
        string allowedTablesCsv,
        object? expectedParameter)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            sql,
            dialect);

        switch (expectedKind)
        {
            case SqlStatementKind.Insert:
                Assert.IsType<InsertStatement>(parsed.Statement);
                break;
            case SqlStatementKind.Update:
                Assert.IsType<UpdateStatement>(parsed.Statement);
                break;
            case SqlStatementKind.Delete:
                Assert.IsType<DeleteStatement>(parsed.Statement);
                break;
            default:
                throw new Xunit.Sdk.XunitException(
                    $"{name}: unsupported DML test kind {expectedKind}.");
        }

        var allowedTables = allowedTablesCsv
            .Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validationContext = new SqlPlanValidationContext(
            "six-dialect-dml-grammar-matrix-v1",
            allowedTables);

        var command = SqlCoreFacade.CompileDml(
            sql,
            dialect,
            dialect,
            validationContext);

        Assert.Equal(expectedKind, command.Kind);
        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));

        foreach (var fragment in renderedFragments.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Assert.Contains(
                fragment,
                command.Sql,
                StringComparison.OrdinalIgnoreCase);
        }

        if (expectedParameter is not null)
        {
            Assert.Contains(
                command.Parameters,
                parameter => ParameterEquals(
                    parameter.Value,
                    expectedParameter));
        }

        if (expectedParameter is string text)
        {
            Assert.DoesNotContain(
                text,
                command.Sql,
                StringComparison.Ordinal);
        }
    }

    private static bool ParameterEquals(object? actual, object expected)
    {
        if (actual is null)
            return false;

        if (expected is int expectedInt)
        {
            return actual switch
            {
                sbyte value => value == expectedInt,
                byte value => value == expectedInt,
                short value => value == expectedInt,
                ushort value => value == expectedInt,
                int value => value == expectedInt,
                uint value => value == expectedInt,
                long value => value == expectedInt,
                ulong value => value <= int.MaxValue && (long)value == expectedInt,
                decimal value => value == expectedInt,
                _ => false
            };
        }

        return Equals(actual, expected);
    }

    [Fact]
    public void GeneratedDmlParityCorpus_MatchesCommonDmlMatrixFloor()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SyntaxCorpus",
            "sql-generated-dml-compatibility-floor.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cases = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(42, cases.Length);
        Assert.Equal(
            cases.Length,
            cases.Select(item => item.GetProperty("name").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());

        var counts = cases
            .GroupBy(
                item => item.GetProperty("dialect").GetString(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key!,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
            Assert.Equal(7, counts[dialect.ToString()]);
    }
}
