#nowarn "3261" "3262"

namespace HsSqlAgent.SqlCore.Models

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Globalization
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Core.Compilation

type internal SqlServerConcatTargetMode =
    | Rejected = 0
    | PlusOperator = 1
    | NativePipes = 2

type internal SqlAggregateFilterPredicateFeature =
    | OuterReference = 0
    | Subquery = 1
    | WindowFunction = 2

type internal SqlCurrentTemporalKind =
    | Date = 0
    | Time = 1
    | Timestamp = 2

[<Flags>]
type internal SqlSourceLexicalFeatures =
    | None = 0
    | HashLineComment = 1
    | DashDashCommentRequiresSeparator = 2
    | PostgresEscapeString = 4
    | PostgresDollarQuotedString = 8
    | OracleQuotedString = 16
    | DoubleQuotedIdentifierRequiresAnsiMode = 32
    | BacktickQuotedIdentifier = 64
    | BracketQuotedIdentifier = 128
    | HashPrefixedIdentifier = 256
    | BackslashSensitiveQuotedText = 512

type internal SqlSourceRowLimitGrammar =
    { SupportsLimitKeyword: bool
      SupportsLimitAll: bool
      SupportsCommaLimit: bool
      OffsetRequiresLimit: bool
      UsesStandardOffsetFetch: bool
      OffsetRowKeywordOptional: bool
      OffsetRequiresOrderBy: bool
      SupportsFetch: bool
      FetchRequiresPrecedingOffset: bool
      FetchCountOptional: bool
      FetchCountMustBePositive: bool
      SupportsTop: bool }

[<Sealed>]
type internal SqlSourceDialectGrammarContract(
    lexicalFeatures: SqlSourceLexicalFeatures,
    rowLimit: SqlSourceRowLimitGrammar) =
    member _.LexicalFeatures = lexicalFeatures
    member _.RowLimit = rowLimit
    member _.SupportsLexicalFeature(feature: SqlSourceLexicalFeatures) =
        (int lexicalFeatures &&& int feature) <> 0

module internal SqlSourceDialectGrammarRules =
    let For(sourceDialect: SqlAgentToolType) =
        let flags, rowLimit =
            match sourceDialect with
            | SqlAgentToolType.Postgres ->
                (SqlSourceLexicalFeatures.PostgresEscapeString ||| SqlSourceLexicalFeatures.PostgresDollarQuotedString),
                { SupportsLimitKeyword = true
                  SupportsLimitAll = true
                  SupportsCommaLimit = false
                  OffsetRequiresLimit = false
                  UsesStandardOffsetFetch = true
                  OffsetRowKeywordOptional = true
                  OffsetRequiresOrderBy = false
                  SupportsFetch = true
                  FetchRequiresPrecedingOffset = false
                  FetchCountOptional = true
                  FetchCountMustBePositive = false
                  SupportsTop = false }
            | SqlAgentToolType.MySQL ->
                (SqlSourceLexicalFeatures.HashLineComment
                 ||| SqlSourceLexicalFeatures.DashDashCommentRequiresSeparator
                 ||| SqlSourceLexicalFeatures.DoubleQuotedIdentifierRequiresAnsiMode
                 ||| SqlSourceLexicalFeatures.BacktickQuotedIdentifier
                 ||| SqlSourceLexicalFeatures.BackslashSensitiveQuotedText),
                { SupportsLimitKeyword = true
                  SupportsLimitAll = false
                  SupportsCommaLimit = true
                  OffsetRequiresLimit = true
                  UsesStandardOffsetFetch = false
                  OffsetRowKeywordOptional = false
                  OffsetRequiresOrderBy = false
                  SupportsFetch = false
                  FetchRequiresPrecedingOffset = false
                  FetchCountOptional = false
                  FetchCountMustBePositive = false
                  SupportsTop = false }
            | SqlAgentToolType.MsSqlServer ->
                (SqlSourceLexicalFeatures.BracketQuotedIdentifier ||| SqlSourceLexicalFeatures.HashPrefixedIdentifier),
                { SupportsLimitKeyword = false
                  SupportsLimitAll = false
                  SupportsCommaLimit = false
                  OffsetRequiresLimit = false
                  UsesStandardOffsetFetch = true
                  OffsetRowKeywordOptional = false
                  OffsetRequiresOrderBy = true
                  SupportsFetch = true
                  FetchRequiresPrecedingOffset = true
                  FetchCountOptional = false
                  FetchCountMustBePositive = true
                  SupportsTop = true }
            | SqlAgentToolType.Sqlite ->
                (SqlSourceLexicalFeatures.BacktickQuotedIdentifier ||| SqlSourceLexicalFeatures.BracketQuotedIdentifier),
                { SupportsLimitKeyword = true
                  SupportsLimitAll = false
                  SupportsCommaLimit = true
                  OffsetRequiresLimit = true
                  UsesStandardOffsetFetch = false
                  OffsetRowKeywordOptional = false
                  OffsetRequiresOrderBy = false
                  SupportsFetch = false
                  FetchRequiresPrecedingOffset = false
                  FetchCountOptional = false
                  FetchCountMustBePositive = false
                  SupportsTop = false }
            | SqlAgentToolType.Oracle ->
                SqlSourceLexicalFeatures.OracleQuotedString,
                { SupportsLimitKeyword = false
                  SupportsLimitAll = false
                  SupportsCommaLimit = false
                  OffsetRequiresLimit = false
                  UsesStandardOffsetFetch = true
                  OffsetRowKeywordOptional = false
                  OffsetRequiresOrderBy = false
                  SupportsFetch = true
                  FetchRequiresPrecedingOffset = false
                  FetchCountOptional = true
                  FetchCountMustBePositive = false
                  SupportsTop = false }
            | SqlAgentToolType.Firebird ->
                SqlSourceLexicalFeatures.None,
                { SupportsLimitKeyword = false
                  SupportsLimitAll = false
                  SupportsCommaLimit = false
                  OffsetRequiresLimit = false
                  UsesStandardOffsetFetch = true
                  OffsetRowKeywordOptional = false
                  OffsetRequiresOrderBy = false
                  SupportsFetch = true
                  FetchRequiresPrecedingOffset = false
                  FetchCountOptional = true
                  FetchCountMustBePositive = false
                  SupportsTop = false }
            | value -> raise (ArgumentOutOfRangeException("sourceDialect", value, "No source grammar contract."))
        SqlSourceDialectGrammarContract(flags, rowLimit)

    let UsesMySqlAnsiQuotedIdentifiers(sourceDialect: SqlAgentToolType, sourceProfile: SqlProviderCapabilityProfile | null) =
        sourceDialect = SqlAgentToolType.MySQL
        && not (isNull sourceProfile)
        && sourceProfile.Provider = SqlAgentToolType.MySQL
        && (sourceProfile.HasSessionMode("ANSI_QUOTES") || sourceProfile.HasSessionMode("ANSI"))

    let UsesMySqlNoBackslashEscapes(sourceDialect: SqlAgentToolType, sourceProfile: SqlProviderCapabilityProfile | null) =
        sourceDialect = SqlAgentToolType.MySQL
        && not (isNull sourceProfile)
        && sourceProfile.Provider = SqlAgentToolType.MySQL
        && sourceProfile.HasSessionMode("NO_BACKSLASH_ESCAPES")

