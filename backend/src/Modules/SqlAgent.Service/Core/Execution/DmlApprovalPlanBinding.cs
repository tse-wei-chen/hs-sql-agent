using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// A second, versioned approval identity covering the complete validated plan.
/// Mutation fingerprints are stable SqlCore contracts, but by themselves do not
/// attest to row-selection SQL, row identity, approval mode, policy row limit or
/// the values displayed for an INSERT approval.
/// </summary>
internal static class DmlApprovalPlanBinding
{
    public static string Compute(ValidatedDmlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Append(hash, "dml-approval-plan-binding-v1");
        Append(hash, plan.PlanFingerprint);
        var mutationFingerprintPolicy =
            plan.SqlServerCteUniqueKeyAssurance?.TargetKeyColumn is not null
                ? plan.PolicyVersion + "|live-sqlserver-cte-int=" +
                  plan.SqlServerCteUniqueKeyAssurance.ProofIdentity
                : plan.PolicyVersion;
        Append(hash, DmlFingerprintService.ComputePlanFingerprint(
            plan.MutationCommand, mutationFingerprintPolicy));
        Append(hash, plan.Operation.ToString());
        Append(hash, plan.TableName);
        Append(hash, plan.PolicyVersion);
        Append(hash, plan.ApprovalMode.ToString());
        Append(hash, plan.RowIdentityAssurance.ToString());
        Append(hash, plan.MaxAffectedRows.ToString(CultureInfo.InvariantCulture));
        Append(hash, plan.ApprovalTtl.Ticks.ToString(CultureInfo.InvariantCulture));
        Append(hash, plan.SchemaSnapshot?.Sha256 ?? "no-schema-snapshot");

        // Only opt-in SQL Server plans gain a new binding component. Preserve
        // the existing challenge hash for all other DML and previous plans.
        // The proof is live-minted and includes source/target identity, index
        // fingerprint, physical database and pinned schema SHA256.
        if (plan.SqlServerCteUniqueKeyAssurance is { } proof)
        {
            Append(hash, "sqlserver-cte-live-key-proof-v1");
            Append(hash, proof.ProofIdentity);
        }

        if (plan.MatchQueryCommand is { } match)
        {
            Append(hash, "match-query");
            Append(hash, DmlFingerprintService.ComputePlanFingerprint(
                match, plan.PolicyVersion));
        }
        else
            Append(hash, "no-match-query");

        Append(hash, plan.RowIdentityColumns.IsDefault
            ? "missing-row-identity"
            : plan.RowIdentityColumns.Length.ToString(CultureInfo.InvariantCulture));
        if (!plan.RowIdentityColumns.IsDefault)
            foreach (var column in plan.RowIdentityColumns)
                Append(hash, column);

        Append(hash, plan.InsertRows.IsDefault
            ? "missing-insert-rows"
            : plan.InsertRows.Length.ToString(CultureInfo.InvariantCulture));
        if (!plan.InsertRows.IsDefault)
        {
            foreach (var row in plan.InsertRows)
            {
                // Sort dictionary keys; preserve INSERT row order. Use the
                // canonical typed value encoding from the F# SqlCore rather
                // than JSON's ambiguous cross-type value representation.
                var cells = row.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => (IReadOnlyList<object?>)new object?[] { item.Key, item.Value })
                    .ToArray();
                Append(hash, DmlFingerprintService.ComputeRowSetFingerprint(cells));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var data = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        hash.AppendData(length);
        hash.AppendData(data);
    }
}
