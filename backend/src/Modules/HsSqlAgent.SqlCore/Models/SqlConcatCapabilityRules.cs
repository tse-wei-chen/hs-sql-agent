namespace HsSqlAgent.SqlCore.Models;

internal enum SqlConcatTargetSyntax
{
    NativePipes,
    ConcatFunction,
    PlusOperator
}

/// <summary>
/// Single source/target/matrix contract for canonical string concatenation. Raw MySQL '||' is
/// session-dependent and is accepted only when PIPES_AS_CONCAT or ANSI is explicitly declared in
/// the source profile. Target lowering uses CONCAT(left, right) for MySQL, + for SQL Server, and
/// native || for PostgreSQL, SQLite, Oracle, and Firebird.
/// </summary>
internal static class SqlConcatCapabilityRules
{
    internal static bool SupportsMySqlPipesAsConcat(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile) =>
        sourceDialect == SqlAgentToolType.MySQL
        && sourceProfile is { Provider: SqlAgentToolType.MySQL }
        && (sourceProfile.HasSessionMode("PIPES_AS_CONCAT")
            || sourceProfile.HasSessionMode("ANSI"));

    internal static string? SourceValidationError(SqlAgentToolType sourceDialect) =>
        sourceDialect == SqlAgentToolType.MySQL
            ? "MySQL '||' semantics depend on PIPES_AS_CONCAT sql_mode; Core rejects the operator because session sql_mode is not part of the compilation plan."
            : null;

    internal static SqlConcatTargetSyntax TargetSyntax(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.MySQL => SqlConcatTargetSyntax.ConcatFunction,
        SqlAgentToolType.MsSqlServer => SqlConcatTargetSyntax.PlusOperator,
        SqlAgentToolType.Postgres
            or SqlAgentToolType.Sqlite
            or SqlAgentToolType.Oracle
            or SqlAgentToolType.Firebird => SqlConcatTargetSyntax.NativePipes,
        _ => throw new ArgumentOutOfRangeException(
            nameof(provider),
            provider,
            "Unsupported SQL provider.")
    };

    internal static SqlCapability MatrixCapability(SqlAgentToolType provider) =>
        TargetSyntax(provider) switch
        {
            SqlConcatTargetSyntax.ConcatFunction => new(
                "expression.concat",
                "expression",
                SqlCapabilityStatus.Translated,
                "Canonical string concatenation is translated to CONCAT(left, right). Raw MySQL source || is accepted as concatenation only when the separate source capability profile declares PIPES_AS_CONCAT or ANSI sql_mode; without that source-session contract it remains fail-closed because MySQL otherwise interprets || as logical OR. A target profile alone never authorizes the source spelling."),
            SqlConcatTargetSyntax.PlusOperator => new(
                "expression.concat",
                "expression",
                SqlCapabilityStatus.Translated,
                "Canonical string concatenation is translated to +."),
            SqlConcatTargetSyntax.NativePipes => new(
                "expression.concat",
                "expression",
                SqlCapabilityStatus.Supported,
                "The provider-native || operator is emitted."),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
}
