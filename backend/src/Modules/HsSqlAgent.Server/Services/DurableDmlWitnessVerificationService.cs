using System.Security.Cryptography;
using System.Text;
using Admin.Service.Data;
using Admin.Service.Models;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Factories;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// READ-ONLY, operator-initiated inspection of a target-enlisted commit witness.
/// Never mutates Admin Store state, executes a DML statement, or resolves Executing.
/// A negative result cannot fence a pending target transaction.
/// </summary>
public sealed class DurableDmlWitnessVerificationService(
    AdminContext adminContext,
    ICryptoService cryptoService,
    IOptions<McpKeySettings> mcpKeySettings,
    ISqlProviderFactory sqlProviderFactory,
    ISqlConnectionStringFactory connectionStringFactory,
    IDmlTargetCommitWitness? targetWitness = null,
    ISecretProtector? secretProtector = null)
{
    private const string Match = "CommittedWitnessMatch";
    private const string NotObserved = "NotObserved";
    private const string EvidenceMismatch = "EvidenceMismatch";
    private const string TargetChanged = "TargetConfigurationChanged";
    private const string ReadUnavailable = "ReadUnavailable";
    private const string ApprovalChanged = "ApprovalChanged";
    private const string NotConfigured = "WitnessNotConfigured";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public async Task<DmlTargetCommitWitnessInspection?> InspectAsync(
        string requestId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64)
            throw new ArgumentException("Approval request ID must have 1-64 characters.", nameof(requestId));

        var state = await adminContext.DmlApprovalRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == requestId, cancellationToken);
        if (state is null) return null;
        if (state.Status is not ("Executing" or "OutcomeUnknown"
                or "RollbackOutcomeUnknown" or "ApprovalStateUnknown"))
            throw new InvalidOperationException("Only uncertain consumed approvals support independent witness inspection.");

        DmlTargetCommitWitnessInspection Result(string status) =>
            new(requestId, state.Status, DateTime.UtcNow, status,
                status == Match,
                "A missing, mismatched or unreadable witness never proves non-commit or worker quiescence. " +
                "This operation does not reconcile or replay the consumed approval.");

        if (targetWitness is null) return Result(NotConfigured);

        // Never probe a different physical database than the original approval.
        // Protected resume data holds the connection HMAC produced at approval
        // creation; the current DB configuration must still match it exactly.
        var db = await adminContext.DbManagement.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == state.DbManagementId, cancellationToken);
        if (db is null
            || !Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var provider)
            || !string.Equals(provider.ToString(), state.DatabaseProvider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(db.Database?.Trim(), state.DatabaseIdentity, StringComparison.Ordinal))
            return Result(TargetChanged);

        var key = mcpKeySettings.Value.HmacSecretKey;
        if (string.IsNullOrWhiteSpace(key)) return Result(ReadUnavailable);
        var keyBytes = Encoding.UTF8.GetBytes(key);

        string connectionString;
        try
        {
            var plain = DurableDmlPayloadCiphertextReader.Decrypt(
                state.ProtectedExecutionPayload, state.RequestId,
                cryptoService, keyBytes, secretProtector);
            if (string.IsNullOrWhiteSpace(plain)) return Result(TargetChanged);
            // One authoritative decoder for both completion and independent
            // witness inspection. An old flat payload is explicitly upcast;
            // unknown envelope versions never allow a target connection probe.
            var resume = DurableDmlResumePayloadCodec.Deserialize(plain);
            if (resume.DbManagementId != state.DbManagementId
                || resume.AccessKeyId != state.AccessKeyId
                || resume.SourceDialect != provider)
                return Result(TargetChanged);

            var password = DatabasePasswordCiphertextReader.Decrypt(
                db.PasswordHash, db.Id, cryptoService, keyBytes, secretProtector);
            connectionString = connectionStringFactory.BuildConnectionString(provider,
                new BuildDbConnectionModelBase
                {
                    Host = db.Host,
                    Port = db.Port,
                    Username = db.Username,
                    Password = password,
                    Database = db.Database,
                    ExtraSettings = db.ExtraSettings
                });
            var actualFingerprint = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(connectionString));
            if (!CryptographicOperations.FixedTimeEquals(
                    actualFingerprint, Convert.FromHexString(resume.ConnectionFingerprint)))
                return Result(TargetChanged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Broken encryption/configuration is never permission to probe an
            // unbound target. Never return SQL, the connection string or secrets.
            return Result(TargetChanged);
        }

        string status;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ProbeTimeout);
            var sqlProvider = sqlProviderFactory.GetProvider(provider);
            await using var connection = sqlProvider.Connections.Create(connectionString);
            await connection.OpenAsync(deadline.Token);
            var observed = await targetWitness.ReadFingerprintAsync(
                connection, provider, requestId, deadline.Token);

            if (observed is null)
                status = NotObserved;
            else if (observed.Length == 64 && observed.All(Uri.IsHexDigit)
                && CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(state.EvidenceFingerprint),
                    Convert.FromHexString(observed)))
                status = Match;
            else
                status = EvidenceMismatch;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            status = ReadUnavailable;
        }

        // The DB configuration is a separate mutable Admin Store row. A
        // matching approval snapshot alone does not prove that the target
        // connection remains bound to the same configured database. Detect
        // a concurrent retarget after the independent witness read.
        var latestDb = await adminContext.DbManagement.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == state.DbManagementId, cancellationToken);
        if (latestDb is null
            || latestDb.SqlProvider != db.SqlProvider
            || latestDb.Database != db.Database
            || latestDb.Host != db.Host
            || latestDb.Port != db.Port
            || latestDb.Username != db.Username
            || latestDb.PasswordHash != db.PasswordHash
            || latestDb.ExtraSettings != db.ExtraSettings)
            return Result(TargetChanged);

        // Ensure an operator is not shown a positive result paired with a
        // different Admin Store approval snapshot that changed during I/O.
        var latest = await adminContext.DmlApprovalRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == requestId, cancellationToken);
        if (latest is null || latest.Status != state.Status
            || latest.UpdatedAt != state.UpdatedAt || latest.Reason != state.Reason
            || latest.EvidenceFingerprint != state.EvidenceFingerprint
            || latest.DbManagementId != state.DbManagementId
            || latest.AccessKeyId != state.AccessKeyId
            || latest.ProtectedExecutionPayload != state.ProtectedExecutionPayload
            || latest.DatabaseProvider != state.DatabaseProvider
            || latest.DatabaseIdentity != state.DatabaseIdentity)
            return Result(ApprovalChanged);
        return Result(status);
    }
}

public sealed record DmlTargetCommitWitnessInspection(
    string RequestId,
    string ApprovalStatus,
    DateTime InspectedAt,
    string WitnessStatus,
    bool MatchingCommitWitness,
    string Limitation);
