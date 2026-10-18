using System.Collections.Immutable;
using Admin.Service.Models;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Immutable policy decision evidence at the SQL execution boundary.
/// Uses the exact query/DML compiler policy versions (not a second independent hash
/// algorithm). This is a binding/snapshot contract, not yet row/column/tenant RLS.
/// </summary>
internal sealed class SqlExecutionPolicyDecision
{
    private readonly SecurityPolicyModel _policy;
    private readonly ImmutableHashSet<string>? _allowedTables;
    private ImmutableHashSet<string>? _allowedQueryColumns;
    private readonly string _identityFingerprint;
    private readonly bool _isDml;

    private SqlExecutionPolicyDecision(
        DmlApprovalExecutionContext identity,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables,
        bool isDml)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(policy);

        // SecurityPolicyModel is a mutable configuration DTO. Do not keep a
        // reference to it or hand that reference to downstream callers.
        _policy = policy.Clone();
        _allowedTables = allowedTables is null
            ? null
            : ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, allowedTables);
        _identityFingerprint = TypedDmlRuntime.ComputeApprovalContextFingerprint(identity);
        _isDml = isDml;

        PolicyVersion = isDml
            ? TypedDmlRuntime.ComputePolicyVersion(_policy, _allowedTables)
            : TypedQueryRuntime.ComputePolicyVersion(_policy, _allowedTables);
    }

    internal string PolicyVersion { get; }

    internal SecurityPolicyModel PolicySnapshot => _policy.Clone();

    // Immutable even if the source HashSet is later changed.
    internal IReadOnlySet<string>? AllowedTables => _allowedTables;
    internal IReadOnlySet<string>? AllowedQueryColumns => _allowedQueryColumns;

    internal static SqlExecutionPolicyDecision ForQuery(
        McpSqlExecutionScope scope,
        SecurityPolicyModel policy)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var decision = new SqlExecutionPolicyDecision(
            scope.ToApprovalContext(), policy, scope.AllowedTables, isDml: false);
        decision._allowedQueryColumns = scope.AllowedQueryColumns is null
            ? null : ImmutableHashSet.CreateRange(StringComparer.Ordinal, scope.AllowedQueryColumns);
        return decision;
    }

    internal static SqlExecutionPolicyDecision ForDml(
        DmlApprovalExecutionContext identity,
        SecurityPolicyModel policy,
        IReadOnlySet<string>? allowedTables) =>
        new(identity, policy, allowedTables, isDml: true);

    internal void EnsureSameDmlDecision(SqlExecutionPolicyDecision latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        if (!_isDml || !latest._isDml)
            throw new InvalidOperationException("Only DML policy decisions can be compared for approved mutations.");
        if (!string.Equals(_identityFingerprint, latest._identityFingerprint, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "DML execution principal, database or provider changed after approval preview.");
        if (!string.Equals(PolicyVersion, latest.PolicyVersion, StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                "DML execution policy or table authorization changed after approval preview.");
    }
}
