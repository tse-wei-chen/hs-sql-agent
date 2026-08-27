namespace HsSqlAgent.SqlCore.Models;

[Flags]
internal enum SqlTypedTemporalLiteralKinds
{
    None = 0,
    Date = 1,
    Time = 2,
    Timestamp = 4
}

/// <summary>
/// Declarative raw-source grammar metadata that is stable for a dialect independent of target
/// provider semantics. Runtime/session-sensitive MySQL lexical modes are exposed through the same
/// source-dialect owner, while feature-specific semantic rules such as concat target lowering remain
/// with their dedicated capability contracts.
/// </summary>
internal static class SqlSourceDialectGrammarRules
{
    private static readonly IReadOnlyDictionary<SqlAgentToolType, SqlSourceDialectGrammarContract> Contracts =
        new Dictionary<SqlAgentToolType, SqlSourceDialectGrammarContract>
        {
            [SqlAgentToolType.Postgres] = new(
                SqlAgentToolType.Postgres,
                SupportsDoubleColonCast: true,
                SupportsLimitKeyword: true,
                SupportsBareBooleanKeywords: true,
                TypedTemporalLiteralKinds:
                    SqlTypedTemporalLiteralKinds.Date
                    | SqlTypedTemporalLiteralKinds.Time
                    | SqlTypedTemporalLiteralKinds.Timestamp,
                SupportsTypedTemporalZoneQualifier: true,
                SupportsLimitAll: true,
                SupportsCommaLimit: false,
                OffsetRequiresLimit: false,
                RequiresOrderByForOffset: false,
                UsesStandardOffsetFetch: true,
                OffsetRowKeywordOptional: true,
                SupportsFetchFirstNext: true,
                FetchRequiresOffset: false,
                FetchRequiresExplicitPositiveCount: false),

            [SqlAgentToolType.MySQL] = new(
                SqlAgentToolType.MySQL,
                SupportsDoubleColonCast: false,
                SupportsLimitKeyword: true,
                SupportsBareBooleanKeywords: true,
                TypedTemporalLiteralKinds:
                    SqlTypedTemporalLiteralKinds.Date
                    | SqlTypedTemporalLiteralKinds.Time
                    | SqlTypedTemporalLiteralKinds.Timestamp,
                SupportsTypedTemporalZoneQualifier: false,
                SupportsLimitAll: false,
                SupportsCommaLimit: true,
                OffsetRequiresLimit: true,
                RequiresOrderByForOffset: false,
                UsesStandardOffsetFetch: false,
                OffsetRowKeywordOptional: false,
                SupportsFetchFirstNext: false,
                FetchRequiresOffset: false,
                FetchRequiresExplicitPositiveCount: false),

            [SqlAgentToolType.MsSqlServer] = new(
                SqlAgentToolType.MsSqlServer,
                SupportsDoubleColonCast: false,
                SupportsLimitKeyword: false,
                SupportsBareBooleanKeywords: false,
                TypedTemporalLiteralKinds: SqlTypedTemporalLiteralKinds.None,
                SupportsTypedTemporalZoneQualifier: false,
                SupportsLimitAll: false,
                SupportsCommaLimit: false,
                OffsetRequiresLimit: false,
                RequiresOrderByForOffset: true,
                UsesStandardOffsetFetch: true,
                OffsetRowKeywordOptional: false,
                SupportsFetchFirstNext: true,
                FetchRequiresOffset: true,
                FetchRequiresExplicitPositiveCount: true),

            [SqlAgentToolType.Sqlite] = new(
                SqlAgentToolType.Sqlite,
                SupportsDoubleColonCast: false,
                SupportsLimitKeyword: true,
                SupportsBareBooleanKeywords: true,
                TypedTemporalLiteralKinds: SqlTypedTemporalLiteralKinds.None,
                SupportsTypedTemporalZoneQualifier: false,
                SupportsLimitAll: false,
                SupportsCommaLimit: true,
                OffsetRequiresLimit: true,
                RequiresOrderByForOffset: false,
                UsesStandardOffsetFetch: false,
                OffsetRowKeywordOptional: false,
                SupportsFetchFirstNext: false,
                FetchRequiresOffset: false,
                FetchRequiresExplicitPositiveCount: false),

            [SqlAgentToolType.Oracle] = new(
                SqlAgentToolType.Oracle,
                SupportsDoubleColonCast: false,
                SupportsLimitKeyword: false,
                SupportsBareBooleanKeywords: true,
                TypedTemporalLiteralKinds:
                    SqlTypedTemporalLiteralKinds.Date
                    | SqlTypedTemporalLiteralKinds.Timestamp,
                SupportsTypedTemporalZoneQualifier: false,
                SupportsLimitAll: false,
                SupportsCommaLimit: false,
                OffsetRequiresLimit: false,
                RequiresOrderByForOffset: false,
                UsesStandardOffsetFetch: true,
                OffsetRowKeywordOptional: false,
                SupportsFetchFirstNext: true,
                FetchRequiresOffset: false,
                FetchRequiresExplicitPositiveCount: false),

            [SqlAgentToolType.Firebird] = new(
                SqlAgentToolType.Firebird,
                SupportsDoubleColonCast: false,
                SupportsLimitKeyword: false,
                SupportsBareBooleanKeywords: true,
                TypedTemporalLiteralKinds:
                    SqlTypedTemporalLiteralKinds.Date
                    | SqlTypedTemporalLiteralKinds.Time
                    | SqlTypedTemporalLiteralKinds.Timestamp,
                SupportsTypedTemporalZoneQualifier: false,
                SupportsLimitAll: false,
                SupportsCommaLimit: false,
                OffsetRequiresLimit: false,
                RequiresOrderByForOffset: false,
                UsesStandardOffsetFetch: true,
                OffsetRowKeywordOptional: false,
                SupportsFetchFirstNext: true,
                FetchRequiresOffset: false,
                FetchRequiresExplicitPositiveCount: false)
        };

