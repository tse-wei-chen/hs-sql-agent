using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G2 S03/S04/S08: preview uses only the already compiled plan and an
/// optional caller-declared virtual Catalog. No live authority or SQL execution.
/// </summary>
public sealed class CoreDdlPreviewTests
{
    public static IEnumerable<object[]> Providers() =>
        Enum.GetValues<SqlAgentToolType>().Select(provider => new object[] { provider });

    private static DdlCompilationOptions Options(SqlAgentToolType provider) => new(provider, provider);
    private static string Type(SqlAgentToolType provider) =>
        provider == SqlAgentToolType.MsSqlServer ? "INT" : "INTEGER";
    private static string Table(SqlAgentToolType provider) =>
        $"CREATE TABLE widgets (id {Type(provider)} NOT NULL)";
    private static string Add(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.MsSqlServer => "ALTER TABLE widgets ADD nick VARCHAR(40)",
        SqlAgentToolType.Oracle => "ALTER TABLE widgets ADD (nick VARCHAR2(40))",
        SqlAgentToolType.Firebird => "ALTER TABLE widgets ADD nick VARCHAR(40)",
        _ => "ALTER TABLE widgets ADD COLUMN nick VARCHAR(40)"
    };
    private static SqlCatalogState KnownTable(SqlAgentToolType provider) =>
        SqlCoreFacade.AnalyzeScript(Table(provider), provider, SqlCatalogState.KnownEmpty()).FinalCatalog;

