using System.Collections.Immutable;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCaseNativeSafetyTests
{
    [Fact]
    public void Compile_MalformedSearchedCaseWithoutBranches_FailsClosed()
    {
        var select = new SelectStatement(
            Ctes: ImmutableArray<CteDefinition>.Empty,
            Distinct: false,
            Select:
            [
                new SelectItem(
                    new CaseExpr(
                        ImmutableArray<CaseBranch>.Empty,
                        new LiteralExpr("fallback", SourceSpan.Unknown),
                        SourceSpan.Unknown),
                    Alias: null,
                    SourceSpan.Unknown)
            ],
            From: null,
            Joins: ImmutableArray<JoinSource>.Empty,
            Where: null,
            GroupBy: ImmutableArray<SqlExpr>.Empty,
            Having: null,
            OrderBy: ImmutableArray<OrderByItem>.Empty,
            Limit: null,
            Offset: null,
            Span: SourceSpan.Unknown);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                new ParsedStatement(
                    select,
                    SqlAgentToolType.Postgres,
                    EnforceSourceDialectSyntax: false),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("case-native-safety-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("Searched CASE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at least one WHEN", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