    internal static IEnumerable<SqlSourceDialectGrammarContract> All => Contracts.Values;

    internal static SqlSourceDialectGrammarContract For(SqlAgentToolType sourceDialect) =>
        Contracts.TryGetValue(sourceDialect, out var contract)
            ? contract
            : throw new ArgumentOutOfRangeException(
                nameof(sourceDialect),
                sourceDialect,
                "No raw-source grammar contract is registered for this SQL dialect.");

    internal static bool UsesMySqlAnsiQuotedIdentifiers(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile) =>
        sourceDialect == SqlAgentToolType.MySQL
        && sourceProfile is { Provider: SqlAgentToolType.MySQL }
        && (sourceProfile.HasSessionMode("ANSI_QUOTES")
            || sourceProfile.HasSessionMode("ANSI"));

    internal static bool UsesMySqlNoBackslashEscapes(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile) =>
        sourceDialect == SqlAgentToolType.MySQL
        && sourceProfile is { Provider: SqlAgentToolType.MySQL }
        && sourceProfile.HasSessionMode("NO_BACKSLASH_ESCAPES");
}

internal sealed record SqlSourceDialectGrammarContract(
    SqlAgentToolType Dialect,
    bool SupportsDoubleColonCast,
    bool SupportsLimitKeyword,
    bool SupportsBareBooleanKeywords,
    SqlTypedTemporalLiteralKinds TypedTemporalLiteralKinds,
    bool SupportsTypedTemporalZoneQualifier,
    bool SupportsLimitAll,
    bool SupportsCommaLimit,
    bool OffsetRequiresLimit,
    bool RequiresOrderByForOffset,
    bool UsesStandardOffsetFetch,
    bool OffsetRowKeywordOptional,
    bool SupportsFetchFirstNext,
    bool FetchRequiresOffset,
    bool FetchRequiresExplicitPositiveCount)
{
    internal bool SupportsTypedTemporalLiteral(
        string temporalType,
        bool hasZoneQualifier)
    {
        if (hasZoneQualifier && !SupportsTypedTemporalZoneQualifier)
            return false;

        var kind = temporalType.Trim().ToUpperInvariant() switch
        {
            "DATE" => SqlTypedTemporalLiteralKinds.Date,
            "TIME" => SqlTypedTemporalLiteralKinds.Time,
            "TIMESTAMP" => SqlTypedTemporalLiteralKinds.Timestamp,
            _ => SqlTypedTemporalLiteralKinds.None
        };

        return kind != SqlTypedTemporalLiteralKinds.None
            && (TypedTemporalLiteralKinds & kind) != 0;
    }
}
