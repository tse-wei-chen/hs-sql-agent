using System.Collections.Immutable;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Compilation;

public enum SqlStatementKind
{
    Query,
    // SELECT is the SQL statement spelling; keep Query as the original API name while the
    // compiler pipeline migrates callers. Both names intentionally represent the same kind.
    Select = Query,
    Insert,
    Update,
    Delete
}

public sealed record SqlParameterValue(string Name, object? Value);

/// <summary>
/// Immutable execution boundary. Executors should receive this type rather than parser DTOs,
/// semantic nodes, or SqlKata queries so the command that was validated is the command executed.
/// </summary>
public sealed record CompiledSqlCommand(
    string Sql,
    ImmutableArray<SqlParameterValue> Parameters,
    SqlStatementKind Kind,
    string PlanFingerprint,
    SqlAgentToolType TargetProvider);

public sealed class SqlCompilationException(string message) : InvalidOperationException(message);
