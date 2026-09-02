using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreGrammarTokenizerFuzzTests
{
    private static readonly SqlAgentToolType[] Providers =
    [
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.Firebird
    ];

    private static readonly string[] Separators =
    [
        " ",
        "\t",
        "\n",
        " /*hs_fuzz*/ "
    ];

    private static readonly string[] InvalidTokenMutations =
    [
        "SELECT 'unterminated",
        "SELECT /* unterminated",
        "SELECT id,, name FROM users",
        "SELECT id FROM users WHERE id !!= 1",
        "SELECT id FROM users WHERE (((id = 1)",
        "WITH x AS (SELECT 1 SELECT * FROM x",
        "SELECT id FROM users WHERE id = @",
        "SELECT id FROM users WHERE id = 1\u0000"
    ];

    [Fact]
    public void EquivalentTokenizerPerturbations_CompileToOneCanonicalCommandAcrossSixProviders()
    {
        foreach (var provider in Providers)
        {
            var baseline = Compile(
                "SELECT id, name FROM users WHERE id >= 1 AND name LIKE 'A%' ORDER BY id",
                provider);

            var caseId = 0;
            foreach (var variant in EquivalentVariants())
            {
                var actual = Compile(variant, provider);
                var label = $"provider={provider} case={caseId++} sql={Escape(variant)}";

                Assert.Equal(baseline.Sql, actual.Sql);
                Assert.Equal(baseline.Kind, actual.Kind);
                Assert.Equal(baseline.TargetProvider, actual.TargetProvider);
                Assert.Equal(baseline.PlanFingerprint, actual.PlanFingerprint);
                Assert.Equal(ParameterSnapshot(baseline), ParameterSnapshot(actual));

                var expectedEvidence = Assert.IsType<SqlCompileEvidence>(baseline.CompileEvidence);
                var actualEvidence = Assert.IsType<SqlCompileEvidence>(actual.CompileEvidence);
                Assert.Equal(
                    expectedEvidence.EvidenceFingerprint,
                    actualEvidence.EvidenceFingerprint);
                Assert.Equal(
                    "SQL_COMPILE_TRANSLATED",
                    actualEvidence.DecisionCode);
                Assert.Equal(
                    SqlCompileDecisionBoundary.Completed,
                    actualEvidence.DecisionBoundary);

                Assert.DoesNotContain("'A%'", actual.Sql, StringComparison.Ordinal);
                Assert.False(
                    string.Equals(actual.Sql, variant, StringComparison.Ordinal),
                    label + ": tokenizer perturbation escaped canonical compilation.");
            }
        }
    }

    [Fact]
    public void MalformedTokenizerAndGrammarMutations_RejectDeterministicallyAcrossSixProviders()
    {
        foreach (var provider in Providers)
        {
            foreach (var sql in InvalidTokenMutations)
            {
                var first = TryCompile(sql, provider);
                var second = TryCompile(sql, provider);
                var label = $"provider={provider} sql={Escape(sql)}";

                Assert.False(first.Success, label + ": malformed mutation unexpectedly compiled.");
                Assert.False(second.Success, label + ": malformed mutation unexpectedly compiled on repeat.");
                Assert.Equal(first.ErrorCode, second.ErrorCode);
                Assert.Equal(first.ErrorMessage, second.ErrorMessage);

                var firstEvidence = Assert.IsType<SqlCompileEvidence>(first.CompileEvidence);
                var secondEvidence = Assert.IsType<SqlCompileEvidence>(second.CompileEvidence);
                Assert.Equal(SqlCompileVerdict.Rejected, firstEvidence.Verdict);
                Assert.Equal(firstEvidence.DecisionBoundary, secondEvidence.DecisionBoundary);
                Assert.Equal(firstEvidence.DecisionCode, secondEvidence.DecisionCode);
                Assert.Equal(
                    firstEvidence.EvidenceFingerprint,
                    secondEvidence.EvidenceFingerprint);
                Assert.Null(firstEvidence.PlanFingerprint);
                Assert.Null(secondEvidence.PlanFingerprint);
            }
        }
    }

    private static IEnumerable<string> EquivalentVariants()
    {
        foreach (var separator in Separators)
        {
            yield return
                $"SELECT{separator}id,{separator}name{separator}FROM{separator}users{separator}" +
                $"WHERE{separator}id{separator}>={separator}1{separator}AND{separator}" +
                $"name{separator}LIKE{separator}'A%'{separator}ORDER{separator}BY{separator}id";

            yield return
                $"select{separator}id,{separator}name{separator}from{separator}users{separator}" +
                $"where{separator}id{separator}>={separator}1{separator}and{separator}" +
                $"name{separator}like{separator}'A%'{separator}order{separator}by{separator}id";

            yield return
                $"SeLeCt{separator}id,{separator}name{separator}FrOm{separator}users{separator}" +
                $"WhErE{separator}id{separator}>={separator}1{separator}AnD{separator}" +
                $"name{separator}LiKe{separator}'A%'{separator}OrDeR{separator}By{separator}id";
        }

        yield return
            "SELECT /*lead*/ id, /*projection*/ name FROM users " +
            "WHERE id >= 1 /*predicate*/ AND name LIKE 'A%' ORDER BY id";

        yield return
            "SELECT\r\n id,\r\n name\r\n FROM users\r\n " +
            "WHERE id >= 1 AND name LIKE 'A%'\r\n ORDER BY id;";
    }

    private static CompiledSqlCommand Compile(string sql, SqlAgentToolType provider) =>
        SqlCoreFacade.CompileQuery(
            sql,
            provider,
            provider,
            Validation(),
            new SqlExecutionPlanPolicy(100))
        ?? throw new InvalidOperationException(
            $"Tokenizer fuzz compile unexpectedly returned null for {provider}: {Escape(sql)}");

    private static SqlCoreTryResult<CompiledSqlCommand?> TryCompile(
        string sql,
        SqlAgentToolType provider) =>
        SqlCoreFacade.TryCompileQuery(
            sql,
            provider,
            provider,
            Validation(),
            new SqlExecutionPlanPolicy(100));

    private static SqlPlanValidationContext Validation() =>
        new(
            "grammar-tokenizer-fuzz-v1",
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));

    private static (string Name, string Type, string Value)[] ParameterSnapshot(
        CompiledSqlCommand command) =>
        command.Parameters
            .Select(parameter => (
                parameter.Name,
                parameter.Value?.GetType().FullName ?? "<null>",
                parameter.Value?.ToString() ?? "<null>"))
            .ToArray();

    private static string Escape(string value) =>
        value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\0", "\\0", StringComparison.Ordinal);
}
