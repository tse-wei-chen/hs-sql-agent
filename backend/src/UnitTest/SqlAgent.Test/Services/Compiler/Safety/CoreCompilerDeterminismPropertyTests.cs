using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCompilerDeterminismPropertyTests
{
    private const string PolicyVersion = "compiler-determinism-properties-v1";

    private static readonly SqlAgentToolType[] Providers =
    [
        SqlAgentToolType.Postgres,
        SqlAgentToolType.MySQL,
        SqlAgentToolType.MsSqlServer,
        SqlAgentToolType.Oracle,
        SqlAgentToolType.Sqlite,
        SqlAgentToolType.Firebird
    ];

    [Fact]
    public void GeneratedPortableQueries_AreParseNormalizeCompileDeterministicAcrossSixProviders()
    {
        foreach (var provider in Providers)
        {
            for (var seed = 0; seed < 16; seed++)
            {
                var sql = GenerateQuery(seed);
                var label = $"query provider={provider} seed={seed} sql={sql}";
                var validation = Validation();
                var policy = new SqlExecutionPlanPolicy(100);

                var textFirst = RequireCommand(SqlCoreFacade.CompileQuery(
                    sql,
                    provider,
                    provider,
                    validation,
                    policy), label + " text-first");
                var textSecond = RequireCommand(SqlCoreFacade.CompileQuery(
                    sql,
                    provider,
                    provider,
                    validation,
                    policy), label + " text-second");

                var parsed = CoreSqlTextParser.ParseQuery(sql, provider);
                var parsedFirst = RequireCommand(SqlCoreFacade.CompileQuery(
                    parsed,
                    provider,
                    validation,
                    policy), label + " parsed-first");
                var parsedSecond = RequireCommand(SqlCoreFacade.CompileQuery(
                    parsed,
                    provider,
                    validation,
                    policy), label + " parsed-second");

                var freshParsed = CoreSqlTextParser.ParseQuery(sql, provider);
                var freshParsedCommand = RequireCommand(SqlCoreFacade.CompileQuery(
                    freshParsed,
                    provider,
                    validation,
                    policy), label + " fresh-parsed");

                AssertEquivalent(textFirst, textSecond, label + " text-repeat");
                AssertEquivalent(textFirst, parsedFirst, label + " text-vs-parsed");
                AssertEquivalent(parsedFirst, parsedSecond, label + " same-parsed-repeat");
                AssertEquivalent(parsedFirst, freshParsedCommand, label + " independent-parse");
                AssertParameterized(textFirst, sql, label);
            }
        }
    }

    [Fact]
    public void GeneratedPortableDml_AreParseNormalizeCompileDeterministicAcrossSixProviders()
    {
        foreach (var provider in Providers)
        {
            for (var seed = 0; seed < 8; seed++)
            {
                foreach (var sql in GenerateDml(seed))
                {
                    var label = $"dml provider={provider} seed={seed} sql={sql}";
                    var validation = Validation();

                    var textFirst = RequireCommand(SqlCoreFacade.CompileDml(
                        sql,
                        provider,
                        provider,
                        validation), label + " text-first");
                    var textSecond = RequireCommand(SqlCoreFacade.CompileDml(
                        sql,
                        provider,
                        provider,
                        validation), label + " text-second");

                    var parsed = CoreSqlTextParser.ParseDml(sql, provider);
                    var parsedFirst = RequireCommand(SqlCoreFacade.CompileDml(
                        parsed,
                        provider,
                        validation), label + " parsed-first");
                    var parsedSecond = RequireCommand(SqlCoreFacade.CompileDml(
                        parsed,
                        provider,
                        validation), label + " parsed-second");

                    var freshParsed = CoreSqlTextParser.ParseDml(sql, provider);
                    var freshParsedCommand = RequireCommand(SqlCoreFacade.CompileDml(
                        freshParsed,
                        provider,
                        validation), label + " fresh-parsed");

                    AssertEquivalent(textFirst, textSecond, label + " text-repeat");
                    AssertEquivalent(textFirst, parsedFirst, label + " text-vs-parsed");
                    AssertEquivalent(parsedFirst, parsedSecond, label + " same-parsed-repeat");
                    AssertEquivalent(parsedFirst, freshParsedCommand, label + " independent-parse");
                    AssertParameterized(textFirst, sql, label);
                }
            }
        }
    }

    [Fact]
    public void GeneratedRejectedInputs_HaveDeterministicFailureEvidence()
    {
        foreach (var provider in Providers)
        {
            foreach (var testCase in RejectedCases(provider))
            {
                var label = $"rejected provider={provider} case={testCase.Name} sql={testCase.Sql}";
                var first = testCase.Compile();
                var second = testCase.Compile();

                Assert.False(first.Success, label + " first compile unexpectedly succeeded");
                Assert.False(second.Success, label + " second compile unexpectedly succeeded");
                Assert.Equal(first.ErrorCode, second.ErrorCode);
                Assert.Equal(first.ErrorMessage, second.ErrorMessage);

                var firstEvidence = Assert.IsType<SqlCompileEvidence>(first.CompileEvidence);
                var secondEvidence = Assert.IsType<SqlCompileEvidence>(second.CompileEvidence);

                Assert.Equal(SqlCompileVerdict.Rejected, firstEvidence.Verdict);
                Assert.Equal(firstEvidence.DecisionBoundary, secondEvidence.DecisionBoundary);
                Assert.Equal(firstEvidence.DecisionCode, secondEvidence.DecisionCode);
                Assert.Equal(firstEvidence.EvidenceFingerprint, secondEvidence.EvidenceFingerprint);
                Assert.Null(firstEvidence.PlanFingerprint);
                Assert.Null(secondEvidence.PlanFingerprint);
            }
        }
    }

    private static IEnumerable<RejectedCase> RejectedCases(SqlAgentToolType provider)
    {
        yield return new RejectedCase(
            "parse-grammar",
            "SELECT FROM",
            () => SqlCoreFacade.TryCompileQuery(
                "SELECT FROM",
                provider,
                provider,
                Validation(),
                new SqlExecutionPlanPolicy()));

        yield return new RejectedCase(
            "query-kind-mismatch",
            "UPDATE users SET name = 'Ada' WHERE id = 1",
            () => SqlCoreFacade.TryCompileQuery(
                "UPDATE users SET name = 'Ada' WHERE id = 1",
                provider,
                provider,
                Validation(),
                new SqlExecutionPlanPolicy()));

        yield return new RejectedCase(
            "dml-kind-mismatch",
            "SELECT 1",
            () => SqlCoreFacade.TryCompileDml(
                "SELECT 1",
                provider,
                provider,
                Validation()));
    }

    private static string GenerateQuery(int seed)
    {
        var first = seed + 1;
        var second = first + 7 + (seed % 3);
        var third = first + 2;
        var prefix = $"u{seed % 5}";

        return (seed % 4) switch
        {
            0 => $"SELECT id, name FROM users WHERE id >= {first} AND id < {second} ORDER BY id ASC",
            1 => $"SELECT id + {third} AS score, name FROM users WHERE name LIKE '{prefix}%' OR id = {first} ORDER BY id DESC",
            2 => $"SELECT id, name FROM users WHERE id BETWEEN {first} AND {second} ORDER BY name ASC",
            _ => $"SELECT id, name FROM users WHERE id IN ({first}, {third}, {second}) AND name IS NOT NULL ORDER BY id"
        };
    }

    private static IEnumerable<string> GenerateDml(int seed)
    {
        var id = seed + 1;
        var replacement = $"user_{seed}";

        yield return $"INSERT INTO users (id, name) VALUES ({id}, '{replacement}')";
        yield return $"UPDATE users SET name = '{replacement}' WHERE id = {id}";
        yield return $"DELETE FROM users WHERE id = {id}";
    }

    private static SqlPlanValidationContext Validation() =>
        new(
            PolicyVersion,
            new HashSet<string>(new[] { "users" }, StringComparer.OrdinalIgnoreCase));

    private static CompiledSqlCommand RequireCommand(CompiledSqlCommand? command, string label)
    {
        Assert.NotNull(command);
        return command!;
    }

    private static void AssertEquivalent(
        CompiledSqlCommand expected,
        CompiledSqlCommand actual,
        string label)
    {
        Assert.True(
            string.Equals(expected.Sql, actual.Sql, StringComparison.Ordinal),
            $"{label}: rendered SQL drifted. expected={expected.Sql} actual={actual.Sql}");
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.TargetProvider, actual.TargetProvider);
        Assert.Equal(expected.ReturnsRows, actual.ReturnsRows);
        Assert.Equal(expected.PlanFingerprint, actual.PlanFingerprint);
        Assert.Equal(expected.Parameters.Length, actual.Parameters.Length);

        for (var index = 0; index < expected.Parameters.Length; index++)
        {
            Assert.Equal(expected.Parameters[index].Name, actual.Parameters[index].Name);
            Assert.True(
                Equals(expected.Parameters[index].Value, actual.Parameters[index].Value),
                $"{label}: parameter {index} drifted. expected={expected.Parameters[index].Value} actual={actual.Parameters[index].Value}");
        }

        var expectedEvidence = Assert.IsType<SqlCompileEvidence>(expected.CompileEvidence);
        var actualEvidence = Assert.IsType<SqlCompileEvidence>(actual.CompileEvidence);
        Assert.Equal(SqlCompileVerdict.Translated, expectedEvidence.Verdict);
        Assert.Equal(SqlCompileDecisionBoundary.Completed, expectedEvidence.DecisionBoundary);
        Assert.Equal("SQL_COMPILE_TRANSLATED", expectedEvidence.DecisionCode);
        Assert.Equal(expectedEvidence.EvidenceFingerprint, actualEvidence.EvidenceFingerprint);
        Assert.Equal(expectedEvidence.PlanFingerprint, actualEvidence.PlanFingerprint);
    }

    private static void AssertParameterized(
        CompiledSqlCommand command,
        string sourceSql,
        string label)
    {
        Assert.NotEmpty(command.Parameters);
        foreach (var parameter in command.Parameters)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(parameter.Name),
                label + ": parameter name is empty");
        }

        Assert.DoesNotContain("'user_", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'u0%", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'u1%", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'u2%", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'u3%", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'u4%", command.Sql, StringComparison.Ordinal);

        Assert.False(
            string.Equals(command.Sql, sourceSql, StringComparison.Ordinal),
            label + ": generated literal-bearing SQL was emitted unchanged");
    }

    private sealed record RejectedCase(
        string Name,
        string Sql,
        Func<SqlCoreTryResult<CompiledSqlCommand>> Compile);
}
