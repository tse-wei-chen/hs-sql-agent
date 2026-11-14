using System.Collections.Immutable;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCaseNativeSafetyTests
{
    [Fact]
    public void Compile_MalformedSearchedCaseWithoutBranches_FailsClosed()
    {
        var select = new SelectStatement(
            ImmutableArray<CteDefinition>.Empty,
            false,
            [
                new SelectItem(
                    new CaseExpr(
                        ImmutableArray<CaseBranch>.Empty,
                        new LiteralExpr("fallback", SourceSpan.Unknown),
                        SourceSpan.Unknown),
                    null,
                    SourceSpan.Unknown)
            ],
            null,
            ImmutableArray<JoinSource>.Empty,
            null,
            ImmutableArray<SqlExpr>.Empty,
            null,
            ImmutableArray<OrderByItem>.Empty,
            null,
            null,
            SourceSpan.Unknown);

        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                new ParsedStatement(
                    select,
                    SqlAgentToolType.Postgres,
                    false),
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("case-native-safety-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains("Searched CASE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at least one WHEN", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