    [Theory]
    [MemberData(nameof(Providers))]
    public void UnknownCatalog_CreateTable_ProjectsTypedAfterButNeverProofOfAbsence(SqlAgentToolType provider)
    {
        var preview = SqlCoreFacade.PreviewDdl(Table(provider), Options(provider));
        Assert.Equal(DdlObjectPresence.Unknown, preview.Before.Presence);
        Assert.Equal(DdlObjectPresence.Present, preview.ExpectedAfter.Presence);
        Assert.True(preview.ExpectedAfter.IsConditional);
        Assert.Single(preview.ExpectedAfter.Columns);
        Assert.Equal("id", preview.ExpectedAfter.Columns[0].Name, ignoreCase: true);
        Assert.False(preview.ExpectedAfter.Columns[0].Nullable);
        Assert.Equal(DdlPreviewPreconditionStatus.Unverified, preview.PreconditionStatus);
        Assert.True(preview.RequiresLiveValidation);
        Assert.True(preview.RequiresDdlApproval);
        Assert.False(preview.IsLiveVerified);
        Assert.False(preview.AuthorizesExecution);
        Assert.NotEmpty(preview.Warnings);
        Assert.Equal(preview.Plan.PlanFingerprint, preview.PlanFingerprint);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void CallerDeclaredKnownEmpty_CanProveOnlyVirtualAbsence(SqlAgentToolType provider)
    {
        var preview = SqlCoreFacade.PreviewDdl(
            Table(provider), Options(provider), SqlCatalogState.KnownEmpty());
        Assert.Equal(DdlObjectPresence.Absent, preview.Before.Presence);
        Assert.Equal(DdlPreviewPreconditionStatus.ConsistentWithCallerCatalog, preview.PreconditionStatus);
        Assert.Equal(DdlObjectPresence.Present, preview.ExpectedAfter.Presence);
        Assert.False(preview.IsLiveVerified);
        Assert.True(preview.ExpectedAfter.IsConditional);
        Assert.True(preview.RequiresLiveValidation);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void AlreadyKnownTable_CreateConflict_DoesNotPretendToApply(SqlAgentToolType provider)
    {
        var before = KnownTable(provider);
        Assert.Equal(SqlCatalogKnowledge.Known, before.Knowledge);
        var preview = SqlCoreFacade.PreviewDdl(Table(provider), Options(provider), before);
        Assert.Equal(DdlObjectPresence.Present, preview.Before.Presence);
        Assert.Equal(DdlPreviewPreconditionStatus.ContradictedByCallerCatalog, preview.PreconditionStatus);
        Assert.Equal(DdlObjectPresence.Present, preview.ExpectedAfter.Presence);
        Assert.True(preview.ExpectedAfter.IsConditional);
        Assert.Contains(preview.Warnings, warning => warning.Contains("precondition", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void AddNullableColumn_GivesTrueBeforeAfterFromTypedKnownCatalog(SqlAgentToolType provider)
    {
        var catalog = KnownTable(provider);
        var preview = SqlCoreFacade.PreviewDdl(Add(provider), Options(provider), catalog);
        Assert.Equal(DdlPreviewPreconditionStatus.ConsistentWithCallerCatalog, preview.PreconditionStatus);
        Assert.Single(preview.Before.Columns);
        Assert.Equal(2, preview.ExpectedAfter.Columns.Count);
        Assert.Equal("nick", preview.ExpectedAfter.Columns[1].Name, ignoreCase: true);
        Assert.True(preview.ExpectedAfter.Columns[1].Nullable);
        Assert.Equal(provider == SqlAgentToolType.Oracle ? "VARCHAR2(40)" : "VARCHAR(40)",
            preview.ExpectedAfter.Columns[1].TypeName);
        Assert.Single(catalog.Tables[0].Columns); // no mutation of the caller snapshot
        Assert.True(preview.Plan.Impact.MayLockSchema);
        Assert.Null(preview.Plan.Impact.EstimatedAffectedRows);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void UnknownOrAbsentTable_AddColumn_NeverInventsFullAfterSchema(SqlAgentToolType provider)
    {
        var unknown = SqlCoreFacade.PreviewDdl(Add(provider), Options(provider));
        Assert.Equal(DdlObjectPresence.Unknown, unknown.ExpectedAfter.Presence);
        Assert.Equal(DdlPreviewPreconditionStatus.Unverified, unknown.PreconditionStatus);
        var absent = SqlCoreFacade.PreviewDdl(
            Add(provider), Options(provider), SqlCatalogState.KnownEmpty());
        Assert.Equal(DdlObjectPresence.Absent, absent.Before.Presence);
        Assert.Equal(DdlObjectPresence.Unknown, absent.ExpectedAfter.Presence);
        Assert.Equal(DdlPreviewPreconditionStatus.ContradictedByCallerCatalog, absent.PreconditionStatus);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void DuplicateAdd_ContradictsCatalogAndNeverPublishesHypotheticalExtraColumn(SqlAgentToolType provider)
    {
        var catalog = KnownTable(provider);
        var sql = Add(provider).Replace("nick", "id", StringComparison.Ordinal);
        var preview = SqlCoreFacade.PreviewDdl(sql, Options(provider), catalog);
        Assert.Equal(DdlPreviewPreconditionStatus.ContradictedByCallerCatalog, preview.PreconditionStatus);
        Assert.Single(preview.ExpectedAfter.Columns);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void IndexOnKnownTable_ProjectsTypedIndexAndPreservesBaseColumns(SqlAgentToolType provider)
    {
        var catalog = KnownTable(provider);
        var preview = SqlCoreFacade.PreviewDdl(
            "CREATE INDEX idx_id ON widgets (id)", Options(provider), catalog);
        Assert.Equal(DdlObjectPresence.Present, preview.ExpectedAfter.Presence);
        Assert.Single(preview.ExpectedAfter.Indexes);
        Assert.Equal("idx_id", preview.ExpectedAfter.Indexes[0].Name, ignoreCase: true);
        Assert.Single(preview.ExpectedAfter.Columns);
        // Catalog omits namespace-global index identity, so local absence is not sufficient.
        Assert.Equal(DdlPreviewPreconditionStatus.Unverified, preview.PreconditionStatus);
        Assert.True(preview.RequiresLiveValidation);
    }

    [Fact]
    public void KnownViewDependency_IsListedButNotClaimedCompleteInPhysicalDatabase()
    {
        const SqlAgentToolType provider = SqlAgentToolType.Postgres;
        var catalog = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE widgets (id INTEGER NOT NULL); " +
            "CREATE VIEW public.v_widgets AS SELECT id FROM widgets",
            provider, SqlCatalogState.KnownEmpty()).FinalCatalog;
        var preview = SqlCoreFacade.PreviewDdl(Add(provider), Options(provider), catalog);
        Assert.Contains(preview.Dependencies,
            dependency => dependency.Kind == DdlDependencyKind.View);
        Assert.False(preview.DependenciesAreLiveVerified);
        Assert.True(preview.RequiresLiveValidation);
    }

    [Fact]
    public void TryPreviewDdl_RejectsAllUnsafeDdlWithoutCreatingPlan()
    {
        foreach (var text in new[]
        {
            "DROP TABLE widgets",
            "CREATE TABLE widgets (id INTEGER); DELETE FROM widgets",
            "ALTER TABLE widgets DROP COLUMN id",
            "CREATE UNIQUE INDEX idx_id ON widgets (id)",
            "GRANT ALL ON widgets TO public"
        })
        {
            var result = SqlCoreFacade.TryPreviewDdl(text, Options(SqlAgentToolType.Postgres));
            Assert.False(result.Success, text);
            Assert.Null(result.Value);
            Assert.NotNull(result.ErrorCode);
        }
    }
}
