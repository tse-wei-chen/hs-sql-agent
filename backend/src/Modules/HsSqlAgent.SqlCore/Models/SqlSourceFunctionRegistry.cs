namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Declarative raw-source function contracts consumed before normalization. This registry owns
/// static function-name, source-dialect, arity, and canonicalization-family classification.
/// Runtime/session-sensitive syntax and specialized source capabilities remain with their dedicated
/// capability-rule owners.
/// </summary>
internal static class SqlSourceFunctionRegistry
{
    private static readonly IReadOnlyDictionary<string, SqlSourceFunctionContract> Contracts =
        CreateContracts();

    internal static IEnumerable<SqlSourceFunctionContract> All => Contracts.Values;

    internal static SqlSourceFunctionContract? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Contracts.TryGetValue(name.Trim(), out var contract)
            ? contract
            : null;
    }

    private static IReadOnlyDictionary<string, SqlSourceFunctionContract> CreateContracts()
    {
        var contracts = new[]
        {
            Function(
                "DATEADD",
                SqlSourceFunctionCanonicalizationKind.DateAdd,
                "DATEADD is modeled as a three-argument SQL Server/Firebird source function.",
                Exact(SqlAgentToolType.MsSqlServer, 3),
                Exact(SqlAgentToolType.Firebird, 3)),
            Function(
                "DATEDIFF",
                SqlSourceFunctionCanonicalizationKind.DateDiff,
                "DATEDIFF is modeled as SQL Server/Firebird (3 arguments) or MySQL (2 arguments) source syntax.",
                Exact(SqlAgentToolType.MsSqlServer, 3),
                Exact(SqlAgentToolType.Firebird, 3),
                Exact(SqlAgentToolType.MySQL, 2)),
            Function(
                "DATE_FORMAT",
                SqlSourceFunctionCanonicalizationKind.DateFormat,
                "DATE_FORMAT is modeled as MySQL source syntax.",
                Any(SqlAgentToolType.MySQL)),
            Function(
                "FORMAT",
                SqlSourceFunctionCanonicalizationKind.DateFormat,
                "Core models FORMAT as SQL Server date-format syntax; MySQL/SQLite FORMAT functions have different semantics.",
                Any(SqlAgentToolType.MsSqlServer)),
            Function(
                "TO_DATE",
                SqlSourceFunctionCanonicalizationKind.DateParse,
                "TO_DATE is modeled only for PostgreSQL and Oracle source syntax.",
                Any(SqlAgentToolType.Postgres),
                Any(SqlAgentToolType.Oracle)),

            Function(
                "CHARINDEX",
                SqlSourceFunctionCanonicalizationKind.Position,
                "CHARINDEX is modeled as MsSqlServer source syntax.",
                Any(SqlAgentToolType.MsSqlServer)),
            Function(
                "LOCATE",
                SqlSourceFunctionCanonicalizationKind.Position,
                "LOCATE is modeled as MySQL source syntax.",
                Any(SqlAgentToolType.MySQL)),
            Function(
                "STRPOS",
                SqlSourceFunctionCanonicalizationKind.Position,
                "STRPOS is modeled as Postgres source syntax.",
                Any(SqlAgentToolType.Postgres)),
            Function(
                "INSTR",
                SqlSourceFunctionCanonicalizationKind.Position,
                "INSTR is modeled for MySQL, SQLite, and Oracle source syntax.",
                Any(SqlAgentToolType.MySQL),
                Any(SqlAgentToolType.Sqlite),
                Any(SqlAgentToolType.Oracle)),

            Function(
                "JSON_EXTRACT",
                SqlSourceFunctionCanonicalizationKind.JsonExtract,
                "JSON_EXTRACT is modeled for MySQL and SQLite source syntax.",
                Any(SqlAgentToolType.MySQL),
                Any(SqlAgentToolType.Sqlite)),
            Function(
                "JSON_SET",
                SqlSourceFunctionCanonicalizationKind.JsonSet,
                "JSON_SET is modeled for MySQL and SQLite source syntax.",
                Any(SqlAgentToolType.MySQL),
                Any(SqlAgentToolType.Sqlite)),
            Function(
                "REGEXP_LIKE",
                SqlSourceFunctionCanonicalizationKind.RegexMatch,
                "REGEXP_LIKE is modeled for MySQL, Oracle, and SQL Server 2025+ source syntax.",
                Any(SqlAgentToolType.MySQL),
                Any(SqlAgentToolType.Oracle),
                Any(SqlAgentToolType.MsSqlServer)),

            Function(
                "GETDATE",
                SqlSourceFunctionCanonicalizationKind.CurrentTimestamp,
                "GETDATE is modeled as MsSqlServer source syntax.",
                Any(SqlAgentToolType.MsSqlServer)),
            Function(
                "NOW",
                SqlSourceFunctionCanonicalizationKind.CurrentTimestamp,
                "NOW is modeled for PostgreSQL and MySQL source syntax.",
                Any(SqlAgentToolType.Postgres),
                Any(SqlAgentToolType.MySQL)),

            Function(
                "STRING_AGG",
                SqlSourceFunctionCanonicalizationKind.StringAggregate,
                "STRING_AGG is modeled as a two-argument PostgreSQL/SQL Server source function.",
                Exact(SqlAgentToolType.Postgres, 2),
                Exact(SqlAgentToolType.MsSqlServer, 2)),
            Function(
                "GROUP_CONCAT",
                SqlSourceFunctionCanonicalizationKind.StringAggregate,
                "GROUP_CONCAT is modeled for MySQL source syntax and SQLite with one or two arguments; the SEPARATOR clause is MySQL-only.",
                Any(
                    SqlAgentToolType.MySQL,
                    supportsAggregateSeparatorClause: true),
                Range(SqlAgentToolType.Sqlite, 1, 2)),
            Function(
                "LISTAGG",
                SqlSourceFunctionCanonicalizationKind.StringAggregate,
                "LISTAGG is modeled for Oracle source syntax with one or two arguments.",
                Range(SqlAgentToolType.Oracle, 1, 2)),
            Function(
                "LIST",
                SqlSourceFunctionCanonicalizationKind.StringAggregate,
                "LIST is modeled for Firebird source syntax with one or two arguments.",
                Range(SqlAgentToolType.Firebird, 1, 2))
        };

        return contracts.ToDictionary(
            contract => contract.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    private static SqlSourceFunctionContract Function(
        string name,
        SqlSourceFunctionCanonicalizationKind canonicalizationKind,
        string detail,
        params SqlSourceFunctionDialectRule[] dialectRules) =>
        new(name, canonicalizationKind, detail, dialectRules);

    private static SqlSourceFunctionDialectRule Any(
        SqlAgentToolType dialect,
        bool supportsAggregateSeparatorClause = false) =>
        new(
            dialect,
            0,
            null,
            supportsAggregateSeparatorClause);

    private static SqlSourceFunctionDialectRule Exact(
        SqlAgentToolType dialect,
        int arguments) =>
        new(dialect, arguments, arguments);

    private static SqlSourceFunctionDialectRule Range(
        SqlAgentToolType dialect,
        int minArguments,
        int maxArguments) =>
        new(dialect, minArguments, maxArguments);
}

internal enum SqlSourceFunctionCanonicalizationKind
{
    DateAdd,
    DateDiff,
    DateFormat,
    DateParse,
    Position,
    JsonExtract,
    JsonSet,
    RegexMatch,
    CurrentTimestamp,
    StringAggregate
}

internal sealed record SqlSourceFunctionContract(
    string Name,
    SqlSourceFunctionCanonicalizationKind CanonicalizationKind,
    string Detail,
    IReadOnlyList<SqlSourceFunctionDialectRule> DialectRules)
{
    internal string? ValidationError(
        SqlAgentToolType sourceDialect,
        int argumentCount)
    {
        if (DialectRules.Any(rule => rule.Accepts(sourceDialect, argumentCount)))
            return null;

        return
            $"Function '{Name}' is not valid for declared source dialect {sourceDialect} " +
            $"in the Core source capability profile. {Detail}";
    }

    internal bool SupportsAggregateSeparatorClause(
        SqlAgentToolType sourceDialect) =>
        DialectRules.Any(
            rule => rule.Dialect == sourceDialect
                && rule.SupportsAggregateSeparatorClause);
}

internal sealed record SqlSourceFunctionDialectRule(
    SqlAgentToolType Dialect,
    int MinArguments,
    int? MaxArguments,
    bool SupportsAggregateSeparatorClause = false)
{
    internal bool Accepts(
        SqlAgentToolType dialect,
        int argumentCount) =>
        Dialect == dialect
        && argumentCount >= MinArguments
        && (MaxArguments is null || argumentCount <= MaxArguments);
}