module internal SqlConcatCapabilityRules =
    let private v14 = Version(14,0)
    let private v17 = Version(17,0)

    let SupportsMySqlPipesAsConcat(sourceDialect: SqlAgentToolType, sourceProfile: SqlProviderCapabilityProfile | null) =
        sourceDialect = SqlAgentToolType.MySQL
        && not (isNull sourceProfile)
        && sourceProfile.Provider = SqlAgentToolType.MySQL
        && (sourceProfile.HasSessionMode("PIPES_AS_CONCAT") || sourceProfile.HasSessionMode("ANSI"))

    let SourceSemanticValidationError(sourceDialect: SqlAgentToolType) : string | null =
        if sourceDialect = SqlAgentToolType.MySQL then
            "MySQL '||' semantics depend on PIPES_AS_CONCAT sql_mode; Core rejects the operator because session sql_mode is not part of the compilation plan."
        else null

    let RawSourceSyntaxError(sourceDialect: SqlAgentToolType) : string | null =
        if sourceDialect = SqlAgentToolType.MsSqlServer then
            "Raw SQL Server source operator '||' remains fail-closed. SQL Server 2025 (17.x) introduces ANSI pipes concatenation, but Core has not yet declared a T-SQL 17.x source grammar/precedence contract."
        else null

    let EvaluateSqlServerTarget(targetProfile: SqlProviderCapabilityProfile | null) =
        if isNull targetProfile || targetProfile.Provider <> SqlAgentToolType.MsSqlServer then SqlServerConcatTargetMode.Rejected
        elif not (isNull targetProfile.ServerVersion)
             && targetProfile.ServerVersion.CompareTo(v17) >= 0
             && targetProfile.CompatibilityLevel.HasValue
             && targetProfile.CompatibilityLevel.Value >= 170 then SqlServerConcatTargetMode.NativePipes
        elif not (isNull targetProfile.ServerVersion) && targetProfile.ServerVersion.CompareTo(v14) >= 0 then SqlServerConcatTargetMode.PlusOperator
        else
            let setting = targetProfile.GetSessionSetting("CONCAT_NULL_YIELDS_NULL")
            if String.Equals(setting, "ON", StringComparison.OrdinalIgnoreCase) then SqlServerConcatTargetMode.PlusOperator
            else SqlServerConcatTargetMode.Rejected

    let SqlServerTargetValidationError(targetProfile: SqlProviderCapabilityProfile | null) : string | null =
        let version = if isNull targetProfile || isNull targetProfile.ServerVersion then "undeclared" else targetProfile.ServerVersion.ToString()
        let compatibility = if isNull targetProfile || not targetProfile.CompatibilityLevel.HasValue then "undeclared" else string targetProfile.CompatibilityLevel.Value
        let concatNull = if isNull targetProfile then "undeclared" else targetProfile.GetSessionSetting("CONCAT_NULL_YIELDS_NULL") |> Option.ofObj |> Option.defaultValue "undeclared"
        "SQL capability 'expression.concat' for SQL Server requires declared runtime proof. "
        + "ServerVersion 17.0+ with CompatibilityLevel 170+ uses native ANSI ||; "
        + "ServerVersion 14.0+ uses + because CONCAT_NULL_YIELDS_NULL is always ON; "
        + "older or undeclared versions require SessionSettings['CONCAT_NULL_YIELDS_NULL']='ON'. "
        + "Declared profile: ServerVersion=" + version + ", CompatibilityLevel=" + compatibility + ", CONCAT_NULL_YIELDS_NULL=" + concatNull + "."

module internal SqlIlikeCapabilityRules =
    let SupportsTarget(provider: SqlAgentToolType) = provider = SqlAgentToolType.Postgres
    let SourceValidationError(sourceDialect: SqlAgentToolType) : string | null =
        if sourceDialect = SqlAgentToolType.Postgres then null
        else "ILIKE is PostgreSQL-specific and is not valid for source dialect " + string sourceDialect + "."

module internal SqlIntervalLiteralCapabilityRules =
    let IsTargetSupported(provider: SqlAgentToolType) = provider = SqlAgentToolType.Postgres
    let SourceValidationError(sourceDialect: SqlAgentToolType) : string | null =
        if sourceDialect = SqlAgentToolType.Postgres then null
        else
            "INTERVAL 'literal' is not valid for declared source dialect " + string sourceDialect
            + " in the Core source capability profile. Core models this interval-literal shape as PostgreSQL source syntax; other dialect interval forms require their own structured translation contract."

module internal SqlQualifiedFunctionCapabilityRules =
    let SourceValidationError(sourceDialect: SqlAgentToolType) : string | null =
        if sourceDialect = SqlAgentToolType.Postgres then null
        else "SQL capability 'function.qualified' is currently declared only for the PostgreSQL source dialect; source dialect "
             + string sourceDialect + " remains fail-closed."
    let TargetValidationError(provider: SqlAgentToolType) : string | null =
        if provider = SqlAgentToolType.Postgres then null
        else "SQL capability 'function.qualified' currently has a declared lossless lowering only for PostgreSQL targets; target provider "
             + string provider + " remains fail-closed."

module internal SqlModuloCapabilityRules =
    let private usesFunction provider = provider = SqlAgentToolType.Oracle || provider = SqlAgentToolType.Firebird
    let SourceValidationError(sourceDialect: SqlAgentToolType) : string | null =
        if usesFunction sourceDialect then
            "Operator '%' is not valid portable source syntax for " + string sourceDialect + "; use the provider's MOD function instead."
        else null

module internal SqlNullOrderingCapabilityRules =
    let RequiresTargetRewrite(provider: SqlAgentToolType) =
        provider = SqlAgentToolType.MySQL || provider = SqlAgentToolType.MsSqlServer

    let SourceValidationError(sourceDialect: SqlAgentToolType, nullOrdering: HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind) : string | null =
        if nullOrdering = HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.Default || not (RequiresTargetRewrite sourceDialect) then null
        else
            let modifier = if nullOrdering = HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.First then "NULLS FIRST" else "NULLS LAST"
            "ORDER BY modifier '" + modifier + "' is not valid for declared source dialect "
            + string sourceDialect + " in the Core source capability profile."

module internal SqlStandaloneTimeCapabilityRules =
    let TargetValidationError(provider: SqlAgentToolType) : string | null =
        if provider <> SqlAgentToolType.Oracle then null
        else "Oracle has no standalone TIME data type. SQL capability 'literal.time' is not supported by provider Oracle for this Core plan."

module internal SqlDmlTargetAliasCapabilityRules =
    let SourceValidationError(sourceDialect: SqlAgentToolType) : string | null =
        if sourceDialect = SqlAgentToolType.Postgres then null
        else "SQL capability 'dml.target_alias' is currently declared only for the PostgreSQL source dialect; source dialect "
             + string sourceDialect + " remains fail-closed."
    let TargetValidationError(provider: SqlAgentToolType) : string | null =
        if provider = SqlAgentToolType.Postgres then null
        else "SQL capability 'dml.target_alias' currently has a declared lossless lowering only for PostgreSQL targets; target provider "
             + string provider + " remains fail-closed."

