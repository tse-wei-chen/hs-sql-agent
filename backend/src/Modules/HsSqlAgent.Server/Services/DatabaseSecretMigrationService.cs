using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Common.Interfaces;
using Common.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Explicit, bounded, resumable migration of persisted database credentials.
/// This class is never a hosted/background service. All changes use compare-and-
/// swap with the exact original ciphertext, followed by in-transaction
/// decrypt-and-compare verification. It never touches DML approval state.
/// </summary>
public sealed class DatabaseSecretMigrationService(
    AdminContext context,
    ICryptoService legacyCrypto,
    IOptions<McpKeySettings> mcpSettings,
    ISecretProtector? protector = null,
    ISecretKeyRing? keyRing = null,
    ISecretWriteRolloutPolicy? writePolicy = null)
{
    private const int MaxBatch = 100;
    private const int ScanPage = 200;

    public async Task<SecretMigrationInventory> InspectAsync(CancellationToken ct = default)
    {
        var dbUsage = new Dictionary<string, int>(StringComparer.Ordinal);
        var approvalUsage = new Dictionary<string, int>(StringComparer.Ordinal);
        var legacyDatabases = 0;
        var legacyApprovals = 0;
        var malformed = 0;
        var databasesWithoutPasswords = 0;
        var afterId = 0;

        while (true)
        {
            var rows = await context.DbManagement.AsNoTracking()
                .Where(x => x.Id > afterId)
                .OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.PasswordHash })
                .Take(ScanPage).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                afterId = row.Id;
                if (string.IsNullOrEmpty(row.PasswordHash))
                    databasesWithoutPasswords++;
                else
                    CountEnvelope(row.PasswordHash, dbUsage, ref legacyDatabases, ref malformed);
            }
        }

        // DML payloads must be included in key-retirement inventory even
        // though this migrator is intentionally forbidden from changing them.
        // Offset paging for string PK avoids dialect-specific String.Compare
        // translation differences between PostgreSQL and SQLite.
        var offset = 0;
        while (true)
        {
            var rows = await context.DmlApprovalRequests.AsNoTracking()
                .OrderBy(x => x.RequestId)
                .Select(x => new { x.RequestId, x.ProtectedExecutionPayload })
                .Skip(offset).Take(ScanPage).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
                CountEnvelope(row.ProtectedExecutionPayload, approvalUsage, ref legacyApprovals, ref malformed);
            offset += rows.Count;
        }

        var outstanding = await context.DmlApprovalRequests.AsNoTracking()
            .CountAsync(x => x.Status == "Pending" || x.Status == "Executing", ct);
        return new SecretMigrationInventory(
            legacyDatabases,
            legacyApprovals,
            databasesWithoutPasswords,
            malformed,
            outstanding,
            dbUsage.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new SecretKeyUsage(x.Key, x.Value)).ToArray(),
            approvalUsage.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new SecretKeyUsage(x.Key, x.Value)).ToArray());
    }

    /// <summary>
    /// A zero-reference result is only eligible for OFFLINE operator review.
    /// It is not a distributed proof that no node or backup still depends on
    /// the key. This service never removes keys from the ring.
    /// </summary>
    public async Task<SecretKeyRetirementReview> ReviewKeyRetirementAsync(
        SecretPurpose purpose, string keyId, CancellationToken ct = default)
    {
        if (purpose is not (SecretPurpose.DatabasePassword or SecretPurpose.DurableDmlPayload))
            throw new ArgumentException("Only persisted DB and DML keys can be inventoried.");
        if (string.IsNullOrEmpty(keyId) || keyId.Length > 64 ||
            keyId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Invalid encryption key ID.");

        if (keyRing is null)
            throw new InvalidOperationException("Secret key ring is not configured.");

        var (activeKeyId, activeBytes) = keyRing.GetActiveKey(purpose);
        CryptographicOperations.ZeroMemory(activeBytes);
        var candidateBytes = keyRing.GetDecryptionKey(purpose, keyId);
        CryptographicOperations.ZeroMemory(candidateBytes);

        var inventory = await InspectAsync(ct);
        var refs = purpose == SecretPurpose.DatabasePassword
            ? inventory.DatabaseKeyUsage
            : inventory.DmlKeyUsage;
        var references = refs.FirstOrDefault(x => x.KeyId == keyId)?.Records ?? 0;
        var blockers = new List<string>();
        if (activeKeyId == keyId)
            blockers.Add("Key is still active for new encryption.");
        if (references != 0)
            blockers.Add("Live Admin Store ciphertext still references this key ID.");
        if (inventory.MalformedEnvelopes != 0)
            blockers.Add("Malformed or unknown ciphertext envelopes require manual investigation.");
        if (inventory.PendingOrExecutingApprovals != 0)
            blockers.Add("Pending or Executing approvals have not been drained.");

        return new SecretKeyRetirementReview(
            purpose.ToString(), keyId, references, blockers.Count == 0,
            blockers.ToArray(),
            "Zero current database references do not prove that backups, " +
            "old worker processes or external consumers no longer need this key. " +
            "Manually verify all nodes, offline copies and recovery plans before removing any key.");
    }

    public async Task<DatabaseSecretMigrationBatch> MigrateBatchAsync(
        int afterId, int batchSize, CancellationToken ct = default, string actor = "system")
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 64)
            throw new ArgumentException("A migration actor ID of 1-64 characters is required.", nameof(actor));
        if (afterId < 0 || batchSize is < 1 or > MaxBatch)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Migration cursor and batch size must be valid (1-100).");
        if (writePolicy?.EnableV2DatabasePasswordWrites != true || protector is null || keyRing is null)
            throw new InvalidOperationException(
                "Database password migration requires an enabled v2 write policy and a configured decryption key ring.");
        if (!context.Database.IsRelational())
            throw new InvalidOperationException("Database password migration requires a relational Admin Store.");

        // Explicit fail-closed global drain gate. A live DML worker can still
        // be between approval and target COMMIT, so do not rewrite credentials
        // during any Pending/Executing approvals (even on other DBs).
        if (await context.DmlApprovalRequests.AsNoTracking()
            .AnyAsync(x => x.Status == "Pending" || x.Status == "Executing", ct))
            throw new InvalidOperationException(
                "Drain all Pending/Executing DML approvals and fence old workers before migrating credentials.");

        var (activeKeyId, activeKey) = keyRing.GetActiveKey(SecretPurpose.DatabasePassword);
        CryptographicOperations.ZeroMemory(activeKey);

        var rows = await context.DbManagement.AsNoTracking()
            .Where(x => x.Id > afterId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.PasswordHash })
            .Take(batchSize).ToListAsync(ct);

        var migrated = 0;
        var alreadyCurrent = 0;
        var conflicts = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(row.PasswordHash))
            {
                alreadyCurrent++;
                continue;
            }
            if (row.PasswordHash.StartsWith("v2:" + activeKeyId + ":", StringComparison.Ordinal))
            {
                // Even active-key rows must be authentic. A damaged tag is an
                // integrity incident, not an excuse to count them as migrated.
                _ = protector.Unprotect(
                    row.PasswordHash, SecretPurpose.DatabasePassword,
                    row.Id.ToString(CultureInfo.InvariantCulture));
                alreadyCurrent++;
                continue;
            }

            var old = row.PasswordHash;
            var recordId = row.Id.ToString(CultureInfo.InvariantCulture);
            string? plaintext;
            try
            {
                plaintext = DatabasePasswordCiphertextReader.Decrypt(
                    old, row.Id, legacyCrypto,
                    Encoding.UTF8.GetBytes(mcpSettings.Value.HmacSecretKey), protector);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // No ciphertext/secret content in exception or API response.
                throw new InvalidOperationException(
                    "Migration stopped: a persisted database credential cannot be authenticated.");
            }

            if (plaintext is null || plaintext.Length == 0)
                throw new InvalidOperationException("Migration stopped: unexpected empty encrypted database credential.");

            var replacement = protector.Protect(plaintext, SecretPurpose.DatabasePassword, recordId)
                ?? throw new InvalidOperationException("Encryption did not return a database credential.");

            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            // Recheck inside the write transaction; operators still need
            // external worker quiescence across the entire rollout.
            if (await context.DmlApprovalRequests.AsNoTracking()
                .AnyAsync(x => x.Status == "Pending" || x.Status == "Executing", ct))
                throw new InvalidOperationException(
                    "DML activity resumed while migrating credentials; stop and verify worker quiescence.");

            // Atomic CAS prevents overwriting a concurrent Admin/Bootstrap
            // password update. The SELECT before this transaction is not a lock.
            var changed = await context.DbManagement
                .Where(x => x.Id == row.Id && x.PasswordHash == old)
                .ExecuteUpdateAsync(x => x.SetProperty(db => db.PasswordHash, replacement), ct);
            if (changed == 0)
            {
                conflicts++;
                continue;
            }
            if (changed != 1)
                throw new InvalidOperationException("Migration updated an unexpected number of credential rows.");

            var stored = await context.DbManagement.AsNoTracking()
                .Where(x => x.Id == row.Id).Select(x => x.PasswordHash)
                .SingleAsync(ct);
            var verified = protector.Unprotect(stored, SecretPurpose.DatabasePassword, recordId);
            if (!string.Equals(plaintext, verified, StringComparison.Ordinal))
                throw new CryptographicException("Database credential round-trip verification failed.");

            // Persist a secret-free, per-row audit event inside the *same*
            // transaction as the ciphertext change. An audit outage must
            // never allow a migration write to commit without evidence.
            context.AuditLogs.Add(new AuditLog
            {
                EventId = Guid.NewGuid(),
                ActorType = actor == "system" ? "system" : "admin",
                ActorId = actor,
                Action = "security.database-secret.migrated",
                Target = recordId,
                DbManagementId = row.Id,
                Operation = "migration",
                Result = "success",
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            migrated++;
        }

        return new DatabaseSecretMigrationBatch(
            rows.Count == 0 ? afterId : rows[^1].Id,
            rows.Count, migrated, alreadyCurrent, conflicts,
            rows.Count == batchSize);
    }

    private static void CountEnvelope(
        string? cipher, Dictionary<string, int> keys, ref int legacyCount, ref int malformedCount)
    {
        if (string.IsNullOrEmpty(cipher)) return;
        if (cipher.StartsWith("v2:", StringComparison.Ordinal))
        {
            var end = cipher.IndexOf(':', 3);
            var id = end < 0 ? "" : cipher[3..end];
            if (id.Length is < 1 or > 64 ||
                id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) ||
                end == cipher.Length - 1)
            {
                malformedCount++;
                return;
            }
            keys[id] = keys.GetValueOrDefault(id) + 1;
        }
        else if (cipher.Contains(':'))
            malformedCount++;
        else
            legacyCount++;
    }
}

public sealed record SecretKeyUsage(string KeyId, int Records);
public sealed record SecretKeyRetirementReview(
    string Purpose, string KeyId, int CurrentReferences,
    bool EligibleForOfflineReview, IReadOnlyList<string> Blockers,
    string RequiredOperatorVerification);
public sealed record SecretMigrationInventory(
    int LegacyDatabasePasswords,
    int LegacyDmlPayloads,
    int EmptyDatabasePasswords,
    int MalformedEnvelopes,
    int PendingOrExecutingApprovals,
    IReadOnlyList<SecretKeyUsage> DatabaseKeyUsage,
    IReadOnlyList<SecretKeyUsage> DmlKeyUsage);
public sealed record DatabaseSecretMigrationBatch(
    int NextAfterId, int Scanned, int Migrated, int AlreadyCurrent,
    int ConcurrentChanges, bool MayHaveMoreRows);
