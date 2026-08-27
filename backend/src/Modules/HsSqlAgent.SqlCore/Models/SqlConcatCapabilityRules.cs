namespace HsSqlAgent.SqlCore.Models;

internal enum SqlServerConcatTargetMode
{
    Rejected,
    PlusOperator,
    NativePipes
}

/// <summary>
/// Single safety contract for canonical string concatenation where SQL Server runtime semantics are
/// version/session dependent. Raw SQL Server || source syntax remains separate and fail-closed until
/// the source parser has an explicit T-SQL 17.x grammar/precedence contract.
/// </summary>
internal static class SqlConcatCapabilityRules
{
    private static readonly Version SqlServerAlwaysNullConcatVersion = new(14, 0);
    private static readonly Version SqlServerNativePipesVersion = new(17, 0);

    internal static bool SupportsMySqlPipesAsConcat(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile) =>
        sourceDialect == SqlAgentToolType.MySQL
        && sourceProfile is { Provider: SqlAgentToolType.MySQL }
        && (sourceProfile.HasSessionMode("PIPES_AS_CONCAT")
            || sourceProfile.HasSessionMode("ANSI"));

    internal static string? SourceSemanticValidationError(
        SqlAgentToolType sourceDialect) =>
        sourceDialect == SqlAgentToolType.MySQL
            ? "MySQL '||' semantics depend on PIPES_AS_CONCAT sql_mode; Core rejects the operator because session sql_mode is not part of the compilation plan."
            : null;

    internal static bool UsesConcatFunctionForCanonicalPipes(
        SqlAgentToolType targetProvider) => targetProvider switch
    {
        SqlAgentToolType.MySQL => true,
        SqlAgentToolType.Postgres
            or SqlAgentToolType.Sqlite
            or SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Oracle
            or SqlAgentToolType.Firebird => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(targetProvider),
            targetProvider,
            "Unsupported SQL provider.")
    };

    internal static string? RawSourceSyntaxError(SqlAgentToolType sourceDialect) =>
        sourceDialect == SqlAgentToolType.MsSqlServer
            ? "Raw SQL Server source operator '||' remains fail-closed. SQL Server 2025 (17.x) introduces ANSI pipes concatenation, but Core has not yet declared a T-SQL 17.x source grammar/precedence contract."
            : null;

    internal static SqlServerConcatTargetMode EvaluateSqlServerTarget(
        SqlProviderCapabilityProfile? targetProfile)
    {
        if (targetProfile is not { Provider: SqlAgentToolType.MsSqlServer })
            return SqlServerConcatTargetMode.Rejected;

        var version = targetProfile.ServerVersion;
        if (version is not null
            && version.CompareTo(SqlServerNativePipesVersion) >= 0
            && targetProfile.CompatibilityLevel is >= 170)
        {
            return SqlServerConcatTargetMode.NativePipes;
        }

        if (version is not null
            && version.CompareTo(SqlServerAlwaysNullConcatVersion) >= 0)
        {
            return SqlServerConcatTargetMode.PlusOperator;
        }

        var concatNullSetting = targetProfile
            .GetSessionSetting("CONCAT_NULL_YIELDS_NULL")
            ?.Trim();
        if (string.Equals(concatNullSetting, "ON", StringComparison.OrdinalIgnoreCase))
            return SqlServerConcatTargetMode.PlusOperator;

        return SqlServerConcatTargetMode.Rejected;
    }

    internal static string SqlServerTargetValidationError(
        SqlProviderCapabilityProfile? targetProfile)
    {
        var version = targetProfile?.ServerVersion?.ToString() ?? "undeclared";
        var compatibility = targetProfile?.CompatibilityLevel?.ToString() ?? "undeclared";
        var concatNull = targetProfile?.GetSessionSetting("CONCAT_NULL_YIELDS_NULL") ?? "undeclared";
        return
            "SQL capability 'expression.concat' for SQL Server requires declared runtime proof. " +
            "ServerVersion 17.0+ with CompatibilityLevel 170+ uses native ANSI ||; " +
            "ServerVersion 14.0+ uses + because CONCAT_NULL_YIELDS_NULL is always ON; " +
            "older or undeclared versions require SessionSettings['CONCAT_NULL_YIELDS_NULL']='ON'. " +
            $"Declared profile: ServerVersion={version}, CompatibilityLevel={compatibility}, CONCAT_NULL_YIELDS_NULL={concatNull}.";
    }

    internal static SqlCapability MatrixCapability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        if (provider == SqlAgentToolType.MySQL)
        {
            return new(
                "expression.concat",
                "expression",
                SqlCapabilityStatus.Translated,
                "Canonical string concatenation is translated to CONCAT(left, right). Raw MySQL source || is accepted as concatenation only when the separate source capability profile declares PIPES_AS_CONCAT or ANSI sql_mode; without that source-session contract it remains fail-closed because MySQL otherwise interprets || as logical OR. A target profile alone never authorizes the source spelling.");
        }

        if (provider == SqlAgentToolType.MsSqlServer)
        {
            return EvaluateSqlServerTarget(targetProfile) switch
            {
                SqlServerConcatTargetMode.NativePipes => new(
                    "expression.concat",
                    "expression",
                    SqlCapabilityStatus.Supported,
                    "Declared SQL Server 2025 (17.x) / compatibility-level-170+ target emits native ANSI ||, whose NULL behavior does not depend on CONCAT_NULL_YIELDS_NULL."),
                SqlServerConcatTargetMode.PlusOperator => new(
                    "expression.concat",
                    "expression",
                    SqlCapabilityStatus.Translated,
                    "Canonical concatenation is translated to + only because the declared target proves ANSI NULL propagation through SQL Server 14.x+ or explicit CONCAT_NULL_YIELDS_NULL=ON."),
                SqlServerConcatTargetMode.Rejected => new(
                    "expression.concat",
                    "expression",
                    SqlCapabilityStatus.Rejected,
                    "SQL Server concatenation is fail-closed without runtime proof: declare ServerVersion 14.0+ or CONCAT_NULL_YIELDS_NULL=ON; ServerVersion 17.0+ with CompatibilityLevel 170+ can emit native ANSI ||."),
                _ => throw new ArgumentOutOfRangeException(nameof(targetProfile))
            };
        }

        return new(
            "expression.concat",
            "expression",
            SqlCapabilityStatus.Supported,
            "The provider-native || operator is emitted.");
    }
}