module internal SqlDmlUpdateFromCapabilityRules =
    let TargetValidationError(provider: SqlAgentToolType) : string | null =
        if provider = SqlAgentToolType.Postgres then null
        else "SQL capability 'dml.update.from' remains fail-closed for provider " + string provider
             + "; equivalent mutation, duplicate-match, alias, and runtime-version semantics are not yet proven."

module internal SqlDmlDeleteUsingCapabilityRules =
    let TargetValidationError(provider: SqlAgentToolType) : string | null =
        if provider = SqlAgentToolType.Postgres then null
        else "SQL capability 'dml.delete.using' remains fail-closed for provider " + string provider
             + "; equivalent joined-delete, target-row, alias, and duplicate-match semantics are not yet proven."

module internal SqlDmlReturningExpressionCapabilityRules =
    let SourceValidationError(sourceDialect: SqlAgentToolType) : string | null =
        if sourceDialect = SqlAgentToolType.Postgres then null
        else "SQL capability 'dml.returning.expression' is currently declared only for the PostgreSQL source dialect; source dialect "
             + string sourceDialect + " remains fail-closed."
    let TargetValidationError(provider: SqlAgentToolType) : string | null =
        if provider = SqlAgentToolType.Postgres then null
        else "SQL capability 'dml.returning.expression' is currently lowered only for PostgreSQL targets; target provider "
             + string provider + " remains fail-closed."

module internal SqlDmlReturningCapabilityRules =
    let private sqliteVersion = Version(3,35)
    let private firebirdVersion = Version(5,0)
    let private atLeast (actual: Version) required = not (isNull actual) && actual.CompareTo(required) >= 0

    let SourceValidationError(sourceDialect: SqlAgentToolType, sourceServerVersion: Version | null) : string | null =
        let supported =
            match sourceDialect with
            | SqlAgentToolType.Postgres -> true
            | SqlAgentToolType.Sqlite -> atLeast sourceServerVersion sqliteVersion
            | SqlAgentToolType.Firebird -> atLeast sourceServerVersion firebirdVersion
            | _ -> false
        if supported then null
        else
            match sourceDialect with
            | SqlAgentToolType.Sqlite -> "Raw SQLite RETURNING requires a source capability profile with ServerVersion 3.35 or newer."
            | SqlAgentToolType.Firebird -> "Portable multi-row Firebird DSQL RETURNING requires a source capability profile with ServerVersion 5.0 or newer."
            | SqlAgentToolType.MsSqlServer -> "SQL Server uses OUTPUT rather than RETURNING; trigger-sensitive OUTPUT result semantics are not yet represented by the portable Core DML contract."
            | SqlAgentToolType.Oracle -> "Oracle RETURNING requires RETURNING INTO host or bind variables, which are not represented by the portable Core DML result-row contract."
            | SqlAgentToolType.MySQL -> "MySQL has no declared DML RETURNING result-row syntax in the Core MySQL 8.4 source profile."
            | _ -> "DML RETURNING is not represented for source dialect " + string sourceDialect + "."

    let TargetValidationError(provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null) : string | null =
        let supported =
            match provider with
            | SqlAgentToolType.Postgres -> true
            | SqlAgentToolType.Sqlite ->
                not (isNull targetProfile) && targetProfile.Provider = provider && atLeast targetProfile.ServerVersion sqliteVersion
            | SqlAgentToolType.Firebird ->
                not (isNull targetProfile) && targetProfile.Provider = provider && atLeast targetProfile.ServerVersion firebirdVersion
            | _ -> false
        if supported then null
        else
            match provider with
            | SqlAgentToolType.Sqlite -> "SQLite DML RETURNING requires an explicit target capability profile with ServerVersion 3.35 or newer."
            | SqlAgentToolType.Firebird -> "Portable multi-row Firebird DSQL RETURNING requires an explicit target capability profile with ServerVersion 5.0 or newer."
            | SqlAgentToolType.MsSqlServer -> "SQL Server OUTPUT without INTO is trigger-sensitive and Core has no target-table trigger capability metadata; DML result rows remain fail-closed for SQL Server."
            | SqlAgentToolType.Oracle -> "Oracle DML RETURNING requires RETURNING INTO host or bind variables, which are not represented by the Core result-row execution contract."
            | SqlAgentToolType.MySQL -> "MySQL has no declared DML RETURNING result-row equivalent in the Core MySQL 8.4 target profile."
            | _ -> "DML result rows are not represented for target provider " + string provider + "."

module internal SqlDmlUpsertCapabilityRules =
    let private sqliteVersion = Version(3,24)
    let private mysqlAliasVersion = Version(8,0,19)
    let private atLeastVersion (actual: Version) required = not (isNull actual) && actual.CompareTo(required) >= 0
    let private profileAtLeast (profile: SqlProviderCapabilityProfile | null) provider required =
        not (isNull profile) && profile.Provider = provider && atLeastVersion profile.ServerVersion required

    let OnConflictSourceValidationError(sourceDialect: SqlAgentToolType, sourceServerVersion: Version | null) : string | null =
        let supported =
            sourceDialect = SqlAgentToolType.Postgres
            || (sourceDialect = SqlAgentToolType.Sqlite && atLeastVersion sourceServerVersion sqliteVersion)
        if supported then null
        else
            match sourceDialect with
            | SqlAgentToolType.Sqlite -> "Raw SQLite UPSERT requires a source capability profile with ServerVersion 3.24 or newer."
            | SqlAgentToolType.MySQL -> "MySQL ON DUPLICATE KEY UPDATE has no explicit conflict target and is not represented by the deterministic portable upsert contract."
            | SqlAgentToolType.Firebird -> "Firebird source upsert uses UPDATE OR INSERT ... MATCHING rather than ON CONFLICT; use the native explicit MATCHING form so Core can preserve source semantics."
            | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Oracle ->
                "Source dialect " + string sourceDialect + " uses MERGE-style upsert semantics, which require a separate source-row cardinality contract and remain fail-closed."
            | _ -> "Portable INSERT conflict handling is not represented for source dialect " + string sourceDialect + "."

    let DirectTargetValidationError(provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null) : string | null =
        let supported = provider = SqlAgentToolType.Postgres || (provider = SqlAgentToolType.Sqlite && profileAtLeast targetProfile provider sqliteVersion)
        if supported then null
        else
            match provider with
            | SqlAgentToolType.Sqlite -> "SQLite UPSERT requires an explicit target capability profile with ServerVersion 3.24 or newer."
            | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Oracle ->
                "Target provider " + string provider + " requires MERGE-style source/match semantics; portable MERGE remains fail-closed until Core models source-row cardinality and match guarantees."
            | SqlAgentToolType.MySQL | SqlAgentToolType.Firebird ->
                "Portable INSERT conflict handling is not represented as an unconditional target capability for provider " + string provider + "."
            | _ -> "Portable INSERT conflict handling is not represented for target provider " + string provider + "."

    let MySqlConditionalTargetValidationError(targetProfile: SqlProviderCapabilityProfile | null) : string | null =
        if profileAtLeast targetProfile SqlAgentToolType.MySQL mysqlAliasVersion then null
        else "MySQL conflict lowering requires an explicit target capability profile with ServerVersion 8.0.19 or newer so Core can use the proposed-row alias form instead of deprecated VALUES(column) semantics."

