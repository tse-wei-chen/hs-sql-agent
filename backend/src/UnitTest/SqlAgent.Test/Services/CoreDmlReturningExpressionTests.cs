using System.Collections.Immutable;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDmlReturningExpressionTests
{
    [Fact]
    public void Compile_PostgresExpressionReturning_LowersBindingFreeTargetRowExpression()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres);
        var expression = new BinaryExpr(
            new ColumnExpr(SqlIdentifier.Unquoted("id", SourceSpan.Unknown), SourceSpan.Unknown),
            "+",
            new ColumnExpr(SqlIdentifier.Unquoted("id", SourceSpan.Unknown), SourceSpan.Unknown),
            SourceSpan.Unknown);
        parsed = WithExpression(parsed, expression, "doubled_id");

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.Contains("RETURNING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("doubled_id", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
        Assert.Equal(1L, Convert.ToInt64(command.Parameters[0].Value));
    }

    [Fact]
    public void Compile_ExpressionReturningToNonPostgresTarget_FailsClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres);
        parsed = WithExpression(
            parsed,
            new ColumnExpr(SqlIdentifier.Unquoted("id", SourceSpan.Unknown), SourceSpan.Unknown),
            "returned_id");
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 35));

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Sqlite,
                new SqlPlanValidationContext("policy-v1"),
                targetProfile: profile));

        Assert.Contains("dml.returning.expression", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PostgreSQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_ExpressionReturningFromNonPostgresSource_FailsClosedBeforeBinding()
    {
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 35));
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Sqlite,
            profile);
        parsed = WithExpression(
            parsed,
            new ColumnExpr(SqlIdentifier.Unquoted("id", SourceSpan.Unknown), SourceSpan.Unknown),
            null);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("dml.returning.expression", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source dialect", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_LiteralBearingExpressionReturning_IsParameterizedInsideNativeDmlFragment()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING id",
            SqlAgentToolType.Postgres);
        var expression = new BinaryExpr(
            new ColumnExpr(SqlIdentifier.Unquoted("id", SourceSpan.Unknown), SourceSpan.Unknown),
            "+",
            new LiteralExpr(2, SourceSpan.Unknown),
            SourceSpan.Unknown);
        parsed = WithExpression(parsed, expression, null);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.DoesNotContain(" + 2", command.Sql, StringComparison.Ordinal);
        Assert.Equal(2, command.Parameters.Length);
        Assert.Equal(1L, Convert.ToInt64(command.Parameters[0].Value));
        Assert.Equal(2L, Convert.ToInt64(command.Parameters[1].Value));
    }

    [Fact]
    public void Compile_DirectPortableScalarFunctionReturning_IsAllowed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING lower(name) AS normalized_name",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.True(command.ReturnsRows);
        Assert.Contains("LOWER", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("normalized_name", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(command.Parameters);
    }

    [Fact]
    public void Compile_AggregateFunctionReturning_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING sum(id)",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("SUM", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_UnknownFunctionReturning_RemainsFailClosed()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM users WHERE id = 1 RETURNING mystery(name)",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(
                parsed,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("mystery", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedStatement WithExpression(
        ParsedStatement parsed,
        SqlExpr expression,
        string? alias)
    {
        IdentifierPart? aliasPart = alias is null
            ? null
            : new IdentifierPart(alias, false, SourceSpan.Unknown);
        var item = new DmlReturningExpressionItem(expression, aliasPart, SourceSpan.Unknown);
        var returning = ImmutableArray.Create<DmlReturningItem>(item);

        switch (parsed.Statement)
        {
            case InsertStatement insert:
                insert.Returning = returning;
                parsed.Statement = insert;
                break;
            case UpdateStatement update:
                update.Returning = returning;
                parsed.Statement = update;
                break;
            case DeleteStatement delete:
                delete.Returning = returning;
                parsed.Statement = delete;
                break;
            default:
                throw new InvalidOperationException("Expected DML statement.");
        }

        return parsed;
    }
}
