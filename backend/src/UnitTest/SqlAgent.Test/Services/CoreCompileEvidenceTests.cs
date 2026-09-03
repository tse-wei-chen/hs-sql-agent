using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreCompileEvidenceTests
{
    private const string MergeSql =
        "MERGE INTO inventory AS t " +
        "USING (VALUES (1, 3)) AS s (id, quantity) " +
        "ON t.id = s.id " +
        "WHEN MATCHED THEN UPDATE SET quantity = s.quantity " +
        "WHEN NOT MATCHED THEN INSERT (id, quantity) VALUES (s.id, s.quantity);";

    [Fact]
    public void SuccessfulCompile_RecordsSortedProfilesCapabilitiesPolicyAndStableEvidenceFingerprint()
    {
        var firstProfile = MySqlProfile(reverseInsertion: false);
        var secondProfile = MySqlProfile(reverseInsertion: true);
        var validation = new SqlPlanValidationContext(
            "compile-evidence-v1",
            new HashSet<string>(new[] { "users", "accounts" }, StringComparer.OrdinalIgnoreCase));

        var first = SqlCoreFacade.CompileQuery(
            "SELECT id FROM users WHERE id = 1",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL,
            validation,
            new SqlExecutionPlanPolicy(25),
            firstProfile,
            firstProfile);

        var second = SqlCoreFacade.CompileQuery(
            "SELECT id FROM users WHERE id = 999",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL,
            validation,
            new SqlExecutionPlanPolicy(25),
            secondProfile,
            secondProfile);

        var evidence = Assert.IsType<SqlCompileEvidence>(first.CompileEvidence);
        var secondEvidence = Assert.IsType<SqlCompileEvidence>(second.CompileEvidence);

        Assert.Equal("2026-09-02.1", evidence.SchemaVersion);
        Assert.Equal(SqlCapabilityMatrix.Version, evidence.CapabilityMatrixVersion);
        Assert.Equal(SqlCompileVerdict.Translated, evidence.Verdict);
        Assert.Equal(SqlCompileDecisionBoundary.Completed, evidence.DecisionBoundary);
        Assert.Equal("SQL_COMPILE_TRANSLATED", evidence.DecisionCode);
        Assert.Equal(SqlAgentToolType.MySQL, evidence.SourceProfile.Provider);
        Assert.Equal(SqlAgentToolType.MySQL, evidence.TargetProfile.Provider);
        Assert.Equal("8.4", evidence.SourceProfile.ServerVersion);
        Assert.Equal(["ANSI_QUOTES", "PIPES_AS_CONCAT"], evidence.SourceProfile.SessionModes);
        Assert.Empty(evidence.SourceProfile.SessionSettings);
        Assert.Equal(["accounts", "users"], evidence.Policy.AllowedTables);
        Assert.Equal(25, evidence.Policy.QueryMaxRows);
        Assert.Equal("compile-evidence-v1", evidence.Policy.PolicyVersion);
        Assert.NotEmpty(evidence.SourceCapabilities);
        Assert.NotEmpty(evidence.TargetCapabilities);
        Assert.Equal(
            evidence.SourceCapabilities.Select(item => item.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            evidence.SourceCapabilities.Select(item => item.Id));
        Assert.Equal(first.PlanFingerprint, evidence.PlanFingerprint);
        Assert.Equal(LegacyEvidenceFingerprint(evidence), evidence.EvidenceFingerprint);

        // Runtime literal values intentionally remain part of PlanFingerprint, but not the
        // compile-context EvidenceFingerprint.
        Assert.NotEqual(first.PlanFingerprint, second.PlanFingerprint);
        Assert.Equal(evidence.EvidenceFingerprint, secondEvidence.EvidenceFingerprint);
    }

    [Fact]
    public void EvidenceFingerprint_PreservesLegacyUtf8Serialization()
    {
        var command = SqlCoreFacade.CompileQuery(
            "SELECT 1",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("compile-evidence-策略-v1"),
            new SqlExecutionPlanPolicy());

        var evidence = Assert.IsType<SqlCompileEvidence>(command.CompileEvidence);

        Assert.Equal(LegacyEvidenceFingerprint(evidence), evidence.EvidenceFingerprint);
    }

    [Fact]
    public void SqlServerProfile_RecordsOnlyCapabilityRelevantSessionSetting()
    {
        var modes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CONCAT_NULL_YIELDS_NULL"] = "ON",
            ["password"] = "must-not-enter-compile-evidence",
            ["application_name"] = "irrelevant"
        };
        var profile = new SqlProviderCapabilityProfile(
            SqlAgentToolType.MsSqlServer,
            new Version(13, 0),
            modes,
            settings);

        var command = SqlCoreFacade.CompileQuery(
            "SELECT 1",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("compile-evidence-sqlserver-setting-v1"),
            new SqlExecutionPlanPolicy(),
            profile,
            profile);

        var evidence = Assert.IsType<SqlCompileEvidence>(command.CompileEvidence);
        var setting = Assert.Single(evidence.TargetProfile.SessionSettings);
        Assert.Equal("CONCAT_NULL_YIELDS_NULL", setting.Name);
        Assert.Equal("ON", setting.Value);
        Assert.DoesNotContain(
            evidence.TargetProfile.SessionSettings,
            item => item.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MergeCompile_RecordsExplicitConflictTargetAssurance()
    {
        var parsed = CoreSqlTextParser.ParseDml(MergeSql, SqlAgentToolType.MsSqlServer);
        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            new SqlPlanValidationContext("compile-evidence-merge-v1"),
            conflictTargetAssurance: DmlConflictTargetAssurance.FromPrimaryKey(["id"]));

        var evidence = Assert.IsType<SqlCompileEvidence>(command.CompileEvidence);
        var assurance = Assert.Single(
            evidence.Assurances,
            item => item.Kind == "dml.conflict_target");

        Assert.Equal("id", Detail(assurance, "primaryKeyColumns"));
        Assert.Equal(string.Empty, Detail(assurance, "matchedUniqueKeyColumns"));
        Assert.Equal(SqlCompileVerdict.Translated, evidence.Verdict);
        Assert.Equal(SqlCompileDecisionBoundary.Completed, evidence.DecisionBoundary);
    }

    [Fact]
    public void SqlServerOutputCompile_RecordsResultRowAssurance()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE users SET name = 'Ada' OUTPUT INSERTED.id WHERE id = 1",
            SqlAgentToolType.MsSqlServer);
        var validation = new SqlPlanValidationContext("compile-evidence-output-v1")
            .WithDmlResultRowAssurance(
                DmlResultRowAssurance.NoEnabledTriggers("users", DmlOperation.Update));

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.MsSqlServer,
            validation);

        var evidence = Assert.IsType<SqlCompileEvidence>(command.CompileEvidence);
        var assurance = Assert.Single(
            evidence.Assurances,
            item => item.Kind == "dml.result_rows");

        Assert.Equal("users", Detail(assurance, "targetTable"));
        Assert.Equal("Update", Detail(assurance, "operation"));
    }

    [Fact]
    public void TryCompile_TargetCapabilityFailure_RecordsRejectedBoundaryDeterministically()
    {
        static SqlCoreTryResult<CompiledSqlCommand> Compile() =>
            SqlCoreFacade.TryCompileQuery(
                "SELECT name FROM users WHERE name ILIKE 'a%'",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("compile-evidence-target-rejection-v1"),
                new SqlExecutionPlanPolicy());

        var first = Compile();
        var second = Compile();

        Assert.False(first.Success);
        var evidence = Assert.IsType<SqlCompileEvidence>(first.CompileEvidence);
        var secondEvidence = Assert.IsType<SqlCompileEvidence>(second.CompileEvidence);

        Assert.Equal(SqlCompileVerdict.Rejected, evidence.Verdict);
        Assert.Equal(SqlCompileDecisionBoundary.TargetCapability, evidence.DecisionBoundary);
        Assert.False(string.IsNullOrWhiteSpace(evidence.DecisionCode));
        Assert.Null(evidence.PlanFingerprint);
        Assert.Equal(evidence.EvidenceFingerprint, secondEvidence.EvidenceFingerprint);
        Assert.Equal(LegacyEvidenceFingerprint(evidence), evidence.EvidenceFingerprint);
    }

    [Fact]
    public void TryCompile_PolicyFailure_RecordsPolicyBoundary()
    {
        var result = SqlCoreFacade.TryCompileDml(
            "UPDATE users SET name = 'Ada'",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("compile-evidence-policy-v1"));

        Assert.False(result.Success);
        var evidence = Assert.IsType<SqlCompileEvidence>(result.CompileEvidence);
        Assert.Equal(SqlCompileVerdict.Rejected, evidence.Verdict);
        Assert.Equal(SqlCompileDecisionBoundary.Policy, evidence.DecisionBoundary);
    }

    [Fact]
    public void DirectCompileFailure_AttachesEvidenceToException()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT name FROM users WHERE name ILIKE 'a%'",
                SqlAgentToolType.Postgres,
                SqlAgentToolType.MySQL,
                new SqlPlanValidationContext("compile-evidence-direct-failure-v1"),
                new SqlExecutionPlanPolicy()));

        var evidence = Assert.IsType<SqlCompileEvidence>(error.CompileEvidence);
        Assert.Same(evidence, SqlCompileEvidence.TryGetFromException(error));
        Assert.Equal(SqlCompileVerdict.Rejected, evidence.Verdict);
        Assert.Equal(SqlCompileDecisionBoundary.TargetCapability, evidence.DecisionBoundary);
    }

    [Fact]
    public void TryCompileQuery_WithDmlStatement_ReclassifiesTranslatedInnerCommandAsRejectedApiBoundary()
    {
        var result = SqlCoreFacade.TryCompileQuery(
            "UPDATE users SET name = 'Ada' WHERE id = 1",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("compile-evidence-query-kind-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(result.Success);
        var evidence = Assert.IsType<SqlCompileEvidence>(result.CompileEvidence);
        Assert.Equal(SqlCompileVerdict.Rejected, evidence.Verdict);
        Assert.Equal(SqlCompileDecisionBoundary.InputValidation, evidence.DecisionBoundary);
        Assert.Equal("SQL_API_STATEMENT_KIND_MISMATCH", evidence.DecisionCode);
        Assert.Null(evidence.PlanFingerprint);
    }

    [Fact]
    public void TryCompileDml_WithQueryStatement_ReclassifiesTranslatedInnerCommandAsRejectedApiBoundary()
    {
        var result = SqlCoreFacade.TryCompileDml(
            "SELECT 1",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("compile-evidence-dml-kind-v1"));

        Assert.False(result.Success);
        var evidence = Assert.IsType<SqlCompileEvidence>(result.CompileEvidence);
        Assert.Equal(SqlCompileVerdict.Rejected, evidence.Verdict);
        Assert.Equal(SqlCompileDecisionBoundary.InputValidation, evidence.DecisionBoundary);
        Assert.Equal("SQL_API_STATEMENT_KIND_MISMATCH", evidence.DecisionCode);
        Assert.Null(evidence.PlanFingerprint);
    }

    [Fact]
    public void ParseFailure_AttachesRejectedParseEvidence()
    {
        var result = SqlCoreFacade.TryCompileQuery(
            "SELECT FROM",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("compile-evidence-parse-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(result.Success);
        var evidence = Assert.IsType<SqlCompileEvidence>(result.CompileEvidence);
        Assert.Equal(SqlCompileVerdict.Rejected, evidence.Verdict);
        Assert.Equal(SqlCompileDecisionBoundary.Parse, evidence.DecisionBoundary);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void SameProviderCompile_RecordsCapabilitySnapshotsForAllProviders(
        SqlAgentToolType provider)
    {
        var command = SqlCoreFacade.CompileQuery(
            "SELECT 1",
            provider,
            provider,
            new SqlPlanValidationContext("compile-evidence-six-provider-v1"),
            new SqlExecutionPlanPolicy());

        var evidence = Assert.IsType<SqlCompileEvidence>(command.CompileEvidence);
        Assert.Equal(provider, evidence.SourceProfile.Provider);
        Assert.Equal(provider, evidence.TargetProfile.Provider);
        Assert.NotEmpty(evidence.SourceCapabilities);
        Assert.NotEmpty(evidence.TargetCapabilities);
        Assert.All(evidence.SourceCapabilities, item =>
            Assert.Equal(SqlCompileCapabilitySide.Source, item.Side));
        Assert.All(evidence.TargetCapabilities, item =>
            Assert.Equal(SqlCompileCapabilitySide.Target, item.Side));
    }

    private static string LegacyEvidenceFingerprint(SqlCompileEvidence evidence)
    {
        var builder = new StringBuilder();
        AppendToken(builder, evidence.SchemaVersion);
        AppendToken(builder, evidence.CapabilityMatrixVersion);
        AppendProfile(builder, evidence.SourceProfile);
        AppendProfile(builder, evidence.TargetProfile);
        AppendCapabilities(builder, evidence.SourceCapabilities);
        AppendCapabilities(builder, evidence.TargetCapabilities);
        AppendPolicy(builder, evidence.Policy);
        AppendAssurances(builder, evidence.Assurances);
        AppendInt(builder, (int)evidence.Verdict);
        AppendInt(builder, (int)evidence.DecisionBoundary);
        AppendToken(builder, evidence.DecisionCode);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendToken(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:;");
            return;
        }

        builder.Append(value.Length).Append(':').Append(value).Append(';');
    }

    private static void AppendInt(StringBuilder builder, int value)
        => AppendToken(builder, value.ToString(CultureInfo.InvariantCulture));

    private static void AppendBool(StringBuilder builder, bool value)
        => AppendToken(builder, value ? "1" : "0");

    private static void AppendProfile(
        StringBuilder builder,
        SqlCompileProfileEvidence profile)
    {
        AppendInt(builder, (int)profile.Provider);
        AppendToken(builder, profile.ServerVersion);
        AppendToken(
            builder,
            profile.CompatibilityLevel.HasValue
                ? profile.CompatibilityLevel.Value.ToString(CultureInfo.InvariantCulture)
                : null);
        AppendInt(builder, profile.SessionModes.Length);
        foreach (var mode in profile.SessionModes)
            AppendToken(builder, mode);
        AppendInt(builder, profile.SessionSettings.Length);
        foreach (var setting in profile.SessionSettings)
        {
            AppendToken(builder, setting.Name);
            AppendToken(builder, setting.Value);
        }
    }

    private static void AppendCapabilities(
        StringBuilder builder,
        IEnumerable<SqlCompileCapabilityEvidence> capabilities)
    {
        var values = capabilities.ToArray();
        AppendInt(builder, values.Length);
        foreach (var capability in values)
        {
            AppendInt(builder, (int)capability.Side);
            AppendToken(builder, capability.Id);
            AppendToken(builder, capability.Category);
            AppendInt(builder, (int)capability.Status);
        }
    }

    private static void AppendPolicy(
        StringBuilder builder,
        SqlCompilePolicyEvidence policy)
    {
        AppendToken(builder, policy.PolicyVersion);
        AppendInt(builder, policy.QueryMaxRows);
        AppendBool(builder, policy.RequireUpdatePredicate);
        AppendBool(builder, policy.RequireDeletePredicate);
        AppendInt(builder, policy.AllowedTables.Length);
        foreach (var table in policy.AllowedTables)
            AppendToken(builder, table);
    }

    private static void AppendAssurances(
        StringBuilder builder,
        IEnumerable<SqlCompileAssuranceEvidence> assurances)
    {
        var values = assurances.ToArray();
        AppendInt(builder, values.Length);
        foreach (var assurance in values)
        {
            AppendToken(builder, assurance.Kind);
            AppendInt(builder, assurance.Details.Length);
            foreach (var detail in assurance.Details)
            {
                AppendToken(builder, detail.Name);
                AppendToken(builder, detail.Value);
            }
        }
    }

    private static SqlProviderCapabilityProfile MySqlProfile(bool reverseInsertion)
    {
        var modes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (reverseInsertion)
        {
            modes.Add("PIPES_AS_CONCAT");
            modes.Add("ANSI_QUOTES");
            settings["sql_mode"] = "ANSI_QUOTES,PIPES_AS_CONCAT";
            settings["time_zone"] = "+00:00";
            settings["password"] = "must-not-enter-compile-evidence";
        }
        else
        {
            modes.Add("ANSI_QUOTES");
            modes.Add("PIPES_AS_CONCAT");
            settings["time_zone"] = "+00:00";
            settings["password"] = "must-not-enter-compile-evidence";
            settings["sql_mode"] = "ANSI_QUOTES,PIPES_AS_CONCAT";
        }

        return new SqlProviderCapabilityProfile(
            SqlAgentToolType.MySQL,
            new Version(8, 4),
            modes,
            settings);
    }

    private static string? Detail(
        SqlCompileAssuranceEvidence assurance,
        string name) =>
        assurance.Details.Single(item => item.Name == name).Value;
}
