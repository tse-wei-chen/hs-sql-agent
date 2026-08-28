namespace HsSqlAgent.SqlCore.Models;

[Flags]
internal enum SqlTypedTemporalLiteralKinds
{
    None = 0,
    Date = 1,
    Time = 2,
    Timestamp = 4
}

[Flags]
internal enum SqlSourceLexicalFeatures
{
    None = 0,
    HashLineComment = 1 << 0,
    DashDashCommentRequiresSeparator = 1 << 1,
    PostgresEscapeString = 1 << 2,
    PostgresDollarQuotedString = 1 << 3,
    OracleQuotedString = 1 << 4,
    DoubleQuotedIdentifierRequiresAnsiMode = 1 << 5,
    BacktickQuotedIdentifier = 1 << 6,
    BracketQuotedIdentifier = 1 << 7,
    HashPrefixedIdentifier = 1 << 8,
    BackslashSensitiveQuotedText = 1 << 9
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
                SupportsLimitAll: true,
                SupportsCommaLimit: false,
                OffsetRequiresLimit: false,
                UsesStandardOffsetFetch: true,
                OffsetRowKeywordOptional: true,
                OffsetRequiresOrderBy: false,
                SupportsFetch: true,
                FetchRequiresPrecedingOffset: false,
                FetchCountOptional: true,
                FetchCountMustBePositive: false,
                SupportsTop: false,
                SupportsBareBooleanKeywords: true,
                TypedTemporalLiteralKinds:
                    SqlTypedTemporalLiteralKinds.Date
                    | SqlTypedTemporalLiteralKinds.Time
                    | SqlTypedTemporalLiteralKinds.Timestamp,
                SupportsTypedTemporalZoneQualifier: true,
                LexicalFeatures: SqlSourceLexicalFeatures.PostgresEscapeString
                    | SqlSourceLexicalFeatures.PostgresDollarQuotedString),

            [SqlAgentToolType.MySQL] = new(
                SqlAgentToolType.MySQL,
                SupportsDoubleColonCast: false,
                SupportsLimitKeyword: true,
                SupportsLimitAll: false,
                SupportsCommaLimit: true,
                OffsetRequiresLimit: true,
                UsesStandardOffsetFetch: false,
                OffsetRowKeywordOptional: false,
                OffsetRequiresOrderBy: false,
                SupportsFetch: false,
                FetchRequiresPrecedingOffset: false,
                FetchCountOptional: false,
                FetchCountMustBePositive: false,
                SupportsTop: false,
                SupportsBareBooleanKeywords: true,
                TypedTemporalLiteralKinds:
                    SqlTypedTemporalLiteralKinds.Date
                    | SqlTypedTemporalLiteralKinds.Time
                    | SqlTypedTemporalLiteralKinds.Timestamp,
                SupportsTypedTemporalZoneQualifier: false,
                LexicalFeatures: SqlSourceLexicalFeatures.HashLineComment
                    | SqlSourceLexicalFeatures.DashDashCommentRequiresSeparator
                    | SqlSourceLexicalFeatures.DoubleQuotedIdentifierRequiresAnsiMode
                    | SqlSourceLexicalFeatures.BacktickQuotedIdentifier
                    | SqlSourceLexicalFeatures.BackslashSensitiveQuotedText),

            [SqlAgentToolType.MsSqlServer] = new(
                SqlAgentToolType.MsSqlServer,
                SupportsDoubleColonCast: false,
                SupportsLimitKeyword: false,
                SupportsLimitAll: false,
                SupportsCommaLimit: false,
                OffsetRequiresLimit: false,
                UsesStandardOffsetFetch: true,
                OffsetRowKeywordOptional: false,
                OffsetRequiresOrderBy: true,
                SupportsFetch: true,
                FetchRequiresPrecedingOffset: true,
                FetchCountOptional: false,
                FetchCountMustBePositive: true,
                SupportsTop: true,
                SupportsBareBooleanKeywords: false,
                TypedTemporalLiteralKinds: SqlTypedTemporalLiteralKinds.None,
                SupportsTypedTemporalZoneQualifier: false,
                LexicalFeatures: SqlSourceLexicalFeatures.BracketQuotedIdentifier
                    | SqlSourceLexicalFeatures.HashPrefixedIdentifier),

            [SqlAgentToolType.Sqlite] = new(
                SqlAgentToolType.Sqlite,
                SupportsDoubleColonCast: false,
                SupportsLimitKeyword: true,
                SupportsLimitAll: false,
                SupportsCommaLimit: true,
                OffsetRequiresLimit: true,
                UsesStandardOffsetFetch: false,
                OffsetRowKeywordOptional: false,
                OffsetRequiresOrderBy: false,
                SupportsFetch: false,
                FetchRequiresPrecedingOffset: false,
                FetchCountOptional: false,
                FetchCountMustBePositive: false,
                SupportsTop: false,
                SupportsBareBooleanKeywords: true,
                TypedTemporalLiteralKinds: SqlTypedTemporalLiteralKinds.None,
                SupportsTypedTemporalZoneQualifier: false,
                LexicalFeatures: SqlSourceLexicalFeatures.BacktickQuotedIdentifier
                    | SqlSourceLexicalFeatures.BracketQuotedIdentifier),

            [SqlAgentToolType.Oracle] = new(
                SqlAgentToolType.Oracle,
                SupportsDoubleColonCast: false,
                SupportsLimitKeyword: false,
                SupportsLimitAll: false,
                SupportsCommaLimit: false,
                OffsetRequiresLimit: false,
                UsesStandardOffsetFetch: true,
                OffsetRowKeywordOptional: false,
                OffsetRequiresOrderBy: false,
                SupportsFetch: true,
                FetchRequiresPrecedingOffset: false,
                FetchCountOptional: true,
                FetchCountMustBePositive: false,
                SupportsTop: false,
                SupportsBareBooleanKeywords: true,
                TypedTemporalLiteralKinds:
                    SqlTypedTemporalLiteralKinds.Date
                    | SqlTypedTemporalLiteralKinds.Timestamp,
                SupportsTypedTemporalZoneQualifier: false,
                LexicalFeatures: SqlSourceLexicalFeatures.OracleQuotedString),

            [SqlAgentToolType.Firebird] = new(
                SqlAgentToolType.Firebird,
                SupportsDoubleColonCast: false,
                SupportsLimitKeyword: false,
                SupportsLimitAll: false,
                SupportsCommaLimit: false,
                OffsetRequiresLimit: false,
                UsesStandardOffsetFetch: true,
                OffsetRowKeywordOptional: false,
                OffsetRequiresOrderBy: false,
                SupportsFetch: true,
                FetchRequiresPrecedingOffset: false,
                FetchCountOptional: true,
                FetchCountMustBePositive: false,
                SupportsTop: false,
                SupportsBareBooleanKeywords: true,
                TypedTemporalLiteralKinds:
                    SqlTypedTemporalLiteralKinds.Date
                    | SqlTypedTemporalLiteralKinds.Time
                    | SqlTypedTemporalLiteralKinds.Timestamp,
                SupportsTypedTemporalZoneQualifier: false,
                LexicalFeatures: SqlSourceLexicalFeatures.None)
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
    bool SupportsLimitAll,
    bool SupportsCommaLimit,
    bool OffsetRequiresLimit,
    bool UsesStandardOffsetFetch,
    bool OffsetRowKeywordOptional,
    bool OffsetRequiresOrderBy,
    bool SupportsFetch,
    bool FetchRequiresPrecedingOffset,
    bool FetchCountOptional,
    bool FetchCountMustBePositive,
    bool SupportsTop,
    bool SupportsBareBooleanKeywords,
    SqlTypedTemporalLiteralKinds TypedTemporalLiteralKinds,
    bool SupportsTypedTemporalZoneQualifier,
    SqlSourceLexicalFeatures LexicalFeatures)
{
    internal bool SupportsLexicalFeature(SqlSourceLexicalFeatures feature) =>
        (LexicalFeatures & feature) != 0;

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
