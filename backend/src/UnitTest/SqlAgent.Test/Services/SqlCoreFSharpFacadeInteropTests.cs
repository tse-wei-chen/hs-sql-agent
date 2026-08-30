using System.Reflection;
using HsSqlAgent.SqlCore;
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
