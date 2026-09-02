using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CompilerDeterminismPropertyTests
{
    public sealed record DeterminismCase(
        string Name,
        string Sql,
        SqlAgentToolType Source,
        SqlAgentToolType Target,
        bool IsDml,
        SqlProviderCapabilityProfile? SourceProfile = null,
        SqlProviderCapabilityProfile? TargetProfile = null,
        DmlConflictTargetAssurance? ConflictAssurance = null);

    public sealed record RejectedCase(
        string Name,
        Func<SqlCoreTryResult<CompiledSqlCommand>> Compile);

    public static TheoryData<DeterminismCase> PositiveCorpus => new()
    {
        new(
            "postgres-cte-order-limit",
            "WITH active AS (SELECT id, name FROM users WHERE enabled = TRUE) SELECT id, name FROM active ORDER BY id LIMIT 10",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            false),
        new(
            "mysql-ansi-quotes-profile",
            "SELECT \"name\" FROM users WHERE id = 1",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL,
            false,
            MySqlProfile(),
            MySqlProfile()),
        new(
            "sqlserver-top-order",
            "SELECT TOP (5) id, name FROM users ORDER BY id",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer,
            false),
        new(
            "sqlite-limit-offset",
            "SELECT id FROM users ORDER BY id LIMIT 5 OFFSET 2",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            false),
        new(
            "oracle-offset-fetch",
            "SELECT id FROM users ORDER BY id OFFSET 2 ROWS FETCH NEXT 5 ROWS ONLY",
            SqlAgentToolType.Oracle,
            SqlAgentToolType.Oracle,
            false),
        new(
            "firebird-first-skip",
            "SELECT FIRST 5 SKIP 2 id FROM users ORDER BY id",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Firebird,
            false),
        new(
            "postgres-concat-to-sqlserver-profile",
            "SELECT first_name || last_name FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MsSqlServer,
            false,
            new SqlProviderCapabilityProfile(SqlAgentToolType.Postgres),
            SqlServerConcatProfile()),
        new(
            "postgres-rich-conflict-update",
            "INSERT INTO inventory (id, quantity) VALUES (1, 3) ON CONFLICT (id) DO UPDATE SET quantity = quantity + excluded.quantity * 2",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            true),
        new(
            "sqlite-rich-conflict-update-profile",
            "INSERT INTO inventory (id, quantity) VALUES (1, 3) ON CONFLICT (id) DO UPDATE SET quantity = quantity + excluded.quantity * 2",
            SqlAgentToolType.Sqlite,
            SqlAgentToolType.Sqlite,
            true,
            Sqlite324(),
            Sqlite324()),
        new(
            "sqlserver-single-row-merge-assured",
            "MERGE INTO inventory AS t USING (VALUES (1, 3)) AS s (id, quantity) ON t.id = s.id WHEN MATCHED THEN UPDATE SET quantity = s.quantity WHEN NOT MATCHED THEN INSERT (id, quantity) VALUES (s.id, s.quantity);",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer,
            true,
            null,
            null,
            DmlConflictTargetAssurance.FromPrimaryKey(["id"])),
        new(
            "firebird-update-or-insert-to-postgres",
            "UPDATE OR INSERT INTO users (id, name) VALUES (1, 'Alice') MATCHING (id)",
            SqlAgentToolType.Firebird,
            SqlAgentToolType.Postgres,
            true),
        new(
            "postgres-upsert-to-mysql-assured",
            "INSERT INTO users (id, name) VALUES (1, 'Alice') ON CONFLICT (id) DO UPDATE SET name = excluded.name",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.MySQL,
            true,
            null,
            MySql819(),
            DmlConflictTargetAssurance.FromUniqueKey(
                ["id"],
                "PRIMARY",
                isPrimaryKey: true,
                enforcedUniqueKeyCount: 1,
                hasUnsupportedEnforcedUniqueKeys: false)),
        new(
            "postgres-update-returning",
            "UPDATE users SET name = 'Ada' WHERE id = 1 RETURNING id, name",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            true)
    };

    public static TheoryData<RejectedCase> NegativeCorpus => new()
    {
        new(
            "parse-rejection",
            () => SqlCoreFacade.TryCompileQuery(
                "SELECT FROM",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("determinism-negative-parse-v1"),
                new SqlExecutionPlanPolicy())),
        new(
            "target-capability-rejection",
            () => SqlCoreFacade.TryCompileQuery(
                "SELECT name FROM users WHERE name ILIKE 'a%'",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("determinism-negative-target-v1"),
                new SqlExecutionPlanPolicy())),
        new(
            "policy-rejection",
            () => SqlCoreFacade.TryCompileDml(
                "UPDATE users SET name = 'Ada'",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("determinism-negative-policy-v1")))
    };

    [Theory]
    [MemberData(nameof(PositiveCorpus))]
    public void AcceptedCorpus_ParseNormalizeAndCompile_AreDeterministicAndNormalizationIsIdempotent(
        DeterminismCase item)
    {
        const int repetitions = 4;

        var inspections = Enumerable.Range(0, repetitions)
            .Select(_ => Inspect(item))
            .ToArray();

        var firstInspection = inspections[0];
        Assert.True(
            firstInspection.NormalizationIdempotent,
            $"{item.Name}: canonical normalization must be idempotent.");
        Assert.Equal(
            firstInspection.CanonicalFingerprint,
            firstInspection.RenormalizedCanonicalFingerprint);

        foreach (var inspection in inspections.Skip(1))
        {
            Assert.Equal(firstInspection.ParseFingerprint, inspection.ParseFingerprint);
            Assert.Equal(firstInspection.CanonicalFingerprint, inspection.CanonicalFingerprint);
            Assert.Equal(
                firstInspection.RenormalizedCanonicalFingerprint,
                inspection.RenormalizedCanonicalFingerprint);
            Assert.Equal(
                firstInspection.NormalizationIdempotent,
                inspection.NormalizationIdempotent);
        }

        var commands = Enumerable.Range(0, repetitions)
            .Select(_ => Compile(item))
            .ToArray();
        var first = commands[0];

        Assert.NotNull(first.CompileEvidence);
        Assert.Equal(SqlCompileVerdict.Translated, first.CompileEvidence!.Verdict);
        Assert.Equal(SqlCompileDecisionBoundary.Completed, first.CompileEvidence.DecisionBoundary);

        foreach (var command in commands.Skip(1))
        {
            Assert.Equal(first.Kind, command.Kind);
            Assert.Equal(first.TargetProvider, command.TargetProvider);
            Assert.Equal(first.ReturnsRows, command.ReturnsRows);
            Assert.Equal(first.Sql, command.Sql);
            Assert.Equal(first.PlanFingerprint, command.PlanFingerprint);
            Assert.Equal(
                first.CompileEvidence.EvidenceFingerprint,
                command.CompileEvidence!.EvidenceFingerprint);
            Assert.Equal(
                first.CompileEvidence.DecisionCode,
                command.CompileEvidence.DecisionCode);

            var expectedParameters = first.Parameters
                .Select(ParameterSnapshot)
                .ToArray();
            var actualParameters = command.Parameters
                .Select(ParameterSnapshot)
                .ToArray();
            Assert.Equal(expectedParameters, actualParameters);
        }
    }

    [Theory]
    [MemberData(nameof(NegativeCorpus))]
    public void RejectedCorpus_RepeatedCompilationKeepsDecisionEvidenceDeterministic(
        RejectedCase item)
    {
        var results = Enumerable.Range(0, 4)
            .Select(_ => item.Compile())
            .ToArray();

        var first = results[0];
        Assert.False(first.Success);
        var firstEvidence = Assert.IsType<SqlCompileEvidence>(first.CompileEvidence);
        Assert.Equal(SqlCompileVerdict.Rejected, firstEvidence.Verdict);

        foreach (var result in results.Skip(1))
        {
            Assert.False(result.Success);
            Assert.Equal(first.ErrorCode, result.ErrorCode);

            var evidence = Assert.IsType<SqlCompileEvidence>(result.CompileEvidence);
            Assert.Equal(firstEvidence.DecisionBoundary, evidence.DecisionBoundary);
            Assert.Equal(firstEvidence.DecisionCode, evidence.DecisionCode);
            Assert.Equal(firstEvidence.EvidenceFingerprint, evidence.EvidenceFingerprint);
        }
    }

    private static SqlDeterminismInspectionFacts Inspect(DeterminismCase item) =>
        SqlCoreInspection.GetDeterminismFacts(
            item.Sql,
            item.Source,
            item.Target,
            item.SourceProfile!,
            item.TargetProfile!);

    private static CompiledSqlCommand Compile(DeterminismCase item)
    {
        var validation = new SqlPlanValidationContext(
            "compiler-determinism-properties-v1");

        if (!item.IsDml)
        {
            if (item.SourceProfile is not null)
            {
                return SqlCoreFacade.CompileQuery(
                    item.Sql,
                    item.Source,
                    item.Target,
                    validation,
                    new SqlExecutionPlanPolicy(100),
                    item.SourceProfile,
                    item.TargetProfile!);
            }

            if (item.TargetProfile is not null)
            {
                return SqlCoreFacade.CompileQuery(
                    item.Sql,
                    item.Source,
                    item.Target,
                    validation,
                    new SqlExecutionPlanPolicy(100),
                    item.TargetProfile);
            }

            return SqlCoreFacade.CompileQuery(
                item.Sql,
                item.Source,
                item.Target,
                validation,
                new SqlExecutionPlanPolicy(100));
        }

        var parsed = CoreSqlTextParser.ParseDml(
            item.Sql,
            item.Source,
            item.SourceProfile);

        return SqlCoreFacade.CompileDml(
            parsed,
            item.Target,
            validation,
            new DmlCompilationPolicy(),
            item.TargetProfile,
            item.ConflictAssurance);
    }

    private static (string Name, string Type, string Value) ParameterSnapshot(
        SqlParameterValue parameter)
    {
        var value = parameter.Value;
        return (
            parameter.Name,
            value?.GetType().FullName ?? "<null>",
            value?.ToString() ?? "<null>");
    }

    private static SqlProviderCapabilityProfile MySqlProfile()
    {
        var modes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ANSI_QUOTES",
            "PIPES_AS_CONCAT"
        };

        return new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            new Version(8, 4),
            modes,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private static SqlProviderCapabilityProfile MySql819() =>
        new(
            SqlAgentToolType.MySQL,
            ServerVersion: new Version(8, 0, 19));

    private static SqlProviderCapabilityProfile Sqlite324() =>
        new(
            SqlAgentToolType.Sqlite,
            ServerVersion: new Version(3, 24));

    private static SqlProviderCapabilityProfile SqlServerConcatProfile()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CONCAT_NULL_YIELDS_NULL"] = "ON"
        };

        return new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer,
            new Version(13, 0),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            settings);
    }
}
