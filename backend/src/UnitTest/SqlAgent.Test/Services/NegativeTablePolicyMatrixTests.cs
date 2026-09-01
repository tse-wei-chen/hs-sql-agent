using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeTablePolicyMatrixTests
{
    private sealed record TablePolicyShape(
        string Name,
        string Sql,
        string[] BaselineAllowedTables,
        string[] RestrictedAllowedTables,
        string ForbiddenTable);

    private static readonly GrammarVariant<SqlAgentToolType>[] Dialects =
    [
        new("postgres", SqlAgentToolType.Postgres),
        new("mysql", SqlAgentToolType.MySQL),
        new("sqlserver", SqlAgentToolType.MsSqlServer),
        new("sqlite", SqlAgentToolType.Sqlite),
        new("oracle", SqlAgentToolType.Oracle),
        new("firebird", SqlAgentToolType.Firebird)
    ];

    private static readonly GrammarVariant<TablePolicyShape>[] Shapes =
    [
        new(
            "root-table",
            new(
                "root-table",
                "SELECT id FROM users",
                ["users"],
                ["orders"],
                "users")),
        new(
            "join-table",
            new(
                "join-table",
                "SELECT users.id FROM users JOIN orders ON users.id = orders.user_id",
                ["users", "orders"],
                ["users"],
                "orders")),
        new(
            "cte-physical-table",
            new(
                "cte-physical-table",
                "WITH x AS (SELECT id FROM orders) SELECT id FROM x",
                ["orders"],
                ["users"],
                "orders")),
        new(
            "subquery-table",
            new(
                "subquery-table",
                "SELECT id FROM users WHERE EXISTS (SELECT id FROM orders WHERE orders.user_id = users.id)",
                ["users", "orders"],
                ["users"],
                "orders"))
    ];

    public static IEnumerable<object[]> TablePolicyMutationMatrix()
    {
        foreach (var (dialect, shape) in
                 SyntaxGrammarMatrix.Product(Dialects, Shapes))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "policy-table",
                    dialect.Name,
                    shape.Name),
                dialect.Value,
                shape.Value.Sql,
                shape.Value.BaselineAllowedTables,
                shape.Value.RestrictedAllowedTables,
                shape.Value.ForbiddenTable
            ];
        }
    }

    [Fact]
    public void TablePolicyMutationMatrix_IsCartesianAndCollisionFree()
    {
        var cases = TablePolicyMutationMatrix().ToArray();
        var expectedCount = Dialects.Length * Shapes.Length;

        Assert.Equal(24, expectedCount);
        Assert.Equal(expectedCount, cases.Length);
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(TablePolicyMutationMatrix))]
    public void TablePolicyMutationMatrix_BaselineAllowsAllPhysicalTablesButRestrictedPolicyFailsClosed(
        string name,
        SqlAgentToolType dialect,
        string sql,
        string[] baselineAllowedTables,
        string[] restrictedAllowedTables,
        string forbiddenTable)
    {
        var baseline = Compile(
            sql,
            dialect,
            baselineAllowedTables);

        Assert.False(string.IsNullOrWhiteSpace(baseline.Sql), name);

        var error = Record.Exception(
            () => Compile(
                sql,
                dialect,
                restrictedAllowedTables));

        Assert.NotNull(error);
        Assert.Equal(typeof(UnauthorizedAccessException), error.GetType());
        Assert.Contains(
            forbiddenTable,
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);

        Assert.Equal("SQL_POLICY_TABLE_NOT_ALLOWED", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Policy, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
        Assert.True(diagnostic.Span.Length > 0, name);
        Assert.True(diagnostic.Span.End <= sql.Length, name);

        var actualSpanText = sql.Substring(
            diagnostic.Span.Start,
            diagnostic.Span.Length);

        Assert.Equal(
            forbiddenTable,
            actualSpanText,
            ignoreCase: true);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType dialect,
        IEnumerable<string> allowedTables)
    {
        var allowed = new HashSet<string>(
            allowedTables,
            StringComparer.OrdinalIgnoreCase);

        return CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                sql,
                dialect),
            dialect,
            new SqlPlanValidationContext(
                "negative-table-policy-matrix-v1",
                allowed),
            new SqlExecutionPlanPolicy());
    }
}
