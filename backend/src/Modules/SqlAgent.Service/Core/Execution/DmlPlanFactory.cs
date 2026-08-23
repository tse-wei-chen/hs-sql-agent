using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Builds the immutable DML plan consumed by <see cref="DmlCoordinator"/>. Mutation and match
/// commands are both derived from the same parser-native Core statement and resolved physical
/// target, preventing approval of one target or predicate from being paired with a different
/// mutation.
/// </summary>
public sealed class DmlPlanFactory(
    IProviderMetadataReader metadataReader,
    CoreDmlCompiler? dmlCompiler = null,
    CoreSqlCompiler? queryCompiler = null)
{
    private readonly DmlRowIdentityResolver _rowIdentityResolver = new(metadataReader);
    private readonly CoreDmlCompiler _dmlCompiler = dmlCompiler ?? CoreDmlCompiler.CreateDefault();
    private readonly CoreSqlCompiler _queryCompiler = queryCompiler ?? CoreSqlCompiler.CreateDefault();

    public async Task<ValidatedDmlPlan> CreateAsync(
        string connectionString,
        ParsedStatement parsedMutation,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? compilationPolicy = null,
        DmlRowIdentityAssurance assurance = DmlRowIdentityAssurance.Strict,
        int maxAffectedRows = 0,
        TimeSpan? approvalTtl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsedMutation);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (maxAffectedRows < 0)
            throw new ArgumentOutOfRangeException(nameof(maxAffectedRows));

        var (operation, target, predicate) = MutationShape(parsedMutation.Statement);
        var requestedTarget = IdentifierText(target.Name);
        if (string.IsNullOrWhiteSpace(requestedTarget))
            throw new InvalidOperationException("DML target table must not be empty.");

        var identity = await _rowIdentityResolver.ResolveTargetAsync(
            connectionString,
            requestedTarget,
            assurance,
            cancellationToken);
        var resolvedTarget = new NamedTableSource(
            MetadataIdentifier(identity.Schema, identity.Table),
            null,
            target.Span);
        var resolvedStatement = ReplaceTarget(parsedMutation.Statement, resolvedTarget);
        var resolvedMutation = new ParsedStatement(resolvedStatement, parsedMutation.SourceDialect);

        var mutationCommand = _dmlCompiler.Compile(
            resolvedMutation,
            targetProvider,
            validationContext,
            compilationPolicy);

        var identityColumns = identity.Columns;
        var selectItems = identityColumns.IsDefaultOrEmpty
            ? ImmutableArray.Create(new SelectItem(
                new LiteralExpr(1, SourceSpan.Unknown),
                "__match",
                SourceSpan.Unknown))
            : identityColumns
                .Select(column => new SelectItem(
                    new ColumnExpr(MetadataIdentifier(column), SourceSpan.Unknown),
                    null,
                    SourceSpan.Unknown))
                .ToImmutableArray();

        var matchStatement = new SelectStatement(
            ImmutableArray<CteDefinition>.Empty,
            false,
            selectItems,
            resolvedTarget,
            ImmutableArray<JoinSource>.Empty,
            predicate,
            ImmutableArray<SqlExpr>.Empty,
            null,
            ImmutableArray<OrderByItem>.Empty,
            maxAffectedRows > 0
                ? maxAffectedRows == int.MaxValue ? int.MaxValue : maxAffectedRows + 1
                : null,
            null,
            SourceSpan.Unknown);
        var parsedMatch = new ParsedStatement(matchStatement, parsedMutation.SourceDialect);

        var matchCommand = _queryCompiler.Compile(
            parsedMatch,
            targetProvider,
            validationContext,
            new SqlExecutionPlanPolicy());

        var fingerprint = DmlFingerprintService.ComputePlanFingerprint(
            mutationCommand,
            validationContext.PolicyVersion);

        return new ValidatedDmlPlan(
            operation,
            identity.QualifiedTableName,
            mutationCommand,
            matchCommand,
            identityColumns,
            assurance,
            fingerprint,
            validationContext.PolicyVersion,
            approvalTtl.GetValueOrDefault(TimeSpan.FromMinutes(5)),
            maxAffectedRows);
    }

    private static (DmlOperation Operation, NamedTableSource Target, SqlExpr? Predicate) MutationShape(
        SqlStatement statement) => statement switch
    {
        UpdateStatement update => (DmlOperation.Update, update.Target, update.Predicate),
        DeleteStatement delete => (DmlOperation.Delete, delete.Target, delete.Predicate),
        InsertStatement => throw new InvalidOperationException(
            "Row-set approval planning currently supports UPDATE and DELETE only."),
        _ => throw new InvalidOperationException(
            $"Statement '{statement.GetType().Name}' is not a supported DML mutation.")
    };

    private static SqlStatement ReplaceTarget(
        SqlStatement statement,
        NamedTableSource resolvedTarget) => statement switch
    {
        UpdateStatement update => update with { Target = resolvedTarget },
        DeleteStatement delete => delete with { Target = resolvedTarget },
        _ => throw new InvalidOperationException(
            $"Statement '{statement.GetType().Name}' is not a supported row-set mutation.")
    };

    private static SqlIdentifier MetadataIdentifier(params string[] parts) =>
        new(
            parts.Select(part => new IdentifierPart(
                    part,
                    WasQuoted: true,
                    SourceSpan.Unknown))
                .ToImmutableArray(),
            SourceSpan.Unknown);

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
