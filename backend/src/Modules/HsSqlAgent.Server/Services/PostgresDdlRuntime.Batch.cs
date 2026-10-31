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
                    var denied = await VerifyPreconditionAsync(
                        connection, null, before, plan.Plans[i], cancellationToken);
                    if (denied is not null)
                        return BatchRejected("ddl.batch_physical_precondition",
                            "One or more ordered batch targets are not proved absent and safe.");
                    original[i] = before;
                }
            } // Release the preview connection BEFORE interactive approval.

            var steps = plan.Plans.Select(p => SqlCoreFacade.PreviewDdl(
                p.Command.Sql, new DdlCompilationOptions(SqlAgentToolType.Postgres,
                    SqlAgentToolType.Postgres, profile.TargetProfile, profile.TargetProfile))).ToArray();
            var expected = plan.Plans.Select((p, i) =>
                (IReadOnlyList<DdlPhysicalColumn>)ProjectExpected(original[i], p).Columns).ToArray();
            var descriptions = plan.Plans.Select((p, i) => HumanReadableDiff(
                original[i], expected[i], Array.Empty<DdlPhysicalIndex>(), p)).ToArray();
            var readable = "Atomic PostgreSQL DDL batch (2–4 independent CREATE TABLE operations)\n"
                + "All statements are one approval and one real PostgreSQL transaction.\n"
                + "Before/After are conditional; no database changes occur during Preview.\n"
                + "No cross-statement references, data loss, implicit commits or partial success claims.\n"
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
        DbConnection? connection = null;
        DbTransaction? transaction = null;
        var startedNative = false;
        var startedCommit = false;
        try
        {
            connection = provider.Connections.Create(connectionString);
            await connection.OpenAsync(cancellationToken);
            RuntimeServerProfileVerifier.EnsureMatches(connection, approvedVersion);
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            await ExecuteHostSqlAsync(connection, transaction,
                "SET LOCAL lock_timeout = '3s'", cancellationToken);
            var statementMs = Math.Clamp((int)_options.ExecutionTimeout.TotalMilliseconds, 1, 15000);
            await ExecuteHostSqlAsync(connection, transaction,
                "SET LOCAL statement_timeout = '" + statementMs + "ms'", cancellationToken);

            // Deterministic lock order prevents participating Host workers
            // deadlocking each other. Advisory locks do not exclude external
            // clients; PostgreSQL native CREATE detects external name conflicts.
            foreach (var name in approved.Select(s => s.TableName)
                .OrderBy(n => n, StringComparer.Ordinal))
            {
                await ExecuteHostSqlAsync(connection, transaction,
                    "SELECT pg_advisory_xact_lock(hashtext(@schema), hashtext(@table))",
                    cancellationToken, ("schema", approved[0].SchemaName), ("table", name));
            }

            // Re-prove EACH absent target inside the same transaction. If a
            // concurrent client creates an object after this check, its native
            // CREATE conflict aborts the transaction and we must roll back
            // every earlier CREATE, never report partial success.
            for (var i = 0; i < approved.Length; i++)
            {
                var old = approved[i];
                var fresh = await ReadSnapshotAsync(connection, transaction,
                    old.SchemaName, old.TableName, cancellationToken);
                if (fresh.Fingerprint != old.Fingerprint
                    || fresh.ServerIdentity != old.ServerIdentity
                    || fresh.DatabaseName != actor.DatabaseName
                    || _time.GetUtcNow() >= preview.ExpiresAt
                    || approvedPolicy != PolicyMaterial()
                    || !_options.Enabled || !_options.AllowCreateTable
                    || !_options.AllowCreateTableBatch
                    || !_options.AllowedBatchPrincipals.Contains(actor.PrincipalIdentity)
                    || !_options.AllowedSchemas.Any(allowed =>
                        String.Equals(allowed, old.SchemaName, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Atomic batch precondition or policy drift.");
                var denied = await VerifyPreconditionAsync(connection, transaction, fresh,
                    preview.Plan.Plans[i], cancellationToken);
                if (denied is not null)
                    throw new InvalidOperationException("Atomic batch live precondition was invalidated.");
            }

            for (var i = 0; i < preview.Plan.Plans.Count; i++)
            {
                var plan = preview.Plan.Plans[i];
                // Native SQL originates only from typed F# compiled command.
                startedNative = true;
                await ExecuteHostSqlAsync(connection, transaction,
                    plan.Command.Sql, cancellationToken);
                var fresh = await ReadSnapshotAsync(connection, transaction,
                    approved[i].SchemaName, approved[i].TableName, cancellationToken);
                if (!MatchesExpected(fresh, plan, approved[i]))
                    throw new InvalidOperationException("DDL batch typed physical postcondition differs.");
            }
            // A final whole-batch readback is evidence for the complete set,
            // not just the last successful statement.
            for (var i = 0; i < preview.Plan.Plans.Count; i++)
            {
                var after = await ReadSnapshotAsync(connection, transaction,
                    approved[i].SchemaName, approved[i].TableName, cancellationToken);
                if (!MatchesExpected(after, preview.Plan.Plans[i], approved[i]))
                    throw new InvalidOperationException("DDL batch final physical state differs.");
            }

            startedCommit = true;
            await transaction.CommitAsync(cancellationToken);
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
