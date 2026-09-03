using HsSqlAgent.SqlCore;
using HsSqlAgent.Provider.Abstractions;
using System.Collections.Immutable;
using System.Data.Common;
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
    private readonly IProviderConnectionDmlPlanningMetadataReader? _dmlPlanningMetadataReader =
        metadataReader as IProviderConnectionDmlPlanningMetadataReader;
    private readonly HsSqlAgent.Provider.Abstractions.IProviderDmlResultRowMetadataReader? _dmlResultRowMetadataReader =
        metadataReader as HsSqlAgent.Provider.Abstractions.IProviderDmlResultRowMetadataReader;
    // Explicit legacy compilers remain an opt-in compatibility seam for tests/custom hosts.
    // The default production path is the F# typestate facade.
    private readonly CoreDmlCompiler? _dmlCompiler = dmlCompiler;
    private readonly CoreSqlCompiler? _queryCompiler = queryCompiler;

    public Task<ValidatedDmlPlan> CreateAsync(
        string connectionString,
        ParsedStatement parsedMutation,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? compilationPolicy = null,
        DmlRowIdentityAssurance assurance = DmlRowIdentityAssurance.Strict,
        int maxAffectedRows = 0,
        TimeSpan? approvalTtl = null,
        CancellationToken cancellationToken = default,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        CreateCoreAsync(
            metadataConnection: null,
            connectionString,
            parsedMutation,
            targetProvider,
            validationContext,
            compilationPolicy,
            assurance,
            maxAffectedRows,
            approvalTtl,
            cancellationToken,
            targetProfile);

    public Task<ValidatedDmlPlan> CreateWithMetadataConnectionAsync(
        DbConnection metadataConnection,
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
        ArgumentNullException.ThrowIfNull(metadataConnection);
        return CreateCoreAsync(
            metadataConnection,
            connectionString,
            parsedMutation,
            targetProvider,
            validationContext,
            compilationPolicy,
            assurance,
            maxAffectedRows,
            approvalTtl,
            cancellationToken,
            targetProfile);
    }

    private async Task<ValidatedDmlPlan> CreateCoreAsync(
        DbConnection? metadataConnection,
        string connectionString,
        ParsedStatement parsedMutation,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? compilationPolicy,
        DmlRowIdentityAssurance assurance,
        int maxAffectedRows,
        TimeSpan? approvalTtl,
        CancellationToken cancellationToken,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(parsedMutation);
        ArgumentNullException.ThrowIfNull(validationContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (maxAffectedRows < 0)
            throw new ArgumentOutOfRangeException(nameof(maxAffectedRows));

        if (parsedMutation.Statement is InsertStatement insert)
        {
            return await CreateInsertValuesPlanAsync(
                metadataConnection,
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

        DatabaseDmlPlanningMetadata? planningMetadata = null;
        DmlRowIdentityResolution identity;
        var triggerOperation = targetProvider == SqlAgentToolType.MsSqlServer
            && ReturnsRows(parsedMutation.Statement)
                ? operation
                : (DmlOperation?)null;
        if (ShouldUsePlanningSnapshot(metadataConnection, target.Name, triggerOperation.HasValue))
        {
            var requested = RequestedTargetParts(target.Name);
            var matches = await _dmlPlanningMetadataReader!.GetDmlPlanningMetadataAsync(
                metadataConnection!,
                requested.Schema,
                requested.Table,
                includeColumns: true,
                triggerOperation,
                cancellationToken);
            planningMetadata = ResolvePlanningTarget(matches, requestedTarget);
            identity = ResolveRowIdentity(planningMetadata, requestedTarget, assurance);
        }
        else
        {
            identity = metadataConnection is null
                ? await _rowIdentityResolver.ResolveTargetAsync(
                    connectionString,
                    requestedTarget,
                    assurance,
                    cancellationToken)
                : await _rowIdentityResolver.ResolveTargetAsync(
                    metadataConnection,
                    connectionString,
                    requestedTarget,
                    assurance,
                    cancellationToken);
        }
        var resolvedTarget = new NamedTableSource(
            MetadataIdentifier(identity.Schema, identity.Table),
            target.Alias,
            target.Span);
        var resolvedStatement = ReplaceTarget(parsedMutation.Statement, resolvedTarget);
        var resolvedMutation = CloneParsedWithStatement(parsedMutation, resolvedStatement);
        var effectiveValidationContext = await PrepareResultRowValidationContextAsync(
            metadataConnection,
            connectionString,
            resolvedStatement,
            targetProvider,
            validationContext,
            operation,
            identity.Schema,
            identity.Table,
            planningMetadata is not null && triggerOperation.HasValue,
            planningMetadata?.HasEnabledDmlTrigger,
            cancellationToken);

        var mutationCommand = CompileMutation(
            resolvedMutation,
            targetProvider,
            effectiveValidationContext,
            compilationPolicy,
            targetProfile);

        var identityColumns = identity.Columns;
        var auxiliarySources = MutationSources(resolvedStatement);
        if (!auxiliarySources.IsDefaultOrEmpty && identityColumns.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                "Joined UPDATE/DELETE approval requires resolved target row identity so duplicate auxiliary matches can be collapsed to the affected target-row set.");
        }

        var selectItems = identityColumns.IsDefaultOrEmpty
            ? ImmutableArray.Create(new SelectItem(
                new LiteralExpr(1, SourceSpan.Unknown),
                "__match",
                SourceSpan.Unknown))
            : identityColumns
                .Select(column => new SelectItem(
                    new ColumnExpr(TargetIdentityIdentifier(resolvedTarget, column), SourceSpan.Unknown),
                    null,
                    SourceSpan.Unknown))
                .ToImmutableArray();
        var matchJoins = auxiliarySources
            .Select(source => new JoinSource(
                "CROSS",
                source,
                null!,
                SourceSpan.Unknown))
            .ToImmutableArray();

        var matchStatement = new SelectStatement(
            ImmutableArray<CteDefinition>.Empty,
            !auxiliarySources.IsDefaultOrEmpty,
            selectItems,
            resolvedTarget,
            matchJoins,
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
        DbConnection? metadataConnection,
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

        // INSERT VALUES needs only physical-target resolution and authorization. It has no
        // pre-existing target row set, so loading column/primary-key metadata here is pure overhead.
        DatabaseDmlPlanningMetadata? planningMetadata = null;
        DmlPhysicalTargetResolution targetResolution;
        var triggerOperation = targetProvider == SqlAgentToolType.MsSqlServer
            && ReturnsRows(parsedMutation.Statement)
                ? DmlOperation.Insert
                : (DmlOperation?)null;
        if (ShouldUsePlanningSnapshot(metadataConnection, insert.Target.Name, triggerOperation.HasValue))
        {
            var requested = RequestedTargetParts(insert.Target.Name);
            var matches = await _dmlPlanningMetadataReader!.GetDmlPlanningMetadataAsync(
                metadataConnection!,
                requested.Schema,
                requested.Table,
                includeColumns: false,
                triggerOperation,
                cancellationToken);
            planningMetadata = ResolvePlanningTarget(matches, requestedTarget);
            targetResolution = new DmlPhysicalTargetResolution(
                planningMetadata.Schema,
                planningMetadata.Table);
        }
        else
        {
            targetResolution = metadataConnection is null
                ? await _rowIdentityResolver.ResolvePhysicalTargetAsync(
                    connectionString,
                    requestedTarget,
                    cancellationToken)
                : await _rowIdentityResolver.ResolvePhysicalTargetAsync(
                    metadataConnection,
                    connectionString,
                    requestedTarget,
                    cancellationToken);
        }
        var resolvedTarget = new NamedTableSource(
            MetadataIdentifier(targetResolution.Schema, targetResolution.Table),
            insert.Target.Alias,
            insert.Target.Span);
        var resolvedInsert = (InsertStatement)ReplaceTarget(insert, resolvedTarget);
        var resolvedMutation = CloneParsedWithStatement(parsedMutation, resolvedInsert);
        var effectiveValidationContext = await PrepareResultRowValidationContextAsync(
            metadataConnection,
            connectionString,
            resolvedInsert,
            targetProvider,
            validationContext,
            DmlOperation.Insert,
            targetResolution.Schema,
            targetResolution.Table,
            planningMetadata is not null && triggerOperation.HasValue,
            planningMetadata?.HasEnabledDmlTrigger,
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
        DbConnection? metadataConnection,
        string connectionString,
        SqlStatement resolvedStatement,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlOperation operation,
        string schema,
        string table,
        bool hasPlanningTriggerMetadata,
        bool? planningHasEnabledTrigger,
        CancellationToken cancellationToken)
    {
        if (targetProvider != SqlAgentToolType.MsSqlServer || !ReturnsRows(resolvedStatement))
            return validationContext;

        bool hasEnabledTrigger;
        if (hasPlanningTriggerMetadata)
        {
            if (!planningHasEnabledTrigger.HasValue)
            {
                throw new InvalidOperationException(
                    $"SQL Server OUTPUT trigger assurance for '{schema}.{table}' requires VIEW DEFINITION metadata visibility; Core remains fail-closed when trigger metadata completeness cannot be proven.");
            }

            hasEnabledTrigger = planningHasEnabledTrigger.Value;
        }
        else
        {
            if (_dmlResultRowMetadataReader is null)
            {
                throw new InvalidOperationException(
                    "SQL Server OUTPUT requires provider trigger metadata support; no DML result-row metadata reader is available for the resolved target.");
            }

            hasEnabledTrigger =
                metadataConnection is not null
                && _dmlResultRowMetadataReader is IProviderConnectionDmlResultRowMetadataReader connectionTriggerMetadata
                    ? await connectionTriggerMetadata.HasEnabledDmlTriggerAsync(
                        metadataConnection,
                        schema,
                        table,
                        operation,
                        cancellationToken)
                    : await _dmlResultRowMetadataReader.HasEnabledDmlTriggerAsync(
                        connectionString,
                        schema,
                        table,
                        operation,
                        cancellationToken);
        }

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

    private bool ShouldUsePlanningSnapshot(
        DbConnection? metadataConnection,
        SqlIdentifier target,
        bool triggerMetadataRequired) =>
        metadataConnection is not null
        && _dmlPlanningMetadataReader is not null
        && (target.Parts.Length == 1
            || (target.Parts.Length == 2 && triggerMetadataRequired));

    private static (string? Schema, string Table) RequestedTargetParts(SqlIdentifier target) =>
        target.Parts.Length switch
        {
            1 => (null, target.Parts[0].Value),
            2 => (target.Parts[0].Value, target.Parts[1].Value),
            _ => throw new InvalidOperationException(
                $"DML target '{IdentifierText(target)}' must be <table> or <schema>.<table> for metadata planning.")
        };

    private static DatabaseDmlPlanningMetadata ResolvePlanningTarget(
        IReadOnlyList<DatabaseDmlPlanningMetadata> matches,
        string requestedTarget) =>
        matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"DML target '{requestedTarget}' could not be resolved to a physical table. Schema-qualify the target explicitly."),
            _ => throw new InvalidOperationException(
                $"DML target '{requestedTarget}' is ambiguous across schemas. Schema-qualify the target explicitly.")
        };

    private static DmlRowIdentityResolution ResolveRowIdentity(
        DatabaseDmlPlanningMetadata metadata,
        string requestedTarget,
        DmlRowIdentityAssurance assurance)
    {
        var primaryKey = metadata.Columns
            .Where(column => column.IsPrimaryKey)
            .OrderBy(column => column.PrimaryKeyOrdinal ?? int.MaxValue)
            .ThenBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
            .Select(column => column.Name)
            .ToImmutableArray();

        if (!primaryKey.IsDefaultOrEmpty)
            return new DmlRowIdentityResolution(metadata.Schema, metadata.Table, primaryKey);

        if (assurance == DmlRowIdentityAssurance.CountOnly)
            return new DmlRowIdentityResolution(
                metadata.Schema,
                metadata.Table,
                ImmutableArray<string>.Empty);

        throw new InvalidOperationException(
            $"Strict DML row-identity assurance requires a primary key on '{metadata.Schema}.{metadata.Table}'.");
    }

    private static bool ReturnsRows(SqlStatement statement) => statement switch
    {
        InsertStatement insert => !insert.Returning.IsDefaultOrEmpty,
        UpdateStatement update => !update.Returning.IsDefaultOrEmpty,
        DeleteStatement delete => !delete.Returning.IsDefaultOrEmpty,
        _ => false
    };

    private static ImmutableArray<TableSource> MutationSources(SqlStatement statement) => statement switch
    {
        UpdateStatement update when !update.FromSources.IsDefaultOrEmpty => update.FromSources,
        UpdateStatement update => update.From.Cast<TableSource>().ToImmutableArray(),
        DeleteStatement delete when !delete.UsingSources.IsDefaultOrEmpty => delete.UsingSources,
        DeleteStatement delete => delete.Using.Cast<TableSource>().ToImmutableArray(),
        _ => ImmutableArray<TableSource>.Empty
    };

    private static SqlIdentifier TargetIdentityIdentifier(
        NamedTableSource target,
        string column)
    {
        var qualifier = target.Alias ?? target.Name.Parts[^1];
        return new SqlIdentifier(
            ImmutableArray.Create(
                qualifier,
                new IdentifierPart(column, true, SourceSpan.Unknown)),
            SourceSpan.Unknown);
    }

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
                    FromSources = update.FromSources,
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
                    UsingSources = delete.UsingSources,
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
