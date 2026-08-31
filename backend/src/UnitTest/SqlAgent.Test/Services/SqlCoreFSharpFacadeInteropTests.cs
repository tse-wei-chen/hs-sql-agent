using System.Reflection;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreFSharpFacadeInteropTests
{
    [Fact]
    public void Facade_QueryTextPipeline_CompilesParameterizedCommand()
    {
        const string sql = "SELECT id FROM users WHERE id = 1 ORDER BY id";
        var validation = new SqlPlanValidationContext(
            "fsharp-query-boundary-v2",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));

        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            validation,
            new SqlExecutionPlanPolicy(20));

        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" = 1", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 1));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Theory]
    [InlineData("-- audit comment\nSELECT id FROM users WHERE id = 1")]
    [InlineData("/* audit comment */ SELECT id FROM users WHERE id = 1")]
    public void Facade_QueryTextPipeline_DerivesKindFromParsedStatement(string sql)
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-comment-prefix-v2",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));

        var command = SqlCoreFacade.CompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            validation,
            new SqlExecutionPlanPolicy());

        Assert.Equal(SqlStatementKind.Query, command.Kind);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 1));
    }

    [Fact]
    public void Facade_DmlTextPipeline_DerivesKindFromParsedStatementAfterComment()
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-comment-prefix-dml-v2",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));

        var command = SqlCoreFacade.CompileDml(
            "/* audit comment */ UPDATE users SET name = 'Ada' WHERE id = 1",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            validation);

        Assert.Equal(SqlStatementKind.Update, command.Kind);
    }

    [Fact]
    public void Facade_DmlTextPipeline_CompilesParameterizedCommand()
    {
        const string sql = "UPDATE users SET name = 'b' WHERE id = 1";
        var validation = new SqlPlanValidationContext(
            "fsharp-dml-boundary-v2",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));

        var command = SqlCoreFacade.CompileDml(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            validation);

        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'b'", command.Sql, StringComparison.Ordinal);
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "b"));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 1));
        Assert.False(string.IsNullOrWhiteSpace(command.PlanFingerprint));
    }

    [Fact]
    public void Facade_QueryTextPipeline_EnforcesWhitelist()
    {
        var validation = new SqlPlanValidationContext(
            "fsharp-whitelist-v2",
            new HashSet<string>(new[] { "public.users" }, StringComparer.OrdinalIgnoreCase));

        Assert.Throws<UnauthorizedAccessException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT id FROM public.secrets",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                validation,
                new SqlExecutionPlanPolicy()));
    }


    [Fact]
    public void Facade_TryCompileQuery_ReportsGrammarFailuresAsParseErrors()
    {
        var result = SqlCoreFacade.TryCompileQuery(
            "SELECT FROM",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("fsharp-parse-contract-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(result.Success);
        Assert.Equal("SQL_PARSE_ERROR", result.ErrorCode);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_PARSE_GRAMMAR", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Parse, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.True(diagnostic.Span.Length > 0);
    }

    [Fact]
    public void Facade_TryCompileDml_ReportsPolicyDenialAsTypedPolicyDiagnostic()
    {
        var result = SqlCoreFacade.TryCompileDml(
            "UPDATE users SET name = 'Ada'",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("fsharp-policy-diagnostic-v1"));

        Assert.False(result.Success);
        Assert.Equal("SQL_POLICY_DENIED", result.ErrorCode);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_POLICY_UPDATE_REQUIRES_WHERE", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Policy, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Policy, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.Equal(0, diagnostic.Span.Start);
        Assert.True(diagnostic.Span.Length > 0);
    }

    [Fact]
    public void Facade_TryCompileQuery_ReportsBindingFailureAsTypedBindingDiagnostic()
    {
        const string sql = "SELECT missing.id FROM users";
        var result = SqlCoreFacade.TryCompileQuery(
            sql,
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("fsharp-binding-diagnostic-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(result.Success);
        Assert.Equal("SQL_COMPILATION_ERROR", result.ErrorCode);

        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal("SQL_BINDING_ERROR", diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Binding, diagnostic.Category);
        Assert.NotNull(diagnostic.Span);
        Assert.Equal(0, diagnostic.Span.Start);
        Assert.Equal(sql.Length, diagnostic.Span.Length);
    }

    [Fact]
    public void LegacyParsedStatement_CompilationUsesCurrentStatement_NotOriginalRawSql()
    {
        var parsed = CoreSqlTextParser.ParseQuery("SELECT 1", SqlAgentToolType.Postgres);
        var replacement = CoreSqlTextParser.ParseQuery("SELECT 2", SqlAgentToolType.Postgres);
        parsed.Statement = replacement.Statement;

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("fsharp-parsed-statement-contract-v1"),
            new SqlExecutionPlanPolicy());

        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, 2));
        Assert.DoesNotContain(command.Parameters, parameter => Equals(parameter.Value, 1));
    }

    [Fact]
    public void LegacyParsedStatement_InspectionUsesCurrentStatement_NotOriginalRawSql()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM users",
            SqlAgentToolType.Postgres);
        var replacement = CoreSqlTextParser.ParseQuery(
            "SELECT id FROM accounts",
            SqlAgentToolType.Postgres);
        parsed.Statement = replacement.Statement;

        var facts = HsSqlAgent.SqlCore.SqlCoreInspection.GetQueryFacts(parsed);

        Assert.Contains("accounts", facts.ReferencedTables);
        Assert.DoesNotContain("users", facts.ReferencedTables);
    }

    [Fact]
    public void Parser_CompatibilityProjection_PreservesNestedSourceSpans()
    {
        const string sql =
            "WITH recent AS (SELECT id FROM users WHERE id = 7) SELECT recent.id FROM recent";

        var parsed = CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres);
        var select = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.SelectStatement>(parsed.Statement);
        var cte = Assert.Single(select.Ctes);
        var cteSelect = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.SelectStatement>(cte.Query);
        var predicate = Assert.IsType<HsSqlAgent.SqlCore.Core.Ast.BinaryExpr>(cteSelect.Where);

        Assert.Equal(0, select.Span.Start);
        Assert.Equal(sql.Length, select.Span.End);
        Assert.NotEqual(HsSqlAgent.SqlCore.Core.Ast.SourceSpan.Unknown, cte.Span);
        Assert.NotEqual(HsSqlAgent.SqlCore.Core.Ast.SourceSpan.Unknown, cteSelect.Span);
        Assert.NotEqual(HsSqlAgent.SqlCore.Core.Ast.SourceSpan.Unknown, predicate.Span);
        Assert.NotEqual(HsSqlAgent.SqlCore.Core.Ast.SourceSpan.Unknown, predicate.Left.Span);
        Assert.NotEqual(HsSqlAgent.SqlCore.Core.Ast.SourceSpan.Unknown, predicate.Right.Span);
    }

    [Fact]
    public void CompiledCommand_ReturnsRows_IsReadOnly()
    {
        var property = typeof(CompiledSqlCommand).GetProperty(nameof(CompiledSqlCommand.ReturnsRows));

        Assert.NotNull(property);
        Assert.False(property!.CanWrite);
    }

    [Fact]
    public void Facade_PublicApi_DoesNotExposeFSharpImplementationTypes()
    {
        var assembly = typeof(SqlCoreFacade).Assembly;
        foreach (var type in assembly.GetExportedTypes())
        {
            AssertClrFriendly(type.BaseType);

            foreach (var constructor in type.GetConstructors())
            foreach (var parameter in constructor.GetParameters())
                AssertClrFriendly(parameter.ParameterType);

            foreach (var property in type.GetProperties())
                AssertClrFriendly(property.PropertyType);

            foreach (var method in type.GetMethods(
                         BindingFlags.Public |
                         BindingFlags.Instance |
                         BindingFlags.Static |
                         BindingFlags.DeclaredOnly))
            {
                AssertClrFriendly(method.ReturnType);
                foreach (var parameter in method.GetParameters())
                    AssertClrFriendly(parameter.ParameterType);
            }
        }
    }

    private static void AssertClrFriendly(Type? type)
    {
        if (type is null)
            return;

        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            AssertClrFriendly(type.GetElementType());
            return;
        }

        if (type.IsGenericType)
        {
            Assert.DoesNotContain(
                "Microsoft.FSharp",
                type.GetGenericTypeDefinition().FullName ?? type.Name,
                StringComparison.Ordinal);
            foreach (var argument in type.GetGenericArguments())
                AssertClrFriendly(argument);
            return;
        }

        Assert.DoesNotContain(
            "Microsoft.FSharp",
            type.FullName ?? type.Name,
            StringComparison.Ordinal);
    }
}
