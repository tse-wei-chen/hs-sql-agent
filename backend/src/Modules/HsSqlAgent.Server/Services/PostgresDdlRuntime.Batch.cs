using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Ddl;
using HsSqlAgent.SqlCore.Enums;
using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// G6/S16 independent multi-DDL proof on the existing PostgreSQL Host.
/// An ordered 2–4 CREATE-only script is ONE human approval and ONE actual DB
/// transaction. No reuse of DML batch or single-DDL approval tokens.
/// </summary>
public sealed partial class PostgresDdlRuntime
{
    private static DdlRuntimeResult BatchRejected(
        string code, string message, DdlBatchRuntimePreview? preview = null) =>
        new(DdlRuntimeStatus.Rejected, null, message, code) { BatchPreview = preview };

    private static DdlRuntimeResult BatchUnknown(
        DdlBatchRuntimePreview preview, string reason) =>
        new(DdlRuntimeStatus.OutcomeUnknown, null,
            reason + " Never retry the batch automatically; reconcile all physical objects.",
            "ddl.batch_outcome_unknown") { BatchPreview = preview };

    private async Task<DdlRuntimeResult> RunBatchAsync(
        ISqlProvider provider, string connectionString, string sql,
        DdlActorContext actor, bool previewOnly,
        IDdlApprovalProvider? approvalProvider, CancellationToken cancellationToken)
    {
        if (!_options.AllowCreateTable || !_options.AllowCreateTableBatch)
            return BatchRejected("ddl.batch_disabled",
                "PostgreSQL atomic DDL batch requires a separate Host opt-in.");
        if (!_options.AllowedBatchPrincipals.Contains(actor.PrincipalIdentity))
            return BatchRejected("ddl.batch_principal_denied",
                "This authenticated MCP key is not separately authorized for DDL batch.");
        if (!previewOnly && approvalProvider is null)
            return BatchRejected("ddl.approval_required",
                "The entire DDL batch requires one independent human approval.");

        CompiledDdlBatchPlan plan;
        string schema;
        try
        {
            // The F# Script dispatcher and closed per-statement DDL compiler
            // prove every member before ANY database connection is opened.
            plan = SqlCoreFacade.CompileDdlBatch(sql,
                new DdlCompilationOptions(SqlAgentToolType.Postgres, SqlAgentToolType.Postgres));
            if (plan.Topology == DdlBatchTopology.DependentCreateChain
                && (!_options.AllowDependentDdlBatch || !_options.AllowAddColumn
                    || (plan.Plans.Any(p => p.Changes[0].Operation == DdlSchemaOperation.CreateIndex)
                        && !_options.AllowCreateIndex)))
                return BatchRejected("ddl.batch_dependency_disabled",
                    "Dependent CREATE/ALTER/INDEX batch requires separately enabled operations.");
            schema = plan.Plans[0].Changes[0].ObjectIdentity.Parts[0];
            if (!_options.AllowedSchemas.Any(allowed => String.Equals(allowed, schema, StringComparison.Ordinal))
                || schema is "pg_catalog" or "information_schema"
                || schema.StartsWith("pg_temp", StringComparison.Ordinal))
                return BatchRejected("ddl.schema_denied",
                    "Atomic DDL batch requires one explicitly allowed user schema.");
            ValidateConfiguration();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BatchRejected("ddl.batch_compile_denied",
                "DDL batch grammar, bounds, capability or Host policy is unproven.");
        }

        try
        {
            VerifiedRuntimeServerProfile profile;
            DdlPhysicalSnapshot[] original;
            await using (var connection = provider.Connections.Create(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                profile = RuntimeServerProfileVerifier.Capture(provider.Type, connection);
                if (profile.TargetProfile.ServerVersion is null
                    || profile.TargetProfile.ServerVersion.Major != 16)
                    return BatchRejected("ddl.version_unproven",
                        "Only PostgreSQL 16 has a verified atomic DDL batch Host capability.");
                var opts = new DdlCompilationOptions(SqlAgentToolType.Postgres,
                    SqlAgentToolType.Postgres, profile.TargetProfile, profile.TargetProfile);
                plan = SqlCoreFacade.CompileDdlBatch(sql, opts);
                if (plan.Topology == DdlBatchTopology.DependentCreateChain
                    && (!_options.AllowDependentDdlBatch || !_options.AllowAddColumn
                        || (plan.Plans.Any(p => p.Changes[0].Operation == DdlSchemaOperation.CreateIndex)
                            && !_options.AllowCreateIndex)))
                    return BatchRejected("ddl.batch_dependency_disabled",
                        "Dependent DDL batch Host operation gates are not all enabled.");

                original = new DdlPhysicalSnapshot[plan.Plans.Count];
                string? cluster = null;
                long? namespaceOid = null;
                for (var i = 0; i < plan.Plans.Count; i++)
                {
                    var name = plan.Plans[i].Changes[0].ObjectIdentity.Parts[1];
                    var before = await ReadSnapshotAsync(
                        connection, null, schema, name, cancellationToken);
                    if (before.DatabaseName != actor.DatabaseName
                        || before.SchemaOid is null
                        || (cluster is not null && before.ServerIdentity != cluster)
                        || (namespaceOid.HasValue && before.SchemaOid != namespaceOid))
                        return BatchRejected("ddl.batch_identity_unproven",
                            "The physical PostgreSQL cluster, database or namespace is inconsistent.");
                    cluster = before.ServerIdentity;
                    namespaceOid = before.SchemaOid;
                    // Every Preview.Before remains ACTUAL physical metadata.
                    // An ALTER/INDEX that depends on this CREATE must *not*
                    // be passed to the live present-table validator yet.
                    var dependent = plan.Topology == DdlBatchTopology.DependentCreateChain;
                    if (dependent && i > 0
                        && before.Fingerprint != original[0].Fingerprint)
                        return BatchRejected("ddl.batch_physical_precondition",
                            "Dependent batch root changed while collecting real metadata.");
                    if (!dependent || i == 0)
                    {
                        var denied = await VerifyPreconditionAsync(
                            connection, null, before, plan.Plans[i], cancellationToken);
                        if (denied is not null)
                            return BatchRejected("ddl.batch_physical_precondition",
                                "One or more ordered batch targets are not proved absent and safe.");
                    }
                    original[i] = before;
                }
                // INDEX is a schema-level relation, not only a table child:
                // check its global namespace collision before approval.
                if (plan.Topology == DdlBatchTopology.DependentCreateChain)
                {
                    var index = plan.Plans.Select(p => p.Changes[0])
                        .FirstOrDefault(c => c.Operation == DdlSchemaOperation.CreateIndex);
                    if (index is not null)
                    {
                        var collision = await ScalarAsync(connection, null,
                            "SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_class c "
                            + "JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace "
                            + "WHERE n.nspname=@schema AND c.relname=@name)",
                            cancellationToken, ("schema", schema), ("name", index.IndexName));
                        if (collision is not false)
                            return BatchRejected("ddl.batch_index_collision",
                                "The dependent INDEX name is not proven absent.");
                    }
                }
            } // Release the preview connection BEFORE interactive approval.

            var steps = plan.Plans.Select(p => SqlCoreFacade.PreviewDdl(
                p.Command.Sql, new DdlCompilationOptions(SqlAgentToolType.Postgres,
                    SqlAgentToolType.Postgres, profile.TargetProfile, profile.TargetProfile))).ToArray();
            // Pure in-memory plan projection, never promoted into the
            // physical Before[] observations nor treated as table-OID proof.
            // The -1 sentinel exists only for the human-readable projected
            // intermediate state and is never used in database queries.
            var expected = new IReadOnlyList<DdlPhysicalColumn>[plan.Plans.Count];
            var descriptions = new string[plan.Plans.Count];
            var virtualState = original[0];
            for (var i = 0; i < plan.Plans.Count; i++)
            {
                var prior = plan.Topology == DdlBatchTopology.DependentCreateChain
                    ? virtualState : original[i];
                var projected = ProjectExpected(prior, plan.Plans[i]);
                expected[i] = projected.Columns;
                descriptions[i] = HumanReadableDiff(prior, projected.Columns,
                    projected.Indexes, plan.Plans[i]);
                if (plan.Topology == DdlBatchTopology.DependentCreateChain)
                    virtualState = prior with
                    {
                        TableOid = -1L, Columns = projected.Columns,
                        Indexes = projected.Indexes
                    };
            }
            var chain = plan.Topology == DdlBatchTopology.DependentCreateChain;
            var readable = chain
                    ? "Atomic PostgreSQL DDL dependent CREATE -> nullable ADD -> optional INDEX batch\n"
                    : "Atomic PostgreSQL DDL batch (2–4 independent CREATE TABLE operations)\n";
            readable += "All statements are one approval and one real PostgreSQL transaction.\n"
                + (chain
                    ? "Projected intermediate Before/After are HYPOTHETICAL, not real table metadata.\n"
                    : "Before/After are conditional; no database changes occur during Preview.\n")
                + "No data loss, implicit commits or partial success claims.\n"
                + string.Join("\n---\n", descriptions.Select((item, index) =>
                    "Step " + (index + 1) + "/" + descriptions.Length + ":\n" + item));
            var now = _time.GetUtcNow();
            var expiry = now + _options.ApprovalLifetime;
            var nonce = "ddl_batch_" + Guid.NewGuid().ToString("N");
            var policy = PolicyMaterial();
            var fingerprint = Digest(JsonSerializer.Serialize(new
            {
                version = "pg-atomic-ddl-batch-approval-v1",
                plan.PlanFingerprint,
                plan.Topology,
                nativeSql = plan.Plans.Select(p => p.Command.Sql).ToArray(),
                before = original.Select(s => s.Fingerprint).ToArray(),
                serverIdentity = original[0].ServerIdentity,
                actor.PrincipalIdentity, actor.TargetIdentity, actor.DatabaseName,
                policy, nonce,
                issuedAt = now.ToUnixTimeMilliseconds(),
                expiresAt = expiry.ToUnixTimeMilliseconds(),
                readable
            }));
            var preview = new DdlBatchRuntimePreview(plan, Array.AsReadOnly(steps),
                Array.AsReadOnly(original), Array.AsReadOnly(expected),
                fingerprint, expiry, nonce, readable);
            if (previewOnly)
                return new DdlRuntimeResult(DdlRuntimeStatus.Preview, null,
                    "Read-only live PostgreSQL atomic DDL batch preview; no statement executed.")
                { BatchPreview = preview };

            // The existing independent DDL Approval interface authorizes
            // the entire immutable batch, never one individual SQL statement.
            var request = new DdlApprovalRequest(
                nonce, fingerprint, plan.PlanFingerprint,
                actor.PrincipalIdentity, actor.TargetIdentity,
                original[0].ServerIdentity, _options.PolicyRevision, now, expiry, readable);
            DdlApprovalDecision decision;
            try
            {
                decision = await approvalProvider!.RequestAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return BatchRejected("ddl.approval_cancelled", "Batch approval was cancelled.", preview);
            }
            if (decision is null
                || decision.RequestId != nonce
                || decision.ApprovalFingerprint != fingerprint)
                return BatchRejected("ddl.approval_mismatch",
                    "DDL batch approval is not bound to the exact physical, ordered plan.", preview);
            if (decision.State == DdlApprovalState.Pending)
                return new DdlRuntimeResult(DdlRuntimeStatus.Pending, null,
                    "Batch approval is pending; the ephemeral challenge cannot resume execution.")
                { BatchPreview = preview };
            if (decision.State != DdlApprovalState.Approved
                || string.IsNullOrWhiteSpace(decision.ApproverIdentity)
                || decision.ApproverIdentity == actor.PrincipalIdentity)
                return BatchRejected("ddl.approval_denied",
                    "DDL batch requires a different independent human approver.", preview);
            if (_time.GetUtcNow() >= expiry)
                return BatchRejected("ddl.approval_expired", "Batch approval expired.", preview);
            if (!_options.Enabled || !_options.AllowCreateTable || !_options.AllowCreateTableBatch
                || (plan.Topology == DdlBatchTopology.DependentCreateChain
                    && (!_options.AllowDependentDdlBatch || !_options.AllowAddColumn
                        || (plan.Plans.Any(p => p.Changes[0].Operation == DdlSchemaOperation.CreateIndex)
                            && !_options.AllowCreateIndex)))
                || !_options.AllowedBatchPrincipals.Contains(actor.PrincipalIdentity)
                || !_options.AllowedSchemas.Any(allowed => String.Equals(allowed, schema, StringComparison.Ordinal))
                || policy != PolicyMaterial())
                return BatchRejected("ddl.batch_policy_changed",
                    "DDL batch operation, principal allowlist or Host policy changed.", preview);

            return await CommitBatchOnceAsync(provider, connectionString, preview, original,
                profile.ServerVersionIdentity, policy, actor, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BatchRejected("ddl.cancelled_before_execute",
                "DDL batch was cancelled before entering its execution transaction.");
        }
        catch (Exception)
        {
            return BatchRejected("ddl.batch_preview_failed",
                "Atomic DDL batch physical proof or preview failed closed.");
        }
    }

    private async Task<DdlRuntimeResult> CommitBatchOnceAsync(
        ISqlProvider provider, string connectionString,
        DdlBatchRuntimePreview preview, DdlPhysicalSnapshot[] approved,
        string approvedVersion, string approvedPolicy,
        DdlActorContext actor, CancellationToken cancellationToken)
    {
        // One aggregate execution deadline, not four independent per-command
        // time windows. Interactive human approval is outside this budget.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.ExecutionTimeout);
        var execToken = deadline.Token;
        DbConnection? connection = null;
        DbTransaction? transaction = null;
        var startedNative = false;
        var startedCommit = false;
        try
        {
            connection = provider.Connections.Create(connectionString);
            await connection.OpenAsync(execToken);
            RuntimeServerProfileVerifier.EnsureMatches(connection, approvedVersion);
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, execToken);
            await ExecuteHostSqlAsync(connection, transaction,
                "SET LOCAL lock_timeout = '3s'", execToken);
            var statementMs = Math.Clamp((int)_options.ExecutionTimeout.TotalMilliseconds, 1, 15000);
            await ExecuteHostSqlAsync(connection, transaction,
                "SET LOCAL statement_timeout = '" + statementMs + "ms'", execToken);

            // Deterministic lock order prevents participating Host workers
            // deadlocking each other. Advisory locks do not exclude external
            // clients; PostgreSQL native CREATE detects external name conflicts.
            foreach (var name in approved.Select(s => s.TableName)
                .OrderBy(n => n, StringComparer.Ordinal))
            {
                await ExecuteHostSqlAsync(connection, transaction,
                    "SELECT pg_advisory_xact_lock(hashtext(@schema), hashtext(@table))",
                    execToken, ("schema", approved[0].SchemaName), ("table", name));
            }

            // Re-prove EACH absent target inside the same transaction. If a
            // concurrent client creates an object after this check, its native
            // CREATE conflict aborts the transaction and we must roll back
            // every earlier CREATE, never report partial success.
            for (var i = 0; i < approved.Length; i++)
            {
                var old = approved[i];
                var fresh = await ReadSnapshotAsync(connection, transaction,
                    old.SchemaName, old.TableName, execToken);
                if (fresh.Fingerprint != old.Fingerprint
                    || fresh.ServerIdentity != old.ServerIdentity
                    || fresh.DatabaseName != actor.DatabaseName
                    || _time.GetUtcNow() >= preview.ExpiresAt
                    || approvedPolicy != PolicyMaterial()
                    || !_options.Enabled || !_options.AllowCreateTable
                    || !_options.AllowCreateTableBatch
                    || (preview.Plan.Topology == DdlBatchTopology.DependentCreateChain
                        && (!_options.AllowDependentDdlBatch || !_options.AllowAddColumn
                            || (preview.Plan.Plans.Any(p => p.Changes[0].Operation == DdlSchemaOperation.CreateIndex)
                                && !_options.AllowCreateIndex)))
                    || !_options.AllowedBatchPrincipals.Contains(actor.PrincipalIdentity)
                    || !_options.AllowedSchemas.Any(allowed =>
                        String.Equals(allowed, old.SchemaName, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Atomic batch precondition or policy drift.");
                // Only the CREATE root exists at transaction start for a
                // dependent chain; later operations prove their real states
                // after the previous native DDL, not against fabricated rows.
                if (preview.Plan.Topology != DdlBatchTopology.DependentCreateChain || i == 0)
                {
                    var denied = await VerifyPreconditionAsync(connection, transaction, fresh,
                        preview.Plan.Plans[i], execToken);
                    if (denied is not null)
                        throw new InvalidOperationException("Atomic batch live precondition was invalidated.");
                }
            }

            DdlPhysicalSnapshot? lastVerified = null;
            for (var i = 0; i < preview.Plan.Plans.Count; i++)
            {
                var plan = preview.Plan.Plans[i];
                var dependent = preview.Plan.Topology == DdlBatchTopology.DependentCreateChain;
                DdlPhysicalSnapshot actualBefore;
                if (dependent && i > 0)
                {
                    actualBefore = await ReadSnapshotAsync(connection, transaction,
                        approved[0].SchemaName, approved[0].TableName, execToken);
                    if (lastVerified is null
                        || actualBefore.Fingerprint != lastVerified.Fingerprint
                        || actualBefore.TableOid != lastVerified.TableOid)
                        throw new InvalidOperationException(
                            "Dependent batch previous staged physical effect changed.");
                    var denied = await VerifyPreconditionAsync(
                        connection, transaction, actualBefore, plan, execToken);
                    if (denied is not null)
                        throw new InvalidOperationException(
                            "Dependent batch next physical operation is unproven.");
                }
                else
                    actualBefore = approved[i];

                // Native SQL originates only from typed F# compiled command.
                startedNative = true;
                await ExecuteHostSqlAsync(connection, transaction,
                    plan.Command.Sql, execToken);
                var after = await ReadSnapshotAsync(connection, transaction,
                    approved[i].SchemaName, approved[i].TableName, execToken);
                if (!MatchesExpected(after, plan, actualBefore))
                    throw new InvalidOperationException("DDL batch typed physical postcondition differs.");
                lastVerified = after;
            }
            // A final whole-batch readback is evidence for the complete set,
            // not just the last successful statement.
            if (preview.Plan.Topology == DdlBatchTopology.DependentCreateChain)
            {
                var final = await ReadSnapshotAsync(connection, transaction,
                    approved[0].SchemaName, approved[0].TableName, execToken);
                if (lastVerified is null || final.Fingerprint != lastVerified.Fingerprint
                    || final.TableOid != lastVerified.TableOid
                    || !final.Columns.SequenceEqual(preview.ExpectedColumns[^1]))
                    throw new InvalidOperationException(
                        "Dependent DDL batch final cumulative schema effect differs.");
            }
            else
            {
                for (var i = 0; i < preview.Plan.Plans.Count; i++)
                {
                    var after = await ReadSnapshotAsync(connection, transaction,
                        approved[i].SchemaName, approved[i].TableName, execToken);
                    if (!MatchesExpected(after, preview.Plan.Plans[i], approved[i]))
                        throw new InvalidOperationException("DDL batch final physical state differs.");
                }
            }

            startedCommit = true;
            await transaction.CommitAsync(execToken);
            return new DdlRuntimeResult(DdlRuntimeStatus.Committed, null,
                "All PostgreSQL DDL batch members committed atomically after physical verification.")
            { BatchPreview = preview };
        }
        catch (Exception)
        {
            // A lost COMMIT response is not proof of rollback. The batch is
            // never retried and no fabricated per-statement success is emitted.
            if (startedCommit)
                return BatchUnknown(preview, "Atomic PostgreSQL batch COMMIT outcome is unproven.");
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return new DdlRuntimeResult(
                        startedNative ? DdlRuntimeStatus.Failed : DdlRuntimeStatus.Rejected,
                        null,
                        startedNative
                            ? "Atomic PostgreSQL DDL batch rolled back after native execution failure."
                            : "DDL batch transaction precondition failed; rollback confirmed.",
                        startedNative ? "ddl.batch_rolled_back" : "ddl.batch_drift")
                    { BatchPreview = preview };
                }
                catch (Exception)
                {
                    return startedNative
                        ? BatchUnknown(preview, "Atomic DDL batch rollback outcome is unproven.")
                        : BatchRejected("ddl.batch_connection_failed",
                            "DDL batch could not enter native execution.", preview);
                }
            }
            return BatchRejected("ddl.batch_connection_failed",
                "DDL batch connection failed before executing native SQL.", preview);
        }
        finally
        {
            if (transaction is not null)
                try { await transaction.DisposeAsync(); } catch (Exception) { }
            if (connection is not null)
                try { await connection.DisposeAsync(); } catch (Exception) { }
        }
    }
}