module internal SqlJoinCapabilityRules =
    let private sqliteMin = Version(3,39)
    let private normalize (kind: string) = kind.Trim().ToUpperInvariant()
    let private sqliteError kind (profile: SqlProviderCapabilityProfile | null) side =
        let cap = if kind = "RIGHT" then "join.right" else "join.full"
        if isNull profile || isNull profile.ServerVersion then
            "SQL capability '" + cap + "' requires a declared SQLite " + side + " capability profile with ServerVersion 3.39+."
        elif profile.ServerVersion.CompareTo(sqliteMin) < 0 then
            "SQL capability '" + cap + "' requires SQLite " + side + " ServerVersion 3.39+; declared version is " + profile.ServerVersion.ToString() + "."
        else null

    let SourceValidationError(joinKind: string, sourceDialect: SqlAgentToolType, sourceProfile: SqlProviderCapabilityProfile | null) : string | null =
        let kind = normalize joinKind
        if kind = "FULL" && sourceDialect = SqlAgentToolType.MySQL then
            "Raw MySQL FULL OUTER JOIN is not valid source syntax. SQL capability 'join.full' is not supported by source provider MySQL."
        elif sourceDialect = SqlAgentToolType.Sqlite && (kind = "RIGHT" || kind = "FULL") then sqliteError kind sourceProfile "source"
        else null

    let TargetValidationError(joinKind: string, provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null) : string | null =
        let kind = normalize joinKind
        if kind = "FULL" && provider = SqlAgentToolType.MySQL then
            "SQL capability 'join.full' is not supported by provider MySQL for this Core plan."
        elif provider = SqlAgentToolType.Sqlite && (kind = "RIGHT" || kind = "FULL") then sqliteError kind targetProfile "target"
        else null

module internal SqlUsingJoinCapabilityRules =
    let Supports(provider: SqlAgentToolType) =
        provider <> SqlAgentToolType.MsSqlServer

    let SourceValidationError(provider: SqlAgentToolType) : string | null =
        if Supports(provider) then null
        else
            "JOIN ... USING is not valid Transact-SQL source syntax. SQL capability 'join.using' is not supported by source provider MsSqlServer."

    let TargetValidationError(provider: SqlAgentToolType) : string | null =
        if Supports(provider) then null
        else
            "SQL capability 'join.using' is not supported by provider MsSqlServer because Transact-SQL has no native JOIN ... USING form and Core does not lower named-column join semantics to ON without a proven merged-column equivalence."

module internal SqlAggregateFilterCapabilityRules =
    let private pg = Version(9,4)
    let private sqlite = Version(3,30)
    let private firebird = Version(4,0)
    let private oracle = Version(26,0)

    let private minimum provider =
        match provider with
        | SqlAgentToolType.Postgres -> Some pg
        | SqlAgentToolType.Sqlite -> Some sqlite
        | SqlAgentToolType.Firebird -> Some firebird
        | SqlAgentToolType.Oracle -> Some oracle
        | _ -> None

    let RawSourceSyntaxError(sourceDialect: SqlAgentToolType) : string | null =
        match minimum sourceDialect with
        | Some _ -> null
        | None -> "Aggregate FILTER (WHERE ...) is not valid for declared source dialect " + string sourceDialect + " in the Core source capability profile."

    let ValidationError(provider: SqlAgentToolType, profile: SqlProviderCapabilityProfile | null, side: string) : string | null =
        match minimum provider with
        | None -> "SQL capability 'expression.filter' is not supported by provider " + string provider + " for " + side + " SQL."
        | Some min when provider = SqlAgentToolType.Postgres ->
            if not (isNull profile) && not (isNull profile.ServerVersion) && profile.ServerVersion.CompareTo(min) < 0 then
                "SQL capability 'expression.filter' requires Postgres " + side + " ServerVersion 9.4+; declared version is " + profile.ServerVersion.ToString() + "."
            else null
        | Some min ->
            if isNull profile || isNull profile.ServerVersion then
                "SQL capability 'expression.filter' requires a declared " + string provider + " " + side + " capability profile with ServerVersion " + min.ToString() + "+."
            elif profile.ServerVersion.CompareTo(min) < 0 then
                "SQL capability 'expression.filter' requires " + string provider + " " + side + " ServerVersion " + min.ToString() + "+; declared version is " + profile.ServerVersion.ToString() + "."
            else null

    let PredicateValidationError(provider: SqlAgentToolType, side: string, feature: SqlAggregateFilterPredicateFeature) : string | null =
        if provider <> SqlAgentToolType.Oracle then null
        else
            let restriction =
                match feature with
                | SqlAggregateFilterPredicateFeature.OuterReference -> "outer references"
                | SqlAggregateFilterPredicateFeature.Subquery -> "subqueries"
                | SqlAggregateFilterPredicateFeature.WindowFunction -> "window functions"
                | _ -> "unsupported predicate features"
            "SQL capability 'expression.filter' requires an Oracle 26ai " + side + " FILTER condition without " + restriction + "."

