using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DialectDmlGrammarMatrixTests
{
    public static IEnumerable<object[]> SixDialectDmlMatrix() =>
        DmlGrammarMatrixCases.All()
            .Select(item => new object[] { item });

    [Fact]
    public void SixDialectDmlMatrix_HasStableCoverage()
    {
        var cases = DmlGrammarMatrixCases.All().ToArray();

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
        DmlGrammarCase testCase)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            testCase.Sql,
            testCase.Dialect);

        switch (testCase.ExpectedKind)
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
                    $"{testCase.Name}: unsupported DML test kind {testCase.ExpectedKind}.");
        }

        var allowedTables = testCase.AllowedTables
            .Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validationContext = new SqlPlanValidationContext(
            "six-dialect-dml-grammar-matrix-v1",
            allowedTables);

        var command = SqlCoreFacade.CompileDml(
            testCase.Sql,
            testCase.Dialect,
            testCase.Dialect,
            validationContext);

        Assert.Equal(testCase.ExpectedKind, command.Kind);
        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));

        foreach (var fragment in testCase.RenderedFragments.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Assert.Contains(
                fragment,
                command.Sql,
                StringComparison.OrdinalIgnoreCase);
        }

        if (testCase.ExpectedParameter is not null)
        {
            Assert.Contains(
                command.Parameters,
                parameter => ParameterEquals(
                    parameter.Value,
                    testCase.ExpectedParameter));
        }

        if (testCase.ExpectedParameter is string text)
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
}
