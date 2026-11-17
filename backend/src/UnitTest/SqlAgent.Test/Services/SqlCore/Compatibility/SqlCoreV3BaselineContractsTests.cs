using System.Reflection;
using System.Threading;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services.SqlCore.Compatibility;

/// <summary>
/// U0: this is a snapshot of current ParseScript behaviour, not a promise that
/// every dialect supports the SQL or that parsing authorizes execution.
/// No production Rewrite*.fs file is changed to satisfy these assertions.
/// </summary>
public sealed class SqlCoreV3BaselineContractsTests
{
    private static readonly SqlAgentToolType[] Dialects =
    [
        SqlAgentToolType.Postgres, SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer, SqlAgentToolType.Sqlite,
        SqlAgentToolType.Oracle, SqlAgentToolType.Firebird
    ];

    // Eight families x six dialects. Semantic-only families are intentionally
    // Unsupported in ParseScript; AnalyzeScript has a separate, weaker contract.
    public static IEnumerable<object[]> FamilyMatrix()
    {
        var cases = new (SqlScriptStatementKind Kind, string Sql, SqlScriptParseStatus Status)[]
        {
            (SqlScriptStatementKind.Query, "SELECT 1", SqlScriptParseStatus.Parsed),
            (SqlScriptStatementKind.Mutation, "DELETE FROM users WHERE id = 1", SqlScriptParseStatus.Parsed),
            (SqlScriptStatementKind.Schema, "CREATE TABLE users(id INTEGER)", SqlScriptParseStatus.Unsupported),
            (SqlScriptStatementKind.Security, "GRANT SELECT ON users TO analyst", SqlScriptParseStatus.Unsupported),
            (SqlScriptStatementKind.Transaction, "COMMIT", SqlScriptParseStatus.Unsupported),
            (SqlScriptStatementKind.Session, "SET search_path TO public", SqlScriptParseStatus.Unsupported),
            (SqlScriptStatementKind.Utility, "EXPLAIN SELECT 1", SqlScriptParseStatus.Unsupported),
            (SqlScriptStatementKind.Procedural, "CALL run_job()", SqlScriptParseStatus.Unsupported)
        };
        foreach (var dialect in Dialects)
        foreach (var item in cases)
            yield return [dialect, item.Kind, item.Sql, item.Status];
    }

    [Theory]
    [MemberData(nameof(FamilyMatrix))]
    public void EightFamiliesSixDialects_FreezeExistingScriptClassification(
        SqlAgentToolType dialect, SqlScriptStatementKind kind,
        string sql, SqlScriptParseStatus expectedStatus)
    {
        var result = SqlCoreFacade.ParseScript(sql, dialect);
        var statement = Assert.Single(result.Statements);
        Assert.Equal(1, result.BatchCount);
        Assert.Equal(0, statement.BatchIndex);
        Assert.Equal(kind, statement.Kind);
        Assert.Equal(expectedStatus, statement.Status);
        Assert.Equal(0, statement.Span.Start);
        Assert.Equal(sql.Length, statement.Span.Length);
        Assert.Equal(sql, statement.Text);
        if (expectedStatus == SqlScriptParseStatus.Parsed)
        {
            Assert.Null(statement.Diagnostic);
            Assert.Empty(result.Diagnostics);
        }
        else
        {
            Assert.NotNull(statement.Diagnostic);
            Assert.Equal("SQL_SCRIPT_UNSUPPORTED_STATEMENT", statement.Diagnostic.Code);
            Assert.Equal(SqlDiagnosticStage.Parse, statement.Diagnostic.Stage);
            Assert.Equal(SqlDiagnosticCategory.Capability, statement.Diagnostic.Category);
            Assert.Equal(0, statement.Diagnostic.Span!.Start);
            Assert.Equal(sql.Length, statement.Diagnostic.Span.Length);
            Assert.Single(result.Diagnostics);
        }
    }

    [Theory]
    [MemberData(nameof(DialectRows))]
    public void QueryAndMutation_InvalidInputDoesNotBecomeParsed(SqlAgentToolType dialect)
    {
        foreach (var (sql, kind) in new[]
        {
            ("SELECT FROM", SqlScriptStatementKind.Query),
            ("UPDATE users SET WHERE id = 1", SqlScriptStatementKind.Mutation)
        })
        {
            var result = SqlCoreFacade.ParseScript(sql, dialect);
            var statement = Assert.Single(result.Statements);
            Assert.Equal(kind, statement.Kind);
            Assert.Equal(SqlScriptParseStatus.Invalid, statement.Status);
            Assert.NotNull(statement.Diagnostic);
            Assert.Equal(SqlDiagnosticStage.Parse, statement.Diagnostic.Stage);
            Assert.True(statement.Diagnostic.Span!.Start >= 0);
            Assert.True(statement.Diagnostic.Span.End <= sql.Length);
            Assert.Single(result.Diagnostics);
        }

        var unknown = Assert.Single(SqlCoreFacade.ParseScript("MYSTERY 1", dialect).Statements);
        Assert.Equal(SqlScriptStatementKind.Unknown, unknown.Kind);
        Assert.Equal(SqlScriptParseStatus.Unsupported, unknown.Status);
    }

