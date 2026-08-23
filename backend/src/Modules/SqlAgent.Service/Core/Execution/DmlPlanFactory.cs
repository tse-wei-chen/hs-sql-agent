using System.Collections.Immutable;
using System.Text.Json;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Builds the immutable DML plan consumed by <see cref="DmlCoordinator"/>. UPDATE/DELETE mutation
/// and match commands are derived from the same parser-native Core statement and resolved physical
/// target. INSERT VALUES plans instead bind approval to the exact compiled payload because no
/// pre-existing target row set exists to fingerprint.
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

        if (parsedMutation.Statement is InsertStatement insert)
        {
            return await CreateInsertValuesPlanAsync(
                connectionString,
                parsedMutation,
                insert,
                targetProvider,
                validationContext,
                compilationPolicy,
                maxAffectedRows,
                approvalTtl,
                cancellationToken);
        }

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

    private async Task<ValidatedDmlPlan> CreateInsertValuesPlanAsync(
        string connectionString,
        ParsedStatement parsedMutation,
        InsertStatement insert,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? compilationPolicy,
        int maxAffectedRows,
        TimeSpan? approvalTtl,
        CancellationToken cancellationToken)
    {
        if (insert.Source is not InsertValuesSource values)
        {
            throw new NotSupportedException(
                "INSERT ... SELECT remains fail-closed until source-rowset approval semantics are defined.");
        }

        var requestedTarget = IdentifierText(insert.Target.Name);
        if (string.IsNullOrWhiteSpace(requestedTarget))
            throw new InvalidOperationException("DML target table must not be empty.");

        // INSERT VALUES needs physical-target resolution and authorization, but it does not need a
        // pre-existing primary-key row set. CountOnly here intentionally disables the PK requirement.
        var targetResolution = await _rowIdentityResolver.ResolveTargetAsync(
            connectionString,
            requestedTarget,
            DmlRowIdentityAssurance.CountOnly,
            cancellationToken);
        var resolvedTarget = new NamedTableSource(
            MetadataIdentifier(targetResolution.Schema, targetResolution.Table),
            null,
            insert.Target.Span);
        var resolvedInsert = insert with { Target = resolvedTarget };
        var resolvedMutation = new ParsedStatement(resolvedInsert, parsedMutation.SourceDialect);

        var mutationCommand = _dmlCompiler.Compile(
            resolvedMutation,
            targetProvider,
            validationContext,
            compilationPolicy);
        var previewRows = BuildInsertPreviewRows(resolvedInsert, values);

        if (maxAffectedRows > 0 && previewRows.Length > maxAffectedRows)
        {
            throw new UnauthorizedAccessException(
                $"Security policy denied INSERT: rowCount={previewRows.Length} exceeds maximum {maxAffectedRows}.");
        }

        var fingerprint = DmlFingerprintService.ComputePlanFingerprint(
            mutationCommand,
            validationContext.PolicyVersion);

        return new ValidatedDmlPlan(
            DmlOperation.Insert,
            targetResolution.QualifiedTableName,
            mutationCommand,
            MatchQueryCommand: null,
            RowIdentityColumns: ImmutableArray<string>.Empty,
            RowIdentityAssurance: DmlRowIdentityAssurance.CountOnly,
            PlanFingerprint: fingerprint,
            PolicyVersion: validationContext.PolicyVersion,
            ApprovalTtl: approvalTtl.GetValueOrDefault(TimeSpan.FromMinutes(5)),
            MaxAffectedRows: maxAffectedRows,
            ApprovalMode: DmlApprovalMode.InsertValues,
            InsertRows: previewRows);
    }

    private static ImmutableArray<ImmutableDictionary<string, object?>> BuildInsertPreviewRows(
        InsertStatement insert,
        InsertValuesSource values)
    {
        if (insert.Columns.IsDefaultOrEmpty)
            throw new InvalidOperationException("INSERT requires at least one target column.");
        if (values.Rows.IsDefaultOrEmpty)
            throw new InvalidOperationException("INSERT VALUES requires at least one row.");

        var rows = ImmutableArray.CreateBuilder<ImmutableDictionary<string, object?>>(values.Rows.Length);
        for (var rowIndex = 0; rowIndex < values.Rows.Length; rowIndex++)
        {
            var row = values.Rows[rowIndex];
            if (row.Length != insert.Columns.Length)
            {
                throw new InvalidOperationException(
                    $"INSERT row {rowIndex + 1} has {row.Length} values but {insert.Columns.Length} columns were declared.");
            }

            var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var columnIndex = 0; columnIndex < insert.Columns.Length; columnIndex++)
            {
                if (row[columnIndex] is not LiteralExpr literal)
                {
                    throw new InvalidOperationException(
                        $"INSERT VALUES preview requires literal canonical values, not {row[columnIndex].GetType().Name}.");
                }

                builder.Add(
                    IdentifierText(insert.Columns[columnIndex]),
                    NormalizeInsertPreviewValue(literal.Value));
            }
            rows.Add(builder.ToImmutable());
        }

        return rows.ToImmutable();
    }

    private static object? NormalizeInsertPreviewValue(object? value)
    {
        if (value is not JsonElement json) return value;
        return json.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => json.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when json.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when json.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => json.GetDouble(),
            _ => throw new InvalidOperationException(
                $"INSERT preview literal JSON kind '{json.ValueKind}' is not a scalar SQL value.")
        };
    }

    private static (DmlOperation Operation, NamedTableSource Target, SqlExpr? Predicate) MutationShape(
        SqlStatement statement) => statement switch
    {
        UpdateStatement update => (DmlOperation.Update, update.Target, update.Predicate),
        DeleteStatement delete => (DmlOperation.Delete, delete.Target, delete.Predicate),
        _ => throw new InvalidOperationException(
            $"Statement '{statement.GetType().Name}' is not a supported row-set DML mutation.")
    };

    private static SqlStatement ReplaceTarget(
        SqlStatement statement,
        NamedTableSource resolvedTarget) => statement switch
    {
        UpdateStatement update => update with { Target = resolvedTarget },
        DeleteStatement delete => delete with { Target = resolvedTarget },
        InsertStatement insert => insert with { Target = resolvedTarget },
        _ => throw new InvalidOperationException(
            $"Statement '{statement.GetType().Name}' is not a supported DML mutation.")
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
