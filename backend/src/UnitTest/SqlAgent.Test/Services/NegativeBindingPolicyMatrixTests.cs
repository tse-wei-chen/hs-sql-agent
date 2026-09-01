using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class NegativeBindingPolicyMatrixTests
{
    private sealed record BindingMutationShape(
        string Name,
        string BaselineSql,
        string MutatedSql);

    private sealed record DmlPolicyMutationShape(
        string Name,
        string BaselineSql,
        string MutatedSql,
        string DiagnosticCode,
        string MessageFragment);

    private sealed record QueryPolicyShape(
        string Name,
        string Sql);

    private static readonly GrammarVariant<SqlAgentToolType>[] Dialects =
    [
        new("postgres", SqlAgentToolType.Postgres),
        new("mysql", SqlAgentToolType.MySQL),
        new("sqlserver", SqlAgentToolType.MsSqlServer),
        new("sqlite", SqlAgentToolType.Sqlite),
        new("oracle", SqlAgentToolType.Oracle),
        new("firebird", SqlAgentToolType.Firebird)
    ];

    private static readonly GrammarVariant<BindingMutationShape>[] BindingShapes =
    [
        new(
            "projection",
            new(
                "projection",
                "SELECT users.id FROM users",
                "SELECT missing.id FROM users")),
        new(
            "predicate",
            new(
                "predicate",
                "SELECT id FROM users WHERE users.id = 1",
                "SELECT id FROM users WHERE missing.id = 1")),
        new(
            "join-on",
            new(
                "join-on",
                "SELECT users.id FROM users JOIN orders ON users.id = orders.user_id",
                "SELECT users.id FROM users JOIN orders ON missing.id = orders.user_id")),
        new(
            "cte-root",
            new(
                "cte-root",
                "WITH x AS (SELECT id FROM users) SELECT x.id FROM x",
                "WITH x AS (SELECT id FROM users) SELECT missing.id FROM x")),
        new(
            "correlated-subquery",
            new(
                "correlated-subquery",
                "SELECT u.id FROM users u WHERE EXISTS (SELECT id FROM orders o WHERE o.user_id = u.id)",
                "SELECT u.id FROM users u WHERE EXISTS (SELECT id FROM orders o WHERE o.user_id = missing.id)"))
    ];

    private static readonly GrammarVariant<DmlPolicyMutationShape>[] DmlPolicyShapes =
    [
        new(
            "update-without-where",
            new(
                "update-without-where",
                "UPDATE users SET name = 'x' WHERE id = 1",
                "UPDATE users SET name = 'x'",
                "SQL_POLICY_UPDATE_REQUIRES_WHERE",
                "UPDATE without WHERE")),
        new(
            "delete-without-where",
            new(
                "delete-without-where",
                "DELETE FROM users WHERE id = 1",
                "DELETE FROM users",
                "SQL_POLICY_DELETE_REQUIRES_WHERE",
                "DELETE without WHERE"))
    ];

    private static readonly GrammarVariant<QueryPolicyShape>[] QueryMaxRowsShapes =
    [
        new(
            "root-fetch-with-ties",
            new(
                "root-fetch-with-ties",
                "SELECT id FROM users ORDER BY id FETCH FIRST 10 ROWS WITH TIES")),
        new(
            "cte-root-fetch-with-ties",
            new(
                "cte-root-fetch-with-ties",
                "WITH x AS (SELECT id FROM users) SELECT id FROM x ORDER BY id FETCH FIRST 10 ROWS WITH TIES"))
    ];

    public static IEnumerable<object[]> BindingMutationMatrix()
    {
        foreach (var (dialect, shape) in
                 SyntaxGrammarMatrix.Product(Dialects, BindingShapes))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "binding",
                    dialect.Name,
                    shape.Name),
                dialect.Value,
                shape.Value.BaselineSql,
                shape.Value.MutatedSql
            ];
        }
    }

    public static IEnumerable<object[]> DmlPolicyMutationMatrix()
    {
        foreach (var (dialect, shape) in
                 SyntaxGrammarMatrix.Product(Dialects, DmlPolicyShapes))
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "policy",
                    dialect.Name,
                    shape.Name),
                dialect.Value,
                shape.Value.BaselineSql,
                shape.Value.MutatedSql,
                shape.Value.DiagnosticCode,
                shape.Value.MessageFragment
            ];
        }
    }

    public static IEnumerable<object[]> QueryMaxRowsPolicyMatrix()
    {
        foreach (var shape in QueryMaxRowsShapes)
        {
            yield return
            [
                SyntaxGrammarMatrix.CaseName(
                    "policy",
                    "postgres",
                    shape.Name),
                shape.Value.Sql
            ];
        }
    }

    [Fact]
    public void BindingMutationMatrix_IsCartesianAndCollisionFree()
    {
        var cases = BindingMutationMatrix().ToArray();
        var expectedCount = Dialects.Length * BindingShapes.Length;

        Assert.Equal(30, expectedCount);
        Assert.Equal(expectedCount, cases.Length);
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void DmlPolicyMutationMatrix_IsCartesianAndCollisionFree()
    {
        var cases = DmlPolicyMutationMatrix().ToArray();
        var expectedCount = Dialects.Length * DmlPolicyShapes.Length;

        Assert.Equal(12, expectedCount);
        Assert.Equal(expectedCount, cases.Length);
        Assert.Equal(
            expectedCount,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void QueryMaxRowsPolicyMatrix_IsCollisionFree()
    {
        var cases = QueryMaxRowsPolicyMatrix().ToArray();

        Assert.Equal(2, cases.Length);
        Assert.Equal(
            cases.Length,
            cases.Select(item => Assert.IsType<string>(item[0]))
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Theory]
    [MemberData(nameof(BindingMutationMatrix))]
    public void BindingMutationMatrix_BaselineCompilesButUnknownQualifierFailsAtBinding(
        string name,
        SqlAgentToolType dialect,
        string baselineSql,
        string mutatedSql)
    {
        var baseline = CompileQuery(
            baselineSql,
            dialect,
            new SqlExecutionPlanPolicy());

        Assert.False(string.IsNullOrWhiteSpace(baseline.Sql), name);

        var error = Record.Exception(
            () => CompileQuery(
                mutatedSql,
                dialect,
                new SqlExecutionPlanPolicy()));

        Assert.NotNull(error);
        Assert.Equal(typeof(InvalidOperationException), error.GetType());
        Assert.Contains(
            "unknown table/alias qualifier",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        AssertTypedDiagnostic(
            name,
            error,
            mutatedSql,
            "SQL_BINDING_ERROR",
            SqlDiagnosticStage.Binding,
            SqlDiagnosticCategory.Binding);
    }

    [Theory]
    [MemberData(nameof(DmlPolicyMutationMatrix))]
    public void DmlPolicyMutationMatrix_PredicateBaselineCompilesButUnboundedMutationIsDenied(
        string name,
        SqlAgentToolType dialect,
        string baselineSql,
        string mutatedSql,
        string expectedDiagnosticCode,
        string expectedMessageFragment)
    {
        var baseline = CompileDml(
            baselineSql,
            dialect,
            new DmlCompilationPolicy());

        Assert.False(string.IsNullOrWhiteSpace(baseline.Sql), name);

        var error = Record.Exception(
            () => CompileDml(
                mutatedSql,
                dialect,
                new DmlCompilationPolicy()));

        Assert.NotNull(error);
        Assert.Equal(typeof(UnauthorizedAccessException), error.GetType());
        Assert.Contains(
            expectedMessageFragment,
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        AssertTypedDiagnostic(
            name,
            error,
            mutatedSql,
            expectedDiagnosticCode,
            SqlDiagnosticStage.Policy,
            SqlDiagnosticCategory.Policy);
    }

    [Theory]
    [MemberData(nameof(QueryMaxRowsPolicyMatrix))]
    public void QueryMaxRowsPolicyMatrix_UnlimitedBaselineCompilesButHardCapRejectsUnprovableTies(
        string name,
        string sql)
    {
        var baseline = CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            new SqlExecutionPlanPolicy());

        Assert.False(string.IsNullOrWhiteSpace(baseline.Sql), name);

        var error = Record.Exception(
            () => CompileQuery(
                sql,
                SqlAgentToolType.Postgres,
                new SqlExecutionPlanPolicy(5)));

        Assert.NotNull(error);
        Assert.Equal(typeof(UnauthorizedAccessException), error.GetType());
        Assert.Contains(
            "QueryMaxRows",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "WITH TIES",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        AssertTypedDiagnostic(
            name,
            error,
            sql,
            "SQL_POLICY_QUERY_MAX_ROWS_UNPROVABLE",
            SqlDiagnosticStage.Policy,
            SqlDiagnosticCategory.Policy);
    }

    private static void AssertTypedDiagnostic(
        string name,
        Exception error,
        string sql,
        string expectedCode,
        SqlDiagnosticStage expectedStage,
        SqlDiagnosticCategory expectedCategory)
    {
        var diagnostic = SyntaxGrammarMatrix.RequireTypedDiagnostic(error);

        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(expectedStage, diagnostic.Stage);
        Assert.Equal(expectedCategory, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Start >= 0, name);
        Assert.True(diagnostic.Span.Length >= 0, name);
        Assert.True(diagnostic.Span.End <= sql.Length, name);
    }

    private static CompiledSqlCommand CompileQuery(
        string sql,
        SqlAgentToolType dialect,
        SqlExecutionPlanPolicy policy) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                sql,
                dialect),
            dialect,
            new SqlPlanValidationContext(
                "negative-binding-policy-matrix-v1"),
            policy);

    private static CompiledSqlCommand CompileDml(
        string sql,
        SqlAgentToolType dialect,
        DmlCompilationPolicy policy) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(
                sql,
                dialect),
            dialect,
            new SqlPlanValidationContext(
                "negative-binding-policy-matrix-v1"),
            policy);
}
