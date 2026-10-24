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
/// Read-only inventory of v3 encrypted secrets for purpose-specific key retirement.
/// This class never writes to Admin Store, executes DML or migrates v2 ciphertext.
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
