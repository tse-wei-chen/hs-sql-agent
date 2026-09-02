using HsSqlAgent.SqlCore;
using System.Collections.Immutable;
using System.Text.Json;

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
    private readonly IProviderDmlResultRowMetadataReader? _dmlResultRowMetadataReader =
        metadataReader as IProviderDmlResultRowMetadataReader;
    // Explicit legacy compilers remain an opt-in compatibility seam for tests/custom hosts.
    // The default production path is the F# typestate facade.
    private readonly CoreDmlCompiler? _dmlCompiler = dmlCompiler;
    private readonly CoreSqlCompiler? _queryCompiler = queryCompiler;

    public async Task<ValidatedDmlPlan> CreateAsync(
        string connectionString,
        ParsedStatement parsedMutation,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? compilationPolicy = null,
        DmlRowIdentityAssurance assurance = DmlRowIdentityAssurance.Strict,
        int maxAffectedRows = 0,
        TimeSpan? approvalTtl = null,
        CancellationToken cancellationToken = default,
        SqlProviderCapabilityProfile? targetProfile = null)
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
                cancellationToken,
                targetProfile);
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
        var resolvedMutation = CloneParsedWithStatement(parsedMutation, resolvedStatement);
        var effectiveValidationContext = await PrepareResultRowValidationContextAsync(
            connectionString,
            resolvedStatement,
            targetProvider,
            validationContext,
            operation,
            identity.Schema,
            identity.Table,
            cancellationToken);

        var mutationCommand = CompileMutation(
            resolvedMutation,
            targetProvider,
            effectiveValidationContext,
            compilationPolicy,
            targetProfile);

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

        var matchCommand = CompileMatchQuery(
            parsedMatch,
            targetProvider,
            validationContext,
            targetProfile);

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
        CancellationToken cancellationToken,
        SqlProviderCapabilityProfile? targetProfile)
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
        var resolvedInsert = (InsertStatement)ReplaceTarget(insert, resolvedTarget);
        var resolvedMutation = CloneParsedWithStatement(parsedMutation, resolvedInsert);
        var effectiveValidationContext = await PrepareResultRowValidationContextAsync(
            connectionString,
            resolvedInsert,
            targetProvider,
            validationContext,
            DmlOperation.Insert,
            targetResolution.Schema,
            targetResolution.Table,
            cancellationToken);

        var mutationCommand = CompileMutation(
            resolvedMutation,
            targetProvider,
            effectiveValidationContext,
            compilationPolicy,
            targetProfile);
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

    private async Task<SqlPlanValidationContext> PrepareResultRowValidationContextAsync(
        string connectionString,
        SqlStatement resolvedStatement,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlOperation operation,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        if (targetProvider != SqlAgentToolType.MsSqlServer || !ReturnsRows(resolvedStatement))
            return validationContext;

        if (_dmlResultRowMetadataReader is null)
        {
            throw new InvalidOperationException(
                "SQL Server OUTPUT requires provider trigger metadata support; no DML result-row metadata reader is available for the resolved target.");
        }

        var hasEnabledTrigger = await _dmlResultRowMetadataReader.HasEnabledDmlTriggerAsync(
            connectionString,
            schema,
            table,
            operation,
            cancellationToken);

        if (hasEnabledTrigger)
        {
            throw new InvalidOperationException(
                $"SQL Server OUTPUT without INTO remains fail-closed because resolved target '{schema}.{table}' has an enabled {operation.ToString().ToUpperInvariant()} trigger.");
        }

        var metadataBackedContext = new SqlPlanValidationContext(
            validationContext.PolicyVersion,
            validationContext.AllowedTables);

        return metadataBackedContext.WithDmlResultRowAssurance(
            DmlResultRowAssurance.NoEnabledTriggers(
                $"{schema}.{table}",
                operation));
    }

    private static bool ReturnsRows(SqlStatement statement) => statement switch
    {
        InsertStatement insert => !insert.Returning.IsDefaultOrEmpty,
        UpdateStatement update => !update.Returning.IsDefaultOrEmpty,
        DeleteStatement delete => !delete.Returning.IsDefaultOrEmpty,
        _ => false
    };

    private CompiledSqlCommand CompileMutation(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? compilationPolicy,
        SqlProviderCapabilityProfile? targetProfile)
    {
        if (_dmlCompiler is not null)
        {
            return _dmlCompiler.Compile(
                parsed,
                targetProvider,
                validationContext,
                compilationPolicy,
                targetProfile);
        }

        return SqlCoreFacade.CompileDml(
            parsed,
            targetProvider,
            validationContext,
            compilationPolicy,
            targetProfile,
            conflictTargetAssurance: null);
    }

    private CompiledSqlCommand CompileMatchQuery(
        ParsedStatement parsed,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        SqlProviderCapabilityProfile? targetProfile)
    {
        if (_queryCompiler is not null)
        {
            return _queryCompiler.Compile(
                parsed,
                targetProvider,
                validationContext,
                new SqlExecutionPlanPolicy(),
                targetProfile);
        }

        return SqlCoreFacade.CompileQuery(
            parsed,
            targetProvider,
            validationContext,
            new SqlExecutionPlanPolicy(),
            targetProfile);
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

    private static ParsedStatement CloneParsedWithStatement(
        ParsedStatement source,
        SqlStatement statement) =>
        new(
            statement,
            source.SourceDialect,
            source.EnforceSourceDialectSyntax,
            source.SourceProfile);

    private static SqlStatement ReplaceTarget(
        SqlStatement statement,
        NamedTableSource resolvedTarget)
    {
        switch (statement)
        {
            case UpdateStatement update:
            {
                var clone = new UpdateStatement(
                    resolvedTarget,
                    update.Assignments,
                    update.Predicate,
                    update.Span)
                {
                    From = update.From,
                    Returning = update.Returning
                };
                return clone;
            }
            case DeleteStatement delete:
            {
                var clone = new DeleteStatement(
                    resolvedTarget,
                    delete.Predicate,
                    delete.Span)
                {
                    Using = delete.Using,
                    Returning = delete.Returning
                };
                return clone;
            }
            case InsertStatement insert:
            {
                var clone = new InsertStatement(
                    resolvedTarget,
                    insert.Columns,
                    insert.Source,
                    insert.Span)
                {
                    Conflict = insert.Conflict,
                    Returning = insert.Returning
                };
                return clone;
            }
            default:
                throw new InvalidOperationException(
                    $"Statement '{statement.GetType().Name}' is not a supported DML mutation.");
        }
    }

    private static SqlIdentifier MetadataIdentifier(params string[] parts) =>
        new(
            parts.Select(part => new IdentifierPart(
                    part,
                    true,
                    SourceSpan.Unknown))
                .ToImmutableArray(),
            SourceSpan.Unknown);

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