module internal SqlFirebirdTimeZoneTypeCapabilityRules =
    let MinimumVersion = Version(4,0)
    let SupportsTargetProfile(targetProfile: SqlProviderCapabilityProfile | null) =
        not (isNull targetProfile)
        && targetProfile.Provider = SqlAgentToolType.Firebird
        && not (isNull targetProfile.ServerVersion)
        && targetProfile.ServerVersion.CompareTo(MinimumVersion) >= 0

    let CastTargetValidationError(provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null, typeName: string) : string | null =
        let normalized = String.Join(" ", typeName.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries))
        let timezoneType =
            normalized.EndsWith(" WITH TIME ZONE", StringComparison.Ordinal)
            && (normalized.StartsWith("TIME", StringComparison.Ordinal) || normalized.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        if provider <> SqlAgentToolType.Firebird || not timezoneType || SupportsTargetProfile(targetProfile) then null
        else
            "SQL capability 'temporal.firebird_time_zone_type' requires an explicit Firebird target capability profile with ServerVersion 4.0 or newer for CAST target type '"
            + typeName + "' because TIME WITH TIME ZONE and TIMESTAMP WITH TIME ZONE were introduced in Firebird 4.0."

module internal SqlOffsetTimestampCapabilityRules =
    let TargetValidationError(provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null) : string | null =
        let supported =
            match provider with
            | SqlAgentToolType.MySQL -> false
            | SqlAgentToolType.Firebird -> SqlFirebirdTimeZoneTypeCapabilityRules.SupportsTargetProfile(targetProfile)
            | _ -> true
        if supported then null
        elif provider = SqlAgentToolType.MySQL then
            "SQL capability 'temporal.offset_timestamp' is not supported by MySQL because it has no native timestamp type that preserves an input UTC offset."
        elif provider = SqlAgentToolType.Firebird then
            "SQL capability 'temporal.offset_timestamp' requires an explicit Firebird target capability profile with ServerVersion 4.0 or newer because TIMESTAMP WITH TIME ZONE was introduced in Firebird 4.0."
        else "SQL capability 'temporal.offset_timestamp' is not supported by the declared target profile."

module internal SqlRegexCapabilityRules =
    let private minVersion = Version(17,0)
    let SupportsTarget(provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null) =
        match provider with
        | SqlAgentToolType.Postgres | SqlAgentToolType.MySQL | SqlAgentToolType.Oracle -> true
        | SqlAgentToolType.MsSqlServer ->
            not (isNull targetProfile)
            && targetProfile.Provider = SqlAgentToolType.MsSqlServer
            && not (isNull targetProfile.ServerVersion)
            && targetProfile.ServerVersion.CompareTo(minVersion) >= 0
            && targetProfile.CompatibilityLevel.HasValue
            && targetProfile.CompatibilityLevel.Value >= 170
        | _ -> false

    let ProviderValidationError(provider: SqlAgentToolType) : string | null =
        if provider = SqlAgentToolType.Sqlite || provider = SqlAgentToolType.Firebird then
            "SQL capability 'function.regex_match' is not supported by provider " + string provider + " for this Core plan."
        else null

    let TargetValidationError(provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null) : string | null =
        if SupportsTarget(provider, targetProfile) then null
        elif provider = SqlAgentToolType.MsSqlServer then
            "SQL capability 'function.regex_match' requires a declared SQL Server target capability profile with ServerVersion 17.0 or newer and compatibility level 170 or above."
        else "SQL capability 'function.regex_match' is not supported by provider " + string provider + " for this Core plan."

module internal SqlScalarBooleanCapabilityRules =
    let TargetValidationError(provider: SqlAgentToolType, capability: string) : string | null =
        if provider <> SqlAgentToolType.Oracle && provider <> SqlAgentToolType.MsSqlServer then null
        else "SQL capability '" + capability + "' is not supported by provider " + string provider + " for this Core plan."

module internal SqlNestedCteCapabilityRules =
    let SupportsTarget(provider: SqlAgentToolType) =
        provider = SqlAgentToolType.Postgres || provider = SqlAgentToolType.MySQL || provider = SqlAgentToolType.Sqlite

module internal SqlTemporalFormatCapabilityRules =
    let TargetValidationError(canonicalFunctionName: string, provider: SqlAgentToolType) : string | null =
        match canonicalFunctionName with
        | "CORE_DATE_FORMAT" when provider <> SqlAgentToolType.Firebird -> null
        | "CORE_DATE_FORMAT" ->
            "portable date formatting is not supported by Firebird. SQL capability 'function.date_format' is not supported by provider "
            + string provider + " for this Core plan."
        | "CORE_DATE_PARSE" when provider = SqlAgentToolType.Postgres || provider = SqlAgentToolType.MySQL || provider = SqlAgentToolType.Oracle -> null
        | "CORE_DATE_PARSE" ->
            "formatted date parsing is not supported by this provider. SQL capability 'function.date_parse' is not supported by provider "
            + string provider + " for this Core plan."
        | value -> raise (ArgumentOutOfRangeException("canonicalFunctionName", value, "Unsupported canonical temporal format function."))

module internal SqlDateOnlyCapabilityRules =
    let IsMySqlSourceFunction(sourceDialect: SqlAgentToolType, functionName: string) =
        sourceDialect = SqlAgentToolType.MySQL
        && String.Equals(functionName.Trim(), "DATE", StringComparison.OrdinalIgnoreCase)

    let SourceValidationError(sourceDialect: SqlAgentToolType, functionName: string, argumentCount: int) : string | null =
        if not (IsMySqlSourceFunction(sourceDialect, functionName)) then null
        elif argumentCount = 1 then null
        else "MySQL DATE(expr) requires exactly 1 argument in the Core source capability profile."

    let TargetValidationError(provider: SqlAgentToolType) : string | null =
        if provider = SqlAgentToolType.MySQL then null
        else
            "SQL capability 'temporal.date_only' currently preserves MySQL DATE(expr) semantics only for MySQL targets. Cross-dialect lowering remains fail-closed until Core can prove the operand is a temporal value rather than a provider-specific string coercion."

module internal SqlJsonCapabilityRules =
    let TargetValidationError(canonicalFunctionName: string, provider: SqlAgentToolType) : string | null =
        match canonicalFunctionName with
        | "CORE_JSON_EXTRACT" when provider = SqlAgentToolType.Postgres || provider = SqlAgentToolType.MySQL || provider = SqlAgentToolType.Sqlite -> null
        | "CORE_JSON_EXTRACT" -> "SQL capability 'function.json_extract' is not supported by provider " + string provider + " for this Core plan."
        | "CORE_JSON_SET" when provider <> SqlAgentToolType.Oracle && provider <> SqlAgentToolType.Firebird -> null
        | "CORE_JSON_SET" -> "SQL capability 'function.json_set' is not supported by provider " + string provider + " for this Core plan."
        | value -> raise (ArgumentOutOfRangeException("canonicalFunctionName", value, "Unsupported canonical JSON function."))

module internal SqlWindowCapabilityRules =
    let SupportsAggregateInWindowSpecification(provider: SqlAgentToolType) = provider = SqlAgentToolType.Postgres
    let FunctionValidationError(functionName: string, provider: SqlAgentToolType) : string | null =
        if provider = SqlAgentToolType.MsSqlServer then
            "SQL capability 'function." + functionName.Trim().ToLowerInvariant() + "' is not supported by provider MsSqlServer for this Core plan."
        else null
    let LiteralOffsetValidationError(functionName: string, offset: int64, provider: SqlAgentToolType) : string | null =
        if offset < 0L && (provider = SqlAgentToolType.MsSqlServer || provider = SqlAgentToolType.MySQL) then
            "SQL capability 'function." + functionName.Trim().ToLowerInvariant() + ".negative_offset' is not supported by provider "
            + string provider + " for this Core plan."
        else null

module internal SqlCurrentTemporalCapabilityRules =
    let SourceValidationError(kind: SqlCurrentTemporalKind, sourceDialect: SqlAgentToolType) : string | null =
        let supported =
            match kind with
            | SqlCurrentTemporalKind.Date -> sourceDialect <> SqlAgentToolType.MsSqlServer
            | SqlCurrentTemporalKind.Time -> sourceDialect <> SqlAgentToolType.MsSqlServer && sourceDialect <> SqlAgentToolType.Oracle
            | SqlCurrentTemporalKind.Timestamp -> true
            | _ -> false
        if supported then null
        else
            let fn = match kind with SqlCurrentTemporalKind.Date -> "CURRENT_DATE" | SqlCurrentTemporalKind.Time -> "CURRENT_TIME" | _ -> "CURRENT_TIMESTAMP"
            let dialect =
                if sourceDialect = SqlAgentToolType.MsSqlServer then "MsSqlServer (Transact-SQL / T-SQL)"
                else string sourceDialect
            "Function '" + fn + "' is not valid for declared source dialect " + dialect + " in the Core source capability profile."

    let TargetValidationError(kind: SqlCurrentTemporalKind, provider: SqlAgentToolType) : string | null =
        if kind <> SqlCurrentTemporalKind.Time || provider <> SqlAgentToolType.Oracle then null
        else "SQL capability 'function.current_time' is not supported by provider Oracle for this Core plan."

module internal SqlDatePartCapabilityRules =
    let private normalize (rawPart: string) = rawPart.Trim().ToUpperInvariant()

    let private portableParts =
        set [ "YEAR"; "MONTH"; "DAY" ]

    let private postgresNativeParts =
        set [
            "QUARTER"
            "HOUR"; "MINUTE"; "SECOND"
            "DOW"; "DOY"; "ISODOW"; "ISOYEAR"; "WEEK"
            "EPOCH"; "CENTURY"; "DECADE"; "MILLENNIUM"; "JULIAN"
            "MILLISECONDS"; "MICROSECONDS"
            "TIMEZONE"; "TIMEZONE_HOUR"; "TIMEZONE_MINUTE"
        ]

    let IsRepresentedPart(rawPart: string) =
        let part = normalize rawPart
        Set.contains part portableParts || Set.contains part postgresNativeParts

    let TargetValidationError(rawPart: string, provider: SqlAgentToolType) : string | null =
        let part = normalize rawPart
        let supported =
            Set.contains part portableParts
            || (provider = SqlAgentToolType.Postgres && Set.contains part postgresNativeParts)
        if supported then null
        elif not (IsRepresentedPart part) then
            "Date part " + part + " is outside the declared Core date-part family. SQL capability 'temporal.date_part."
            + part.ToLowerInvariant() + "' is not supported by provider " + string provider + " for this Core plan."
        else
            "SQL capability 'temporal.date_part." + part.ToLowerInvariant()
            + "' is represented by Core but does not yet have a declared lossless lowering for provider "
            + string provider + "."

module internal SqlDateMathCapabilityRules =
    let NormalizeUnit(rawUnit: string, surfaceName: string) =
        match rawUnit.Trim().ToUpperInvariant() with
        | "DAY" | "DD" | "D" -> "DAY"
        | "WEEK" | "WK" | "WW" -> "WEEK"
        | "MONTH" | "MM" | "M" -> "MONTH"
        | "QUARTER" | "QQ" | "Q" -> "QUARTER"
        | "YEAR" | "YY" | "YYYY" -> "YEAR"
        | "HOUR" | "HH" -> "HOUR"
        | "MINUTE" | "MI" | "N" -> "MINUTE"
        | "SECOND" | "SS" | "S" -> "SECOND"
        | _ -> raise (SqlCompilationException("Unsupported " + surfaceName + " date-part unit '" + rawUnit + "'."))

    let TargetValidationError(rawUnit: string, provider: SqlAgentToolType, functionName: string) : string | null =
        let surface = if functionName = "CORE_DATE_ADD" then "DATEADD" elif functionName = "CORE_DATE_DIFF" then "DATEDIFF" else functionName
        let unit = NormalizeUnit(rawUnit, surface)
        let supported =
            match provider with
            | SqlAgentToolType.Postgres | SqlAgentToolType.Oracle | SqlAgentToolType.Sqlite -> unit = "DAY"
            | SqlAgentToolType.Firebird -> unit <> "QUARTER"
            | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer -> true
            | _ -> false
        if supported then null
        else surface + " unit " + unit + " is not supported by " + string provider + ". SQL capability '"
             + functionName.ToLowerInvariant() + ".unit." + unit.ToLowerInvariant() + "' is not supported by provider "
             + string provider + " for this Core plan."

[<Struct>]
type internal SqlDecimalShape =
    { Precision: int
      Scale: int }

module internal SqlFirebirdDecimalCapabilityRules =
    let LegacyMaximumPrecision = 18
    let Shape(value: decimal) =
        let text = Math.Abs(value).ToString("0.############################", CultureInfo.InvariantCulture)
        let separator = text.IndexOf('.')
        let integerPart = if separator < 0 then text else text.Substring(0, separator)
        let fractionalPart = if separator < 0 then String.Empty else text.Substring(separator + 1)
        let integerDigits = integerPart.TrimStart('0').Length
        let scale = fractionalPart.Length
        { Precision = max 1 (integerDigits + scale); Scale = scale }
    let FirebirdCastType(value: decimal) =
        let shape = Shape(value)
        "DECIMAL(" + string shape.Precision + "," + string shape.Scale + ")"

type internal SqlCanonicalFunctionKind =
    | Scalar = 0
    | Aggregate = 1
    | Window = 2

type internal SqlCanonicalTargetCapabilityFamily =
    | None = 0
    | WindowFunction = 1
    | TemporalFormat = 2
    | Json = 3
    | Regex = 4
    | DatePart = 5
    | DateMath = 6
    | CurrentTemporal = 7
    | DateOnly = 8

type internal SqlCanonicalPlanShapeValidationKind =
    | DistinctWildcardForbidden = 0
    | LiteralStringRequired = 1

type internal SqlCanonicalLiteralArgumentValidationKind =
    | PositiveInteger = 0
    | WindowOffset = 1

[<Sealed>]
type internal SqlCanonicalPlanShapeRule(kind, argumentIndex, validationMessage: string | null, capabilityId: string | null) =
    member _.Kind: SqlCanonicalPlanShapeValidationKind = kind
    member _.ArgumentIndex = argumentIndex
    member _.ValidationMessage = validationMessage
    member _.CapabilityId = capabilityId

[<Sealed>]
type internal SqlCanonicalLiteralArgumentRule(argumentIndex, kind, validationMessage: string | null) =
    member _.ArgumentIndex = argumentIndex
    member _.Kind: SqlCanonicalLiteralArgumentValidationKind = kind
    member _.ValidationMessage = validationMessage

[<Sealed>]
type internal SqlCanonicalFunctionContract(
    name: string,
    minArguments: int,
    maxArguments: int,
    kind: SqlCanonicalFunctionKind,
    allowDistinct: bool,
    allowFilter: bool,
    allowWindow: bool,
    requireWindow: bool,
    isDirectPortable: bool) =
    member _.Name = name
    member _.MinArguments = minArguments
    member _.MaxArguments = maxArguments
    member _.Kind = kind
    member _.AllowDistinct = allowDistinct
    member _.AllowFilter = allowFilter
    member _.AllowWindow = allowWindow
    member _.RequireWindow = requireWindow
    member _.IsDirectPortable = isDirectPortable
    member val TargetCapabilityFamily = SqlCanonicalTargetCapabilityFamily.None with get, set
    member val LiteralArgumentRules = ImmutableArray<SqlCanonicalLiteralArgumentRule>.Empty with get, set
    member val PlanShapeRules = ImmutableArray<SqlCanonicalPlanShapeRule>.Empty with get, set
    member val IsWindowFrameInsensitive = false with get, set
    member val CurrentTemporalKind = Nullable<SqlCurrentTemporalKind>() with get, set
    member _.AcceptsArgumentCount(argumentCount: int) = argumentCount >= minArguments && argumentCount <= maxArguments

module internal SqlCanonicalFunctionRegistry =
    let private scalar name minArgs maxArgs direct =
        SqlCanonicalFunctionContract(name,minArgs,maxArgs,SqlCanonicalFunctionKind.Scalar,false,false,false,false,direct)
    let private aggregate name args =
        SqlCanonicalFunctionContract(name,args,args,SqlCanonicalFunctionKind.Aggregate,true,true,true,false,true)
    let private window name minArgs maxArgs frameInsensitive =
        let c = SqlCanonicalFunctionContract(name,minArgs,maxArgs,SqlCanonicalFunctionKind.Window,false,false,true,true,true)
        c.IsWindowFrameInsensitive <- frameInsensitive
        c

    let private contracts =
        let d = Dictionary<string, SqlCanonicalFunctionContract>(StringComparer.OrdinalIgnoreCase)
        let add (c: SqlCanonicalFunctionContract) = d[c.Name] <- c
        [ scalar "ABS" 1 1 true
          scalar "ROUND" 1 2 true
          scalar "LOWER" 1 1 true
          scalar "UPPER" 1 1 true
          scalar "TRIM" 1 1 true
          scalar "LTRIM" 1 1 true
          scalar "RTRIM" 1 1 true
          scalar "NULLIF" 2 2 true
          aggregate "AVG" 1
          aggregate "COUNT" 1
          aggregate "MAX" 1
          aggregate "MIN" 1
          aggregate "SUM" 1
          window "ROW_NUMBER" 0 0 true
          window "RANK" 0 0 true
          window "DENSE_RANK" 0 0 true
          window "PERCENT_RANK" 0 0 true
          window "CUME_DIST" 0 0 true
          window "LAG" 1 3 true
          window "LEAD" 1 3 true
          window "FIRST_VALUE" 1 1 false
          window "LAST_VALUE" 1 1 false
          window "NTH_VALUE" 2 2 false
          window "NTILE" 1 1 true
          scalar "CORE_DATE_ADD" 3 3 false
          scalar "CORE_DATE_DIFF" 3 3 false
          scalar "CORE_DATE_PART" 2 2 false
          scalar "CORE_DATE_FORMAT" 2 2 false
          scalar "CORE_DATE_PARSE" 2 2 false
          scalar "CORE_DATE_ONLY" 1 1 false
          scalar "CORE_POSITION" 2 2 false
          scalar "CORE_JSON_EXTRACT" 2 2 false
          scalar "CORE_JSON_SET" 3 3 false
          scalar "CORE_REGEX_MATCH" 2 2 false
          scalar "CORE_CURRENT_DATE" 0 0 false
          scalar "CORE_CURRENT_TIME" 0 0 false
          scalar "CORE_CURRENT_TIMESTAMP" 0 0 false
          SqlCanonicalFunctionContract("CORE_STRING_AGG",2,2,SqlCanonicalFunctionKind.Aggregate,false,true,false,false,false) ]
        |> List.iter add

        d["COUNT"].PlanShapeRules <-
            ImmutableArray.Create(
                SqlCanonicalPlanShapeRule(
                    SqlCanonicalPlanShapeValidationKind.DistinctWildcardForbidden,
                    0,
                    "COUNT(DISTINCT *) is not a valid Core aggregate shape.",
                    null))
        d["NTH_VALUE"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.WindowFunction
        d["NTH_VALUE"].LiteralArgumentRules <-
            ImmutableArray.Create(SqlCanonicalLiteralArgumentRule(1, SqlCanonicalLiteralArgumentValidationKind.PositiveInteger, "NTH_VALUE index must be a positive integer."))
        d["NTILE"].LiteralArgumentRules <-
            ImmutableArray.Create(SqlCanonicalLiteralArgumentRule(0, SqlCanonicalLiteralArgumentValidationKind.PositiveInteger, "NTILE bucket count must be a positive integer."))
        d["LAG"].LiteralArgumentRules <- ImmutableArray.Create(SqlCanonicalLiteralArgumentRule(1, SqlCanonicalLiteralArgumentValidationKind.WindowOffset, null))
        d["LEAD"].LiteralArgumentRules <- ImmutableArray.Create(SqlCanonicalLiteralArgumentRule(1, SqlCanonicalLiteralArgumentValidationKind.WindowOffset, null))
        d["CORE_DATE_ADD"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.DateMath
        d["CORE_DATE_DIFF"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.DateMath
        d["CORE_DATE_PART"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.DatePart
        d["CORE_DATE_FORMAT"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.TemporalFormat
        d["CORE_DATE_PARSE"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.TemporalFormat
        d["CORE_DATE_ONLY"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.DateOnly
        d["CORE_JSON_EXTRACT"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.Json
        d["CORE_JSON_SET"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.Json
        d["CORE_REGEX_MATCH"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.Regex
        d["CORE_CURRENT_DATE"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.CurrentTemporal
        d["CORE_CURRENT_DATE"].CurrentTemporalKind <- Nullable(SqlCurrentTemporalKind.Date)
        d["CORE_CURRENT_TIME"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.CurrentTemporal
        d["CORE_CURRENT_TIME"].CurrentTemporalKind <- Nullable(SqlCurrentTemporalKind.Time)
        d["CORE_CURRENT_TIMESTAMP"].TargetCapabilityFamily <- SqlCanonicalTargetCapabilityFamily.CurrentTemporal
        d["CORE_CURRENT_TIMESTAMP"].CurrentTemporalKind <- Nullable(SqlCurrentTemporalKind.Timestamp)
        d["CORE_STRING_AGG"].PlanShapeRules <-
            ImmutableArray.Create(SqlCanonicalPlanShapeRule(SqlCanonicalPlanShapeValidationKind.LiteralStringRequired,1,null,"aggregate.string.dynamic_separator"))
        d

    let Find(name: string) : SqlCanonicalFunctionContract | null =
        if String.IsNullOrWhiteSpace(name) then null
        else
            match contracts.TryGetValue(name.Trim()) with
            | true, value -> value
            | _ -> null
    let IsDirectPortable(name: string) =
        let c = Find(name)
        not (isNull c) && c.IsDirectPortable
    let IsAggregate(name: string) =
        let c = Find(name)
        not (isNull c) && c.Kind = SqlCanonicalFunctionKind.Aggregate
    let IsWindow(name: string) =
        let c = Find(name)
        not (isNull c) && c.Kind = SqlCanonicalFunctionKind.Window

type internal SqlSourceFunctionCanonicalizationKind =
    | DateAdd = 0
    | DateDiff = 1
    | DateFormat = 2
    | DateParse = 3
    | Position = 4
    | JsonExtract = 5
    | JsonSet = 6
    | RegexMatch = 7
    | CurrentTimestamp = 8
    | StringAggregate = 9

[<Sealed>]
type internal SqlSourceFunctionDialectRule(dialect, minArguments, maxArguments: Nullable<int>, supportsSeparator: bool) =
    member _.Dialect: SqlAgentToolType = dialect
    member _.MinArguments = minArguments
    member _.MaxArguments = maxArguments
    member _.SupportsAggregateSeparatorClause = supportsSeparator
    member _.Accepts(value: SqlAgentToolType, argumentCount: int) =
        value = dialect && argumentCount >= minArguments && (not maxArguments.HasValue || argumentCount <= maxArguments.Value)

[<Sealed>]
type internal SqlSourceFunctionContract(name, kind, detail: string, rules: IReadOnlyList<SqlSourceFunctionDialectRule>) =
    member _.Name = name
    member _.CanonicalizationKind: SqlSourceFunctionCanonicalizationKind = kind
    member _.Detail = detail
    member _.DialectRules = rules
    member _.ValidationError(sourceDialect: SqlAgentToolType, argumentCount: int) : string | null =
        if rules |> Seq.exists (fun r -> r.Accepts(sourceDialect, argumentCount)) then null
        else "Function '" + name + "' is not valid for declared source dialect " + string sourceDialect + " in the Core source capability profile. " + detail
    member _.SupportsAggregateSeparatorClause(sourceDialect: SqlAgentToolType) =
        rules |> Seq.exists (fun r -> r.Dialect = sourceDialect && r.SupportsAggregateSeparatorClause)

module internal SqlSourceFunctionRegistry =
    let private exact dialect count = SqlSourceFunctionDialectRule(dialect,count,Nullable(count),false)
    let private range dialect lo hi = SqlSourceFunctionDialectRule(dialect,lo,Nullable(hi),false)
    let private any dialect = SqlSourceFunctionDialectRule(dialect,0,Nullable(),false)
    let private anySep dialect = SqlSourceFunctionDialectRule(dialect,0,Nullable(),true)
    let private contract name kind detail rules =
        SqlSourceFunctionContract(name,kind,detail,List<SqlSourceFunctionDialectRule>(rules :> seq<SqlSourceFunctionDialectRule>) :> IReadOnlyList<_>)
    let private data =
        [ contract "DATEADD" SqlSourceFunctionCanonicalizationKind.DateAdd "DATEADD is modeled as a three-argument SQL Server/Firebird source function." [exact SqlAgentToolType.MsSqlServer 3; exact SqlAgentToolType.Firebird 3]
          contract "DATEDIFF" SqlSourceFunctionCanonicalizationKind.DateDiff "DATEDIFF is modeled as SQL Server/Firebird (3 arguments) or MySQL (2 arguments) source syntax." [exact SqlAgentToolType.MsSqlServer 3; exact SqlAgentToolType.Firebird 3; exact SqlAgentToolType.MySQL 2]
          contract "DATE_FORMAT" SqlSourceFunctionCanonicalizationKind.DateFormat "DATE_FORMAT is modeled as MySQL source syntax." [any SqlAgentToolType.MySQL]
          contract "FORMAT" SqlSourceFunctionCanonicalizationKind.DateFormat "Core models FORMAT as SQL Server date-format syntax; MySQL/SQLite FORMAT functions have different semantics." [any SqlAgentToolType.MsSqlServer]
          contract "TO_DATE" SqlSourceFunctionCanonicalizationKind.DateParse "TO_DATE is modeled only for PostgreSQL and Oracle source syntax." [any SqlAgentToolType.Postgres; any SqlAgentToolType.Oracle]
          contract "CHARINDEX" SqlSourceFunctionCanonicalizationKind.Position "CHARINDEX is modeled as MsSqlServer source syntax." [any SqlAgentToolType.MsSqlServer]
          contract "LOCATE" SqlSourceFunctionCanonicalizationKind.Position "LOCATE is modeled as MySQL source syntax." [any SqlAgentToolType.MySQL]
          contract "STRPOS" SqlSourceFunctionCanonicalizationKind.Position "STRPOS is modeled as Postgres source syntax." [any SqlAgentToolType.Postgres]
          contract "INSTR" SqlSourceFunctionCanonicalizationKind.Position "INSTR is modeled for MySQL, SQLite, and Oracle source syntax." [any SqlAgentToolType.MySQL; any SqlAgentToolType.Sqlite; any SqlAgentToolType.Oracle]
          contract "JSON_EXTRACT" SqlSourceFunctionCanonicalizationKind.JsonExtract "JSON_EXTRACT is modeled for MySQL and SQLite source syntax." [any SqlAgentToolType.MySQL; any SqlAgentToolType.Sqlite]
          contract "JSON_SET" SqlSourceFunctionCanonicalizationKind.JsonSet "JSON_SET is modeled for MySQL and SQLite source syntax." [any SqlAgentToolType.MySQL; any SqlAgentToolType.Sqlite]
          contract "REGEXP_LIKE" SqlSourceFunctionCanonicalizationKind.RegexMatch "REGEXP_LIKE is modeled for MySQL, Oracle, and SQL Server 2025+ source syntax." [any SqlAgentToolType.MySQL; any SqlAgentToolType.Oracle; any SqlAgentToolType.MsSqlServer]
          contract "GETDATE" SqlSourceFunctionCanonicalizationKind.CurrentTimestamp "GETDATE is modeled as MsSqlServer source syntax." [any SqlAgentToolType.MsSqlServer]
          contract "NOW" SqlSourceFunctionCanonicalizationKind.CurrentTimestamp "NOW is modeled for PostgreSQL and MySQL source syntax." [any SqlAgentToolType.Postgres; any SqlAgentToolType.MySQL]
          contract "STRING_AGG" SqlSourceFunctionCanonicalizationKind.StringAggregate "STRING_AGG is modeled as a two-argument PostgreSQL/SQL Server source function." [exact SqlAgentToolType.Postgres 2; exact SqlAgentToolType.MsSqlServer 2]
          contract "GROUP_CONCAT" SqlSourceFunctionCanonicalizationKind.StringAggregate "GROUP_CONCAT is modeled for MySQL source syntax and SQLite with one or two arguments; the SEPARATOR clause is MySQL-only." [anySep SqlAgentToolType.MySQL; range SqlAgentToolType.Sqlite 1 2]
          contract "LISTAGG" SqlSourceFunctionCanonicalizationKind.StringAggregate "LISTAGG is modeled for Oracle source syntax with one or two arguments." [range SqlAgentToolType.Oracle 1 2]
          contract "LIST" SqlSourceFunctionCanonicalizationKind.StringAggregate "LIST is modeled for Firebird source syntax with one or two arguments." [range SqlAgentToolType.Firebird 1 2] ]
    let Find(name: string) : SqlSourceFunctionContract | null =
        if String.IsNullOrWhiteSpace(name) then null
        else data |> List.tryFind (fun c -> String.Equals(c.Name,name.Trim(),StringComparison.OrdinalIgnoreCase)) |> Option.toObj
