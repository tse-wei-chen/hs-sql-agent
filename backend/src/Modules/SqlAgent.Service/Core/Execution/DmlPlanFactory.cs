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

        if (parsedMutation.Statement is MergeStatement merge)
            return await CreateSqlServerMergeMatchedPlanAsync(
                metadataConnection, connectionString, parsedMutation, merge,
                targetProvider, validationContext, compilationPolicy, assurance,
                maxAffectedRows, approvalTtl, cancellationToken, targetProfile);

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
            ExecutionTargetIdentifier(targetProvider, identity.Schema, identity.Table),
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

        // Only a live, already-verified metadata connection can mint this
        // proof; the F# compiler independently checks the entire CTE shape.
        if (_dmlCompiler is null && _queryCompiler is null)
        {
            effectiveValidationContext = SqlServerCteLiveProofDiscovery.AttachIfProven(
                metadataConnection, resolvedMutation, resolvedStatement,
                targetProvider, effectiveValidationContext, targetProfile,
                assurance, maxAffectedRows, cancellationToken);
        }

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
            MutationCtes(resolvedStatement),
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

        var boundProof = effectiveValidationContext.SqlServerCteUniqueKeyAssurance;
        var fingerprintPolicy = boundProof?.TargetKeyColumn is not null
            ? validationContext.PolicyVersion + "|live-sqlserver-cte-exact-integer=" + boundProof.ProofIdentity
            : validationContext.PolicyVersion;
        var fingerprint = DmlFingerprintService.ComputePlanFingerprint(
            mutationCommand, fingerprintPolicy);

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
            maxAffectedRows,
            SchemaSnapshot: validationContext.SchemaSnapshot)
        {
            SqlServerCteUniqueKeyAssurance = boundProof
        };
    }

    private async Task<ValidatedDmlPlan> CreateSqlServerMergeMatchedPlanAsync(
        DbConnection? metadataConnection,
        string connectionString,
        ParsedStatement parsedMutation,
        MergeStatement merge,
        SqlAgentToolType targetProvider,
        SqlPlanValidationContext validationContext,
        DmlCompilationPolicy? compilationPolicy,
        DmlRowIdentityAssurance assurance,
        int maxAffectedRows,
        TimeSpan? approvalTtl,
        CancellationToken cancellationToken,
        SqlProviderCapabilityProfile? targetProfile)
    {
        // SQL Server MERGE is not a normal INSERT or UPDATE: its existing-row
        // effect must be approved through an immutable match query. No custom
        // driver, synthetic catalog or legacy compatibility compiler can mint
        // this one exceptionally narrow authority.
        var shape = SqlServerMergeMatchedDeleteSafety.RequireShape(
            merge, parsedMutation.SourceDialect, targetProvider, maxAffectedRows);
        SqlServerMergeMatchedDeleteSafety.RequireExactSourceLiterals(
            merge, shape, parsedMutation.RawSql);
        if (assurance != DmlRowIdentityAssurance.Strict
            || metadataConnection is null
            || metadataConnection.State != System.Data.ConnectionState.Open
            || _dmlCompiler is not null || _queryCompiler is not null
            || _dmlPlanningMetadataReader is not HsSqlAgent.Provider.SqlServer.MsSqlServerProvider)
            throw new NotSupportedException(
                "SQL Server MERGE Host approval requires the built-in live provider, strict row identity and verified metadata connection.");

        var snapshot = validationContext.SchemaSnapshot;
        if (snapshot is null
            || snapshot.Provider != SqlAgentToolType.MsSqlServer
            || snapshot.Tables.Count != 1
            || !snapshot.Version.StartsWith(
                "native-sqlserver2022-insert-source-v1:", StringComparison.Ordinal)
            || !string.Equals(snapshot.Tables[0].Name,
                shape.PhysicalTable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "SQL Server MERGE approval requires the complete actual SQL Server 2022 table/index/trigger schema digest.");
        if (validationContext.AllowedTables is { } grants
            && !grants.Any(table => string.Equals(
                table, shape.PhysicalTable, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException(
                "SQL Server MERGE target is not in the authorized physical table scope.");

        // A physical INT column and the sole enforced simple PRIMARY key are
        // proved independently. A plain UNIQUE, composite, implicit cast,
        // identity key or user-defined alias type cannot authorize deletion.
        var live = await new DmlUniqueKeyResolver(
            new HsSqlAgent.Provider.SqlServer.MsSqlServerProvider()).ResolveAsync(
                connectionString, shape.Schema, shape.Table,
                [shape.KeyColumn], cancellationToken);
        if (!live.IsSoleEnforcedUniqueKey || live.HasUnsupportedEnforcedUniqueKeys
            || !live.MatchedKey.IsSimpleEnforcedColumnKey
            || !live.MatchedKey.IsPrimaryKey
            || live.MatchedKey.Columns.Count != 1
            || !string.Equals(live.MatchedKey.Columns[0], shape.KeyColumn,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "SQL Server MERGE requires the sole exact physical, non-filtered INT PRIMARY key.");

        // Reuse the existing one-round-trip live physical INT check for
        // both the primary key and every direct UPDATE payload field.
        var dataCells = shape.IsInsert ? shape.InsertedColumns : shape.UpdatedColumns;
        var checkedColumns = new[] { shape.KeyColumn }
            .Concat(dataCells.Select(cell => cell.Column)).ToArray();
        if (shape.IsInsert
            && (snapshot.Tables[0].Columns.Count != checkedColumns.Length
                || snapshot.Tables[0].Columns.Any(name =>
                    !checkedColumns.Contains(name, StringComparer.Ordinal))))
            throw new InvalidOperationException(
                "SQL Server MERGE INSERT requires a direct value for every physical target column; no defaults or omitted fields.");
        await using (var typeCheck = metadataConnection.CreateCommand())
        {
            typeCheck.CommandText = """
                SELECT c.name FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.columns c ON c.object_id = t.object_id
                WHERE s.name = @schema AND t.name = @table
                  AND c.name IN (@name0,@name1,@name2,@name3)
                  AND c.system_type_id = 56 AND c.user_type_id = 56
                  AND c.is_nullable = 0 AND c.is_identity = 0 AND c.is_computed = 0
                  AND c.is_hidden = 0 AND c.generated_always_type = 0
                """;
            void Bind(string name, object value)
            {
                var parameter = typeCheck.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                typeCheck.Parameters.Add(parameter);
            }
            Bind("@schema", shape.Schema);
            Bind("@table", shape.Table);
            for (var i = 0; i < 4; i++)
                Bind("@name" + i, checkedColumns[Math.Min(i, checkedColumns.Length - 1)]);
            var proved = new HashSet<string>(StringComparer.Ordinal);
            await using var found = await typeCheck.ExecuteReaderAsync(cancellationToken);
            while (await found.ReadAsync(cancellationToken))
                if (!proved.Add(found.GetString(0)))
                    throw new InvalidOperationException(
                        "SQL Server MERGE physical INT column proof is ambiguous.");
            if (proved.Count != checkedColumns.Length
                || checkedColumns.Any(column => !proved.Contains(column)))
                throw new InvalidOperationException(
                    "SQL Server MERGE requires physically exact non-null native INT primary key " +
                    "and update payload columns, without aliases, identity or implicit conversions.");
        }

        var targetName = ExecutionTargetIdentifier(
            SqlAgentToolType.MsSqlServer, shape.Schema, shape.Table);
        var resolvedMerge = new MergeStatement(
            new NamedTableSource(targetName, merge.Target.Alias, merge.Target.Span),
            merge.SourceAlias, merge.SourceColumns, merge.SourceValues,
            merge.MatchPredicate, merge.Matched, merge.NotMatched, merge.Span);
        var command = SqlCoreFacade.CompileDml(
            CloneParsedWithStatement(parsedMutation, resolvedMerge),
            SqlAgentToolType.MsSqlServer, validationContext,
            compilationPolicy, targetProfile,
            conflictTargetAssurance: live.ToConflictTargetAssurance());
        if (command.Kind != SqlStatementKind.Merge
            || !command.Sql.Contains("WITH (HOLDLOCK)", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "SQL Server MERGE compiler did not preserve the native HOLDLOCK key-range contract.");

        // Preview fingerprints the physical primary key AND every before-value
        // that may be replaced, not merely row presence. The compiled MERGE
        // can execute only if the approved full value set remains unchanged.
        var matchColumns = new[] { shape.KeyColumn }
            .Concat(shape.UpdatedColumns.Select(cell => cell.Column))
            .ToImmutableArray();
        var key = MetadataIdentifier(shape.KeyColumn);
        var predicate = new BinaryExpr(
            new ColumnExpr(key, SourceSpan.Unknown), "=",
            new LiteralExpr(shape.KeyValue, SourceSpan.Unknown),
            SourceSpan.Unknown);
        var selectItems = matchColumns.Select(column =>
            new SelectItem(
                new ColumnExpr(MetadataIdentifier(column), SourceSpan.Unknown),
                null, SourceSpan.Unknown)).ToImmutableArray();
        var matchSelect = new SelectStatement(
            ImmutableArray<CteDefinition>.Empty, false,
            selectItems,
            new NamedTableSource(targetName, null, SourceSpan.Unknown),
            ImmutableArray<JoinSource>.Empty, predicate,
            ImmutableArray<SqlExpr>.Empty, null,
            ImmutableArray<OrderByItem>.Empty,
            2, null, SourceSpan.Unknown);
        var match = CompileMatchQuery(
            CloneParsedWithStatement(parsedMutation, matchSelect),
            SqlAgentToolType.MsSqlServer, validationContext, targetProfile);
        var fingerprint = DmlFingerprintService.ComputePlanFingerprint(
            command, validationContext.PolicyVersion);
        var matchFingerprint = DmlFingerprintService.ComputePlanFingerprint(
            match, validationContext.PolicyVersion);
        var insertedValues = shape.IsInsert
            ? ImmutableArray.Create(
                new[] { new SqlServerMergeMatchedDeleteSafety.UpdatedInt(
                    shape.KeyColumn, shape.KeyValue) }
                    .Concat(shape.InsertedColumns)
                    .ToImmutableDictionary(item => item.Column,
                        item => (object?)item.Value, StringComparer.Ordinal))
            : ImmutableArray<ImmutableDictionary<string, object?>>.Empty;

        return new ValidatedDmlPlan(
            shape.IsInsert ? DmlOperation.Insert
                : shape.IsUpdate ? DmlOperation.Update : DmlOperation.Delete,
            shape.PhysicalTable,
            command,
            match,
            matchColumns,
            DmlRowIdentityAssurance.Strict,
            fingerprint,
            validationContext.PolicyVersion,
            approvalTtl.GetValueOrDefault(TimeSpan.FromMinutes(5)),
            maxAffectedRows,
            shape.IsInsert
                ? DmlApprovalMode.SqlServerMergeNotMatchedInsert
                : shape.IsUpdate
                    ? DmlApprovalMode.SqlServerMergeMatchedUpdate
                    : DmlApprovalMode.SqlServerMergeMatchedDelete,
            InsertRows: insertedValues,
            SchemaSnapshot: snapshot)
        {
            SqlServerMergeMatchedDeleteProof = shape.IsInsert || shape.IsUpdate ? null :
                new SqlServerMergeMatchedDeleteProof(
                    shape.PhysicalTable, shape.KeyColumn, shape.KeyValue,
                    fingerprint, matchFingerprint, snapshot.Sha256),
            SqlServerMergeMatchedUpdateProof = !shape.IsUpdate ? null :
                new SqlServerMergeMatchedUpdateProof(
                    shape.PhysicalTable, shape.KeyColumn, shape.KeyValue,
                    shape.UpdatedColumns.Select(cell => cell.Column).ToImmutableArray(),
                    shape.UpdatedColumns.Select(cell => cell.Value).ToImmutableArray(),
                    fingerprint, matchFingerprint, snapshot.Sha256),
            SqlServerMergeNotMatchedInsertProof = !shape.IsInsert ? null :
                new SqlServerMergeNotMatchedInsertProof(
                    shape.PhysicalTable, shape.KeyColumn, shape.KeyValue,
                    checkedColumns.ToImmutableArray(),
                    new[] { shape.KeyValue }.Concat(
                        shape.InsertedColumns.Select(cell => cell.Value)).ToImmutableArray(),
                    fingerprint, matchFingerprint, snapshot.Sha256)
        };
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
        // Closed structural admission comes before any metadata read or SQL compilation.
        // DO NOTHING never inherits the insert-only optimistic payload approval.
        var conflictUpdate = insert.Conflict?.Action == InsertConflictActionKind.UpdateProposedValues;
        var conflictShape = insert.Conflict is null
            ? null
            : NativeSingleRowConflictSafety.RequireShape(
                insert, parsedMutation.SourceDialect, targetProvider, maxAffectedRows,
                update: conflictUpdate);
        if (conflictUpdate)
            NativeSingleRowConflictSafety.RequireExactIntegerSourceTokens(
                insert, conflictShape!, parsedMutation.RawSql);
        (SqlStatement Query, ImmutableArray<string> OutputColumns, string PhysicalSource)? sourceSelection = null;
        if (insert.Source is InsertQuerySource)
            sourceSelection = NativeInsertSelectSafety.RequireShape(
                insert, parsedMutation.SourceDialect, targetProvider, maxAffectedRows);
        else if (insert.Source is not InsertValuesSource)
            throw new NotSupportedException("Unsupported INSERT source: missing source-row approval proof.");

        // Independent source READ grant: enforce BEFORE any physical metadata
        // or F# compile step. The typed runtime repeats this at its outer
        // boundary to avoid even unauthorized source-catalog inspection.
        if (sourceSelection is { } admission
            && validationContext.AllowedTables is { } grants
            && !grants.Any(table => string.Equals(table, admission.PhysicalSource,
                targetProvider == SqlAgentToolType.Postgres
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException(
                "INSERT SELECT physical source table is not in the authorized table scope.");

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
            ExecutionTargetIdentifier(targetProvider, targetResolution.Schema, targetResolution.Table),
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
        if (sourceSelection is { } selection)
        {
            // PostgreSQL quoted identifiers are case-sensitive; SQLite physical
            // table identity is case-insensitive. Do not coalesce two PG tables.
            var physicalComparison = targetProvider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL
                ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var snapshot = validationContext.SchemaSnapshot;
            if (snapshot is null || snapshot.Provider != targetProvider
                || targetProvider is not (SqlAgentToolType.Sqlite or SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer)
                || snapshot.Tables.Count is < 1 or > 2
                || !string.Equals(snapshot.Tables[0].Name,
                    targetResolution.QualifiedTableName, physicalComparison)
                || (string.Equals(selection.PhysicalSource,
                        targetResolution.QualifiedTableName, physicalComparison)
                    ? snapshot.Tables.Count != 1
                      || (targetProvider == SqlAgentToolType.MsSqlServer
                          && !snapshot.Version.StartsWith(
                              "native-sqlserver2022-insert-source-v1:", StringComparison.Ordinal))
                      || (targetProvider == SqlAgentToolType.Postgres
                          && !snapshot.Version.StartsWith(
                              "native-postgres-insert-source-v1:", StringComparison.Ordinal))
                      || (targetProvider == SqlAgentToolType.MySQL
                          && !snapshot.Version.StartsWith(
                              "native-mysql84-innodb-source-v2:", StringComparison.Ordinal))
                    : snapshot.Tables.Count != 2
                      || (targetProvider == SqlAgentToolType.MsSqlServer
                          && !snapshot.Version.StartsWith(
                              "native-sqlserver2022-dual-source-v1:", StringComparison.Ordinal))
                      || (targetProvider == SqlAgentToolType.MySQL
                          && !snapshot.Version.StartsWith(
                              "native-mysql84-dual-source-v1:", StringComparison.Ordinal))
                      || !string.Equals(snapshot.Tables[1].Name, selection.PhysicalSource,
                          physicalComparison)))
                throw new InvalidOperationException(
                    "Native INSERT SELECT requires independently attested physical source and target schema snapshots.");

            var select = NativeInsertSelectSafety.PreviewQuery(
                selection.Query, selection.OutputColumns);
            var match = CompileMatchQuery(
                CloneParsedWithStatement(parsedMutation, select), targetProvider,
                validationContext, targetProfile, maxAffectedRows + 1);
            var sourcePlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(
                mutationCommand, validationContext.PolicyVersion);

            return new ValidatedDmlPlan(
                DmlOperation.Insert,
                targetResolution.QualifiedTableName,
                mutationCommand,
                MatchQueryCommand: match,
                RowIdentityColumns: selection.OutputColumns,
                RowIdentityAssurance: DmlRowIdentityAssurance.Strict,
                PlanFingerprint: sourcePlanFingerprint,
                PolicyVersion: validationContext.PolicyVersion,
                ApprovalTtl: approvalTtl.GetValueOrDefault(TimeSpan.FromMinutes(5)),
                MaxAffectedRows: maxAffectedRows,
                ApprovalMode: DmlApprovalMode.InsertSelect,
                SchemaSnapshot: snapshot)
            {
                NativeInsertSelectProof = new NativeInsertSelectProof(
                    targetResolution.QualifiedTableName,
                    selection.PhysicalSource,
                    sourcePlanFingerprint,
                    DmlFingerprintService.ComputePlanFingerprint(
                        match, validationContext.PolicyVersion),
                    snapshot.Sha256,
                    selection.OutputColumns)
            };
        }

        var values = (InsertValuesSource)insert.Source;
        var previewRows = BuildInsertPreviewRows(
            resolvedInsert, values, allowExactSignedIntegers: conflictUpdate);
        if (conflictShape is { } skip)
        {
            var snapshot = validationContext.SchemaSnapshot;
            if (snapshot is null
                || snapshot.Provider != targetProvider
                || targetProvider is not (SqlAgentToolType.Sqlite or SqlAgentToolType.Postgres)
                || snapshot.Tables.Count != 1
                || !(targetProvider == SqlAgentToolType.Sqlite
                    ? snapshot.Version.StartsWith("native-dml-schema-v2:", StringComparison.Ordinal)
                    : snapshot.Version.StartsWith("native-postgres-insert-source-v1:", StringComparison.Ordinal))
                || !string.Equals(snapshot.Tables[0].Name,
                    targetResolution.QualifiedTableName,
                    targetProvider == SqlAgentToolType.Postgres
                        ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Native conflict skip requires a live full-definition target schema snapshot bound to the exact physical table.");

            // The native provider, never a caller supplied virtual catalog, attests
            // the sole simple enforced physical UNIQUE key. PostgreSQL further
            // seals its complete index and trigger definitions in the snapshot.
            IProviderMetadataReader nativeKeys = targetProvider == SqlAgentToolType.Sqlite
                ? new HsSqlAgent.Provider.Sqlite.SqliteProvider()
                : new HsSqlAgent.Provider.PostgreSql.PostgresProvider();
            var live = await new DmlUniqueKeyResolver(nativeKeys).ResolveAsync(
                connectionString, targetResolution.Schema, targetResolution.Table,
                [skip.UniqueKeyColumn], cancellationToken);
            if (!live.IsSoleEnforcedUniqueKey || live.HasUnsupportedEnforcedUniqueKeys
                || !live.MatchedKey.IsSimpleEnforcedColumnKey || live.MatchedKey.Columns.Count != 1)
                throw new InvalidOperationException(
                    "Native conflict skip requires the sole simple enforced UNIQUE key; multiple, partial and expression keys remain fail-closed.");
            if (targetProvider == SqlAgentToolType.Postgres
                && !string.Equals(live.MatchedKey.Columns[0], skip.UniqueKeyColumn,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "PostgreSQL conflict target requires exact case-sensitive physical UNIQUE column identity.");

            if (metadataConnection is null)
                throw new InvalidOperationException(
                    "Native conflict skip requires the live Host metadata connection; detached proof cannot authorize a plan.");
            if (targetProvider == SqlAgentToolType.Postgres)
            {
                // For PostgreSQL, restrict the exact literal to an actual
                // NOT NULL integer-family column. Never silently authorize
                // a text/UUID/collation/implicit-cast comparison.
                await using var typeCheck = metadataConnection.CreateCommand();
                typeCheck.CommandText = """
                    SELECT count(*) FROM pg_catalog.pg_attribute a
                    JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
                    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = @schema AND c.relname = @table
                      AND a.attname = @key AND a.attnum > 0
                      AND NOT a.attisdropped AND a.attnotnull
                      AND a.atttypid IN (20, 21, 23)
                      AND c.relkind = 'r' AND c.relpersistence = 'p'
                      AND EXISTS (
                          SELECT 1 FROM pg_catalog.pg_index i
                          JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid
                          WHERE i.indrelid = c.oid AND idx.relname = @indexName
                            AND i.indisunique AND i.indimmediate
                            AND i.indisvalid AND i.indisready AND i.indislive
                            AND i.indpred IS NULL
                      )
                    """;
                foreach (var (name, value) in new[]
                {
                    ("schema", targetResolution.Schema),
                    ("table", targetResolution.Table),
                    ("key", skip.UniqueKeyColumn),
                    ("indexName", live.MatchedKey.Name)
                })
                {
                    var parameter = typeCheck.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    typeCheck.Parameters.Add(parameter);
                }
                if (Convert.ToInt64(await typeCheck.ExecuteScalarAsync(cancellationToken)) != 1)
                    throw new InvalidOperationException(
                        "PostgreSQL conflict skip requires a real NOT NULL INT2/INT4/INT8 immediately enforced UNIQUE key without implicit casts.");
            }
            if (conflictUpdate)
            {
                // Do not let arbitrary table defaults, CHECK/FK policies or
                // additional columns participate in the one-row before/after
                // claim. PostgreSQL's full catalog proof separately denies
                // user triggers, partitioning, inheritance, rules and RLS.
                await using var shapeCheck = metadataConnection.CreateCommand();
                shapeCheck.CommandText = """
                    SELECT count(*) = @expectedCount
                       AND count(*) FILTER (WHERE a.attname = @key
                           AND a.atttypid = 20 AND a.attnotnull
                           AND a.attidentity = '' AND a.attgenerated = '') = 1
                       AND count(*) FILTER (WHERE a.attname::text = ANY(@updatedColumns)
                           AND a.attidentity = '' AND a.attgenerated = ''
                           AND (
                               (a.atttypid = 25
                                   AND a.attname::text = ANY(@textColumns))
                               OR (a.atttypid = 21
                                   AND a.attname::text = ANY(@smallintColumns))
                               OR (a.atttypid = 23
                                   AND a.attname::text = ANY(@integerColumns))
                               OR (a.atttypid = 20
                                   AND a.attname::text = ANY(@bigintColumns))
                               OR (a.atttypid IN (20,21,23,25)
                                   AND a.attname::text = ANY(@nullColumns)
                                   AND NOT a.attnotnull)
                           )) = @updatedCount
                       AND (SELECT count(*) FROM pg_catalog.pg_index idx
                            WHERE idx.indrelid = c.oid) = 1
                       AND NOT EXISTS (
                           SELECT 1 FROM pg_catalog.pg_attrdef d WHERE d.adrelid = c.oid)
                       AND NOT EXISTS (
                           SELECT 1 FROM pg_catalog.pg_constraint con
                           WHERE con.conrelid = c.oid AND con.contype NOT IN ('p','u'))
                    FROM pg_catalog.pg_class c
                    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                    JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
                    WHERE n.nspname = @schema AND c.relname = @table
                      AND a.attnum > 0 AND NOT a.attisdropped
                    GROUP BY c.oid
                    """;
                var proofs = skip.ProposedValues;
                if (proofs.IsDefaultOrEmpty
                    || proofs.Length != skip.UpdatedColumns.Length)
                    throw new InvalidOperationException(
                        "PostgreSQL typed conflict UPDATE is missing exact proposed-value proof.");
                var integerProofs = proofs.Where(value =>
                    value.Kind == NativeSingleRowConflictSafety.ProposedKind.ExactInteger).ToArray();
                var textColumns = proofs.Where(value =>
                    value.Kind == NativeSingleRowConflictSafety.ProposedKind.Text)
                    .Select(value => value.Column).ToArray();
                var nullColumns = proofs.Where(value =>
                    value.Kind == NativeSingleRowConflictSafety.ProposedKind.SqlNull)
                    .Select(value => value.Column).ToArray();
                var smallintColumns = integerProofs.Where(value =>
                    value.Integer >= short.MinValue && value.Integer <= short.MaxValue)
                    .Select(value => value.Column).ToArray();
                var integerColumns = integerProofs.Where(value =>
                    value.Integer >= int.MinValue && value.Integer <= int.MaxValue)
                    .Select(value => value.Column).ToArray();
                var bigintColumns = integerProofs.Select(value => value.Column).ToArray();
                foreach (var (name, value) in new (string, object)[]
                {
                    ("schema", targetResolution.Schema),
                    ("table", targetResolution.Table),
                    ("key", skip.UniqueKeyColumn),
                    ("expectedCount", skip.UpdatedColumns.Length + 1),
                    ("updatedCount", skip.UpdatedColumns.Length),
                    ("updatedColumns", skip.UpdatedColumns.ToArray()),
                    ("textColumns", textColumns),
                    ("nullColumns", nullColumns),
                    ("smallintColumns", smallintColumns),
                    ("integerColumns", integerColumns),
                    ("bigintColumns", bigintColumns)
                })
                {
                    var parameter = shapeCheck.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    shapeCheck.Parameters.Add(parameter);
                }
                if (await shapeCheck.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new InvalidOperationException(
                        "PostgreSQL conflict UPDATE requires a BIGINT UNIQUE key and 1-4 exactly typed TEXT/SMALLINT/INTEGER/BIGINT value columns, with NULL only on nullable physical fields, and no extra indexes, defaults, CHECK or foreign keys.");
            }

            // SQLite reports only the top-level INSERT row count. A trigger
            // can mutate additional tables without being represented by that
            // count. PostgreSQL's full-definition catalog proof already rejects
            // all user triggers, rules, partitioning and RLS.
            if (targetProvider == SqlAgentToolType.Sqlite)
            await using (var triggers = metadataConnection.CreateCommand())
            {
                triggers.CommandText =
                    "SELECT COUNT(*) FROM main.sqlite_master " +
                    "WHERE type = 'trigger' AND tbl_name = @target COLLATE NOCASE";
                var targetName = triggers.CreateParameter();
                targetName.ParameterName = "@target";
                targetName.Value = targetResolution.Table;
                triggers.Parameters.Add(targetName);
                var triggerCount = Convert.ToInt64(
                    await triggers.ExecuteScalarAsync(cancellationToken));
                if (triggerCount != 0)
                    throw new InvalidOperationException(
                        "SQLite conflict skip cannot approve a trigger-bearing physical target: " +
                        "trigger side effects are outside the one-row affected-count proof.");
            }

            var column = SqlIdentifier.Unquoted(skip.UniqueKeyColumn, SourceSpan.Unknown);
            var keyExpr = new ColumnExpr(column, SourceSpan.Unknown);
            var predicate = new BinaryExpr(
                keyExpr, "=", new LiteralExpr(skip.UniqueKeyValue, SourceSpan.Unknown),
                SourceSpan.Unknown);
            var selections = ImmutableArray.CreateBuilder<SelectItem>(
                1 + (conflictUpdate ? skip.UpdatedColumns.Length : 0));
            selections.Add(new SelectItem(keyExpr, null, SourceSpan.Unknown));
            if (conflictUpdate)
                foreach (var updatedColumn in skip.UpdatedColumns)
                    selections.Add(new SelectItem(new ColumnExpr(
                        SqlIdentifier.Unquoted(updatedColumn, SourceSpan.Unknown),
                        SourceSpan.Unknown), null, SourceSpan.Unknown));
            var select = new SelectStatement(
                ImmutableArray<CteDefinition>.Empty, false,
                selections.MoveToImmutable(),
                resolvedTarget, ImmutableArray<JoinSource>.Empty, predicate,
                ImmutableArray<SqlExpr>.Empty, null,
                ImmutableArray<OrderByItem>.Empty,
                1, null, SourceSpan.Unknown);
            var match = CompileMatchQuery(
                new ParsedStatement(select, parsedMutation.SourceDialect),
                targetProvider, validationContext, targetProfile);
            var skipFingerprint = DmlFingerprintService.ComputePlanFingerprint(
                mutationCommand, validationContext.PolicyVersion);
            return new ValidatedDmlPlan(
                DmlOperation.Insert, targetResolution.QualifiedTableName,
                mutationCommand, match, conflictUpdate
                    ? ImmutableArray.Create(skip.UniqueKeyColumn).AddRange(skip.UpdatedColumns)
                    : [skip.UniqueKeyColumn],
                DmlRowIdentityAssurance.Strict, skipFingerprint,
                validationContext.PolicyVersion,
                approvalTtl.GetValueOrDefault(TimeSpan.FromMinutes(5)),
                maxAffectedRows, conflictUpdate
                    ? DmlApprovalMode.InsertConflictDoUpdate
                    : DmlApprovalMode.InsertConflictDoNothing,
                InsertRows: previewRows, SchemaSnapshot: snapshot)
            {
                NativeConflictDoNothingProof = conflictUpdate ? null :
                    new NativeConflictDoNothingProof(
                        targetResolution.QualifiedTableName, skip.UniqueKeyColumn,
                        skipFingerprint,
                        DmlFingerprintService.ComputePlanFingerprint(
                            match, validationContext.PolicyVersion),
                        snapshot.Sha256),
                NativeConflictDoUpdateProof = conflictUpdate
                    ? new NativeConflictDoUpdateProof(
                        targetResolution.QualifiedTableName, skip.UniqueKeyColumn,
                        skip.UpdatedColumns, skipFingerprint,
                        DmlFingerprintService.ComputePlanFingerprint(
                            match, validationContext.PolicyVersion),
                        snapshot.Sha256)
                    : null
            };
        }

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
            InsertRows: previewRows,
            SchemaSnapshot: validationContext.SchemaSnapshot);
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

        // Preserve the same immutable schema proof while adding SQL Server trigger assurance.
        return validationContext.WithDmlResultRowAssurance(
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

    private static ImmutableArray<CteDefinition> MutationCtes(SqlStatement statement) => statement switch
    {
        UpdateStatement update => update.Ctes,
        DeleteStatement delete => delete.Ctes,
        _ => ImmutableArray<CteDefinition>.Empty
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
        SqlProviderCapabilityProfile? targetProfile,
        int? queryMaxRows = null)
    {
        var queryPolicy = queryMaxRows.HasValue
            ? new SqlExecutionPlanPolicy(queryMaxRows.Value)
            : new SqlExecutionPlanPolicy();
        if (_queryCompiler is not null)
        {
            return _queryCompiler.Compile(
                parsed,
                targetProvider,
                validationContext,
                queryPolicy,
                targetProfile);
        }

        return SqlCoreFacade.CompileQuery(
            parsed,
            targetProvider,
            validationContext,
            queryPolicy,
            targetProfile);
    }

    private static ImmutableArray<ImmutableDictionary<string, object?>> BuildInsertPreviewRows(
        InsertStatement insert,
        InsertValuesSource values,
        bool allowExactSignedIntegers = false)
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
                object? literalValue;
                if (row[columnIndex] is LiteralExpr literal)
                    literalValue = NormalizeInsertPreviewValue(literal.Value);
                else if (allowExactSignedIntegers
                    && NativeSingleRowConflictSafety.ExactIntegerLiteral(row[columnIndex])
                        is long exactSignedInteger)
                    literalValue = exactSignedInteger;
                else
                    throw new InvalidOperationException(
                        $"INSERT VALUES preview requires canonical scalar literals, not {row[columnIndex].GetType().Name}.");

                builder.Add(IdentifierText(insert.Columns[columnIndex]), literalValue);
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
                    Ctes = update.Ctes,
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
                    Ctes = delete.Ctes,
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

    // Firebird has no SQL schema namespace. 'Default' is a metadata-only
    // pseudo-schema, not an executable SQL qualifier. Retain that identity
    // for audit/authorization but render only the physical relation name.
    private static SqlIdentifier ExecutionTargetIdentifier(
        SqlAgentToolType provider, string schema, string table) =>
        provider == SqlAgentToolType.Firebird
            && string.Equals(schema, "Default", StringComparison.OrdinalIgnoreCase)
                ? MetadataIdentifier(table)
                : MetadataIdentifier(schema, table);

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