    public static IEnumerable<object[]> DialectRows() =>
        Dialects.Select(dialect => new object[] { dialect });

    [Theory]
    [MemberData(nameof(DialectRows))]
    public void PublicCompilePaths_DoNotTranslateInvalidOrWrongFamilySql(SqlAgentToolType dialect)
    {
        var context = new SqlPlanValidationContext("sqlcore-v3-u0");
        var policy = new SqlExecutionPlanPolicy();
        var query = SqlCoreFacade.TryCompileQuery("SELECT 1", dialect, dialect, context, policy);
        Assert.True(query.Success);
        Assert.NotNull(query.Value);
        Assert.False(string.IsNullOrWhiteSpace(query.Value.PlanFingerprint));

        var mutation = SqlCoreFacade.TryCompileDml(
            "DELETE FROM users WHERE id = 1", dialect, dialect, context);
        Assert.True(mutation.Success);
        Assert.NotNull(mutation.Value);

        var ddlSql = dialect == SqlAgentToolType.MsSqlServer
            ? "CREATE TABLE users(id INT)" : "CREATE TABLE users(id INTEGER)";
        var ddl = SqlCoreFacade.TryCompileDdl(
            ddlSql, new DdlCompilationOptions(dialect, dialect));
        Assert.True(ddl.Success);
        Assert.NotNull(ddl.Value);

        var invalidQuery = SqlCoreFacade.TryCompileQuery(
            "SELECT FROM", dialect, dialect, context, policy);
        Assert.False(invalidQuery.Success);
        Assert.Null(invalidQuery.Value);
        Assert.NotEmpty(invalidQuery.TypedDiagnostics);

        var invalidDml = SqlCoreFacade.TryCompileDml(
            "UPDATE users SET WHERE id = 1", dialect, dialect, context);
        Assert.False(invalidDml.Success);
        Assert.Null(invalidDml.Value);
        Assert.NotEmpty(invalidDml.TypedDiagnostics);

        Assert.False(SqlCoreFacade.TryCompileQuery(
            "DELETE FROM users WHERE id = 1", dialect, dialect, context, policy).Success);
        Assert.False(SqlCoreFacade.TryCompileDml(
            "SELECT 1", dialect, dialect, context).Success);
        Assert.False(SqlCoreFacade.TryCompileDdl(
            "GRANT SELECT ON users TO analyst",
            new DdlCompilationOptions(dialect, dialect)).Success);
    }

    [Fact]
    public void SqlServerGo_PreservesPhysicalBatchIndicesAndExactSourceSpans()
    {
        const string sql = "-- leading\nSELECT 1\nGO /* batch */\n SELECT 2;\nGO -- next\nSELECT 3";
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.MsSqlServer);
        Assert.Equal(3, parsed.BatchCount);
        Assert.Equal(3, parsed.Statements.Count);
        Assert.Equal(new[] { 0, 1, 2 }, parsed.Statements.Select(s => s.BatchIndex));
        foreach (var item in parsed.Statements)
        {
            Assert.Equal(SqlScriptStatementKind.Query, item.Kind);
            Assert.Equal(SqlScriptParseStatus.Parsed, item.Status);
            Assert.Equal(item.Text, sql.Substring(item.Span.Start, item.Span.Length));
        }
        Assert.Empty(parsed.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(DialectRows))]
    public void GoIsNotAnExecutableStatementInOtherDialects(SqlAgentToolType dialect)
    {
        if (dialect == SqlAgentToolType.MsSqlServer) return;
        var result = SqlCoreFacade.ParseScript("SELECT 1\nGO\nSELECT 2", dialect);
        Assert.Equal(1, result.BatchCount);
        Assert.DoesNotContain(result.Statements, s => s.BatchIndex != 0);
        Assert.DoesNotContain(result.Statements, s => s.Status == SqlScriptParseStatus.Parsed);
    }

    [Fact]
    public void PublicFacadeCSharpSurface_DoesNotExposeFSharpSpecificTypes()
    {
        var types = new[]
        {
            typeof(SqlCoreFacade), typeof(SqlScriptParseResult),
            typeof(SqlScriptStatementSyntax), typeof(SqlScriptParseOptions),
            typeof(SqlCoreTryResult<>), typeof(DdlCompilationOptions)
        };
        foreach (var type in types)
        {
            var signatures = type.GetMembers(BindingFlags.Public |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var member in signatures)
            {
                var pieces = member switch
                {
                    MethodInfo method => new[] { method.ReturnType }
                        .Concat(method.GetParameters().Select(p => p.ParameterType)),
                    ConstructorInfo ctor => ctor.GetParameters().Select(p => p.ParameterType),
                    PropertyInfo property => new[] { property.PropertyType },
                    FieldInfo field => new[] { field.FieldType },
                    _ => []
                };
                foreach (var part in pieces)
                    Assert.DoesNotContain("Microsoft.FSharp", part.ToString(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Cancellation_RemainsExceptionalRatherThanAValidationResult()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SqlCoreFacade.TryCompileQuery(
            "SELECT 1", SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("sqlcore-v3-cancel"),
            new SqlExecutionPlanPolicy(), cancelled.Token));
    }
}
