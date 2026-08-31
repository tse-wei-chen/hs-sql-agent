namespace HsSqlAgent.SqlCore.Models

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Enums

[<AbstractClass; Sealed>]
type SqlQuarterDatePartCapabilityRules private () =
    static member SupportsTarget(provider: SqlAgentToolType) =
        provider = SqlAgentToolType.Postgres
    static member TargetValidationError(provider: SqlAgentToolType) =
        SqlDatePartCapabilityRules.TargetValidationError("QUARTER", provider)

[<AbstractClass; Sealed>]
type SqlCapabilityMatrix private () =
    static member Version = "2026-08-31.55"

    static member private Capability(id, category, status, detail) =
        SqlCapability(id, category, status, detail)

    static member private VersionAtLeast(profile: SqlProviderCapabilityProfile | null, provider, minimum: Version) =
        match profile with
        | null -> false
        | nonNullProfile when nonNullProfile.Provider <> provider -> false
        | nonNullProfile ->
            match nonNullProfile.ServerVersion with
            | null -> false
            | version -> version.CompareTo(minimum) >= 0

    static member ForProvider(provider: SqlAgentToolType) =
        SqlCapabilityMatrix.ForProvider(provider, null)

    static member ForProvider(provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null) =
        let profile = targetProfile
        match SqlProviderCapabilityProfileRules.ValidationIssue(profile, provider) with
        | SqlProviderCapabilityProfileValidationIssue.ProviderMismatch ->
            match profile with
            | null -> invalidOp "Provider mismatch cannot be reported for an absent target profile."
            | nonNullProfile ->
                raise (ArgumentException(
                    "Target capability profile declares provider " + string nonNullProfile.Provider
                    + ", but matrix provider is " + string provider + ".",
                    "targetProfile"))
        | SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel ->
            match profile with
            | null -> invalidOp "Negative compatibility level cannot be reported for an absent target profile."
            | nonNullProfile ->
                raise (ArgumentOutOfRangeException(
                    "targetProfile",
                    nonNullProfile.CompatibilityLevel.Value,
                    "Provider compatibility level must be non-negative."))
        | _ -> ()

        let cap = SqlCapabilityMatrix.Capability
        let translated = SqlCapabilityStatus.Translated
        let supported = SqlCapabilityStatus.Supported
        let rejected = SqlCapabilityStatus.Rejected
        let providerWide = provider <> SqlAgentToolType.Oracle && provider <> SqlAgentToolType.MsSqlServer

        let concatStatus =
            match provider with
            | SqlAgentToolType.MySQL -> translated
            | SqlAgentToolType.MsSqlServer ->
                match SqlConcatCapabilityRules.EvaluateSqlServerTarget(profile) with
                | SqlServerConcatTargetMode.Rejected -> rejected
                | SqlServerConcatTargetMode.PlusOperator -> translated
                | SqlServerConcatTargetMode.NativePipes -> supported
                | _ -> rejected
            | _ -> supported

        let rightJoinStatus =
            if provider = SqlAgentToolType.Sqlite then
                if SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(3,39)) then translated else rejected
            else translated
        let fullJoinStatus =
            if provider = SqlAgentToolType.MySQL then rejected
            elif provider = SqlAgentToolType.Sqlite then
                if SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(3,39)) then translated else rejected
            else translated

        let profileServerVersion =
            match profile with
            | null -> None
            | nonNullProfile ->
                match nonNullProfile.ServerVersion with
                | null -> None
                | version -> Some version

        let filterStatus, filterDetail =
            match provider with
            | SqlAgentToolType.Postgres ->
                match profileServerVersion with
                | Some version when version.CompareTo(Version(9,4)) < 0 ->
                    rejected,
                    "Aggregate FILTER requires PostgreSQL target ServerVersion 9.4+; the declared target version "
                    + string version + " is too old."
                | _ ->
                    supported,
                    "Native aggregate FILTER is supported by PostgreSQL 9.4+. An explicitly declared older target ServerVersion is rejected; an omitted version retains Core's current-supported-release baseline."
            | SqlAgentToolType.Sqlite ->
                match profileServerVersion with
                | None ->
                    rejected,
                    "Aggregate FILTER remains fail-closed unless the Sqlite target capability profile explicitly declares ServerVersion 3.30 or newer."
                | Some version when version.CompareTo(Version(3,30)) < 0 ->
                    rejected,
                    "Aggregate FILTER requires Sqlite target ServerVersion 3.30+; the declared target version "
                    + string version + " is too old."
                | Some version ->
                    supported,
                    "Native aggregate FILTER is enabled by the declared Sqlite target ServerVersion "
                    + string version + ", satisfying the 3.30+ runtime contract."
            | SqlAgentToolType.Firebird ->
                match profileServerVersion with
                | None ->
                    rejected,
                    "Aggregate FILTER remains fail-closed unless the Firebird target capability profile explicitly declares ServerVersion 4.0 or newer."
                | Some version when version.CompareTo(Version(4,0)) < 0 ->
                    rejected,
                    "Aggregate FILTER requires Firebird target ServerVersion 4.0+; the declared target version "
                    + string version + " is too old."
                | Some version ->
                    supported,
                    "Native aggregate FILTER is enabled by the declared Firebird target ServerVersion "
                    + string version + ", satisfying the 4.0+ runtime contract."
            | SqlAgentToolType.Oracle ->
                match profileServerVersion with
                | None ->
                    rejected,
                    "Aggregate FILTER remains fail-closed unless the Oracle target capability profile explicitly declares ServerVersion 26.0 or newer."
                | Some version when version.CompareTo(Version(26,0)) < 0 ->
                    rejected,
                    "Aggregate FILTER requires Oracle target ServerVersion 26.0+; the declared target version "
                    + string version + " is too old."
                | Some _ ->
                    supported,
                    "Oracle AI Database 26ai+ target profiles support native aggregate FILTER. Core additionally requires each FILTER condition to contain no subqueries, window functions, or outer references before Oracle lowering is authorized."
            | _ ->
                rejected,
                "Aggregate FILTER has no declared portable target contract for " + string provider + "."

        let aggregateOrderingStatus, aggregateOrderingDetail =
            match provider with
            | SqlAgentToolType.Postgres -> supported, "PostgreSQL STRING_AGG supports inline ORDER BY."
            | SqlAgentToolType.MySQL -> supported, "MySQL GROUP_CONCAT supports inline ORDER BY and SEPARATOR."
            | SqlAgentToolType.Sqlite ->
                if SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(3,44)) then supported, "SQLite 3.44+ supports aggregate-local ORDER BY."
                else rejected, "SQLite aggregate-local ORDER BY remains fail-closed unless the target capability profile explicitly declares ServerVersion 3.44 or newer."
            | SqlAgentToolType.MsSqlServer ->
                let ok =
                    match profile with
                    | null -> false
                    | nonNullProfile ->
                        SqlCapabilityMatrix.VersionAtLeast(nonNullProfile, provider, Version(14,0))
                        && nonNullProfile.CompatibilityLevel.HasValue
                        && nonNullProfile.CompatibilityLevel.Value >= 110
                if ok then supported, "SQL Server 14.0+ with CompatibilityLevel 110+ supports ordered STRING_AGG."
                else rejected, "SQL Server ordered STRING_AGG remains fail-closed unless the target capability profile explicitly declares ServerVersion 14.0+ and CompatibilityLevel 110+."
            | SqlAgentToolType.Oracle ->
                if SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(11,2)) then supported, "Oracle 11.2+ supports LISTAGG WITHIN GROUP ordering."
                else rejected, "Oracle ordered LISTAGG remains fail-closed unless the target capability profile explicitly declares ServerVersion 11.2 or newer."
            | SqlAgentToolType.Firebird -> rejected, "Firebird aggregate.string.ordering remains fail-closed."
            | _ -> rejected, "Aggregate-local ordering is not declared for this provider."

        let regexStatus, regexDetail =
            if SqlRegexCapabilityRules.SupportsTarget(provider, profile) then
                translated,
                (if provider = SqlAgentToolType.MsSqlServer
                 then "SQL Server REGEXP_LIKE is enabled by target profile ServerVersion 17.0+ and compatibility level 170+."
                 else "REGEXP_LIKE semantics are translated to provider-native regex syntax.")
            else
                rejected,
                (if provider = SqlAgentToolType.MsSqlServer
                 then "SQL Server REGEXP_LIKE requires a declared target profile with ServerVersion 17.0+ and compatibility level 170 or above."
                 else "Regex matching is rejected because no reliable native equivalent is declared.")

        let decimalStatus, decimalDetail =
            if provider <> SqlAgentToolType.Firebird then translated, "Extended exact decimal values use the provider exact-numeric contract."
            elif SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(4,0)) then translated, "Firebird 4.0+ target profiles support exact DECIMAL precision above 18."
            else rejected, "Exact Firebird decimal values requiring precision above 18 remain fail-closed unless the target capability profile explicitly declares ServerVersion 4.0 or newer."

        let offsetStatus, offsetDetail =
            match provider with
            | SqlAgentToolType.MySQL -> rejected, "MySQL has no native timestamp type that preserves an input UTC offset."
            | SqlAgentToolType.Firebird ->
                if SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(4,0))
                then translated, "Firebird 4.0+ offset timestamps lower through TIMESTAMP WITH TIME ZONE."
                else rejected, "Firebird offset timestamps require an explicit target capability profile with ServerVersion 4.0 or newer."
            | _ -> translated, "Offset timestamps use the provider declared representation."

        let nestedStatus =
            if SqlNestedCteCapabilityRules.SupportsTarget(provider) then translated else rejected

        let returningStatus =
            match provider with
            | SqlAgentToolType.Postgres -> translated
            | SqlAgentToolType.Sqlite when SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(3,35)) -> translated
            | SqlAgentToolType.Firebird when SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(5,0)) -> translated
            | _ -> rejected

        let upsertStatus =
            match provider with
            | SqlAgentToolType.Postgres -> translated
            | SqlAgentToolType.Sqlite when SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(3,24)) -> translated
            | _ -> rejected

        let jsonExtract = if provider = SqlAgentToolType.Postgres || provider = SqlAgentToolType.MySQL || provider = SqlAgentToolType.Sqlite then translated else rejected
        let jsonSet = if provider = SqlAgentToolType.Postgres || provider = SqlAgentToolType.MySQL || provider = SqlAgentToolType.Sqlite || provider = SqlAgentToolType.MsSqlServer then translated else rejected
        let booleanProjection = if providerWide then supported else rejected
        let booleanUpdate = if providerWide then translated else rejected
        let standaloneTime = if provider = SqlAgentToolType.Oracle then rejected else translated
        let currentTemporal = if provider = SqlAgentToolType.Oracle then translated else supported
        let postgresNativeDateParts = if provider = SqlAgentToolType.Postgres then supported else rejected
        let modulo = if provider = SqlAgentToolType.Oracle || provider = SqlAgentToolType.Firebird then translated else supported
        let nullOrdering = if provider = SqlAgentToolType.MySQL || provider = SqlAgentToolType.MsSqlServer then translated else supported

        let fetchWithTiesStatus, fetchWithTiesDetail =
            match provider with
            | SqlAgentToolType.Postgres ->
                match profileServerVersion with
                | Some version when version.CompareTo(SqlFetchWithTiesCapabilityRules.PostgresMinimumVersion) < 0 ->
                    rejected,
                    "FETCH ... WITH TIES requires PostgreSQL target ServerVersion 13.0+; declared version is "
                    + version.ToString() + "."
                | _ ->
                    supported,
                    "PostgreSQL 13+ FETCH ... WITH TIES is represented explicitly in the canonical Query, requires ORDER BY, "
                    + "and is emitted natively. An explicitly declared target older than 13.0 is rejected."
            | SqlAgentToolType.Oracle ->
                match profileServerVersion with
                | Some version when version.CompareTo(SqlFetchWithTiesCapabilityRules.OracleMinimumVersion) < 0 ->
                    rejected,
                    "FETCH ... WITH TIES requires Oracle target ServerVersion 12.1+; declared version is "
                    + version.ToString() + "."
                | _ ->
                    supported,
                    "Oracle 12.1+ FETCH ... WITH TIES is represented explicitly in the canonical Query, requires ORDER BY, "
                    + "and is emitted natively. An explicitly declared target older than 12.1 is rejected."
            | _ ->
                rejected,
                "SQL capability 'select.fetch_with_ties' has no proven native or semantics-preserving lowering for "
                + string provider + "."

        let lateralStatus, lateralDetail =
            match provider with
            | SqlAgentToolType.Postgres ->
                match profileServerVersion with
                | Some version when version.CompareTo(SqlLateralDerivedTableCapabilityRules.PostgresMinimumVersion) < 0 ->
                    rejected,
                    "LATERAL derived-table correlation requires PostgreSQL target ServerVersion 9.3+; declared version is "
                    + version.ToString() + "."
                | _ ->
                    supported,
                    "PostgreSQL 9.3+ LATERAL derived subqueries are represented explicitly. "
                    + "Only LATERAL sources may reference preceding FROM items; ordinary derived tables remain independent."
            | _ ->
                rejected,
                "SQL capability 'select.lateral_derived' has no proven cross-provider lowering for "
                + string provider + "."

        let capabilities : SqlCapability list =
            [
                cap("provider.target_profile","provider",supported,
                    "Core accepts optional target runtime metadata including server version, compatibility level, session modes, and session settings. Undeclared target-profile-dependent capabilities remain fail-closed; SQL Server REGEXP_LIKE is enabled only by a declared target profile at compatibility level 170+, SQL Server canonical string concatenation requires runtime proof (ServerVersion 17.x with compatibility level 170+ emits native ||; ServerVersion 14.x+ or an explicit CONCAT_NULL_YIELDS_NULL=ON contract uses +), SQLite RIGHT/FULL OUTER JOIN requires ServerVersion 3.39+, SQLite deterministic ON CONFLICT UPSERT requires ServerVersion 3.24+, SQLite DML RETURNING requires ServerVersion 3.35+, portable multi-row Firebird DSQL RETURNING requires ServerVersion 5.0+, and conditional MySQL assured ON DUPLICATE KEY UPDATE lowering requires ServerVersion 8.0.19+ so Core can use proposed-row aliases instead of deprecated VALUES(column).")
                cap("provider.source_profile","provider",supported,
                    "Raw SQL compilation accepts a separate optional source runtime profile for session-dependent and version-dependent source semantics. The source profile provider must match the parsed source dialect and never authorizes target capabilities. MySQL source || is resolved as concatenation only when PIPES_AS_CONCAT or ANSI is explicitly declared; MySQL double-quoted identifiers are accepted only when ANSI_QUOTES or ANSI is explicitly declared. MySQL backslash-containing single-quoted strings and quoted identifiers use ordinary-character semantics only when NO_BACKSLASH_ESCAPES is explicitly declared; ANSI does not imply NO_BACKSLASH_ESCAPES. Under NO_BACKSLASH_ESCAPES, raw MySQL LIKE is accepted only when the source declares an explicit single-character ESCAPE clause; omitting that contract remains fail-closed rather than guessing pattern escape semantics. Raw SQL Server || source spelling remains fail-closed, including SQL Server 2025, until the Core source parser has an explicit T-SQL 17.x precedence/grammar contract. Raw SQLite RIGHT/FULL OUTER JOIN requires source ServerVersion 3.39+, raw SQLite ON CONFLICT UPSERT requires source ServerVersion 3.24+, raw SQLite RETURNING requires source ServerVersion 3.35+, and portable multi-row Firebird DSQL RETURNING requires source ServerVersion 5.0+. Absent or unrelated modes and versions remain fail-closed rather than guessing runtime semantics.")
                cap("provider.unique_key_metadata","provider",supported,
                    "Provider metadata readers inventory PRIMARY and UNIQUE conflict sources across PostgreSQL, MySQL, SQLite, SQL Server, Oracle, and Firebird. Simple enforced full-column keys are distinguishable from partial, expression/computed, prefix, disabled/invalid, or otherwise richer key shapes, and richer enforced keys remain visible instead of being filtered out. This metadata is an assurance prerequisite only; it does not by itself authorize a SQL lowering.")
                cap("select.basic","query",translated,"SELECT/WHERE/GROUP BY/HAVING/ORDER BY and JOIN are represented structurally.")
                cap("select.distinct_on","query",(if provider = SqlAgentToolType.Postgres then supported else rejected),
                    if provider = SqlAgentToolType.Postgres then
                        "PostgreSQL DISTINCT ON expressions are represented structurally, bound and validated with the SELECT scope, and emitted natively."
                    else
                        "PostgreSQL first-row-per-group DISTINCT ON semantics have no proven cross-provider lowering and remain fail-closed.")
                cap("join.right","query",rightJoinStatus,
                    if provider = SqlAgentToolType.Sqlite && rightJoinStatus = translated then "SQLite 3.39+ RIGHT JOIN runtime contract is satisfied." else "RIGHT JOIN follows provider capability rules.")
                cap("join.full","query",fullJoinStatus,
                    if provider = SqlAgentToolType.Sqlite && fullJoinStatus = translated then "SQLite 3.39+ FULL OUTER JOIN runtime contract is satisfied." else "FULL JOIN follows provider capability rules.")
                cap("join.using","query",(if SqlUsingJoinCapabilityRules.Supports(provider) then translated else rejected),
                    if SqlUsingJoinCapabilityRules.Supports(provider) then
                        "Named-column JOIN ... USING is represented explicitly in the Core AST and emitted natively for PostgreSQL, MySQL, SQLite, Oracle, and Firebird."
                    else
                        "SQL Server has no native JOIN ... USING form; lowering to ON remains fail-closed until Core proves merged-column projection semantics.")
                cap("select.row_limit","query",translated,
                    "Structured Core row-count limits are translated to provider-native target syntax. Raw LIMIT spelling is accepted only for PostgreSQL, MySQL, and SQLite source dialects. PostgreSQL LIMIT ALL is canonicalized to no row-count limit, including LIMIT ALL OFFSET n where only the offset remains; MySQL and SQLite reject LIMIT ALL. MySQL and SQLite additionally accept native LIMIT offset,row_count and canonicalize the first integer to OFFSET and the second to LIMIT; PostgreSQL comma-form LIMIT is rejected. Raw bare OFFSET remains valid PostgreSQL syntax; MySQL and SQLite accept OFFSET only after LIMIT, and comma-form LIMIT cannot be combined with a separate OFFSET clause. PostgreSQL, Oracle, and Firebird raw source may use the modeled SQL-standard integer OFFSET ... ROW(S) and FETCH FIRST/NEXT ... ROW(S) ONLY forms, including FETCH without OFFSET; PostgreSQL may omit ROW/ROWS after OFFSET and may omit the FETCH count, which canonicalizes to one row. Explicit LIMIT and FETCH clauses remain mutually exclusive at the raw source boundary, including LIMIT ALL, matching PostgreSQL's alternative-syntax grammar. SQL Server raw OFFSET/FETCH requires statement-level ORDER BY, FETCH requires a preceding OFFSET, and TOP cannot share the same query scope. FETCH PERCENT and non-integer row-count expressions remain fail-closed. FETCH ... WITH TIES is modeled separately by select.fetch_with_ties because its result cardinality can exceed the FETCH count.")
                cap("select.fetch_with_ties","query",fetchWithTiesStatus,fetchWithTiesDetail)
                cap("select.lateral_derived","query",lateralStatus,lateralDetail)
                cap("select.singleton","query",translated,"SELECT without FROM preserves singleton-row semantics.")
                cap("select.cte_set","query",translated,"Root CTEs and set operations are represented structurally.")
                cap("set.intersect_all","query",(if provider=SqlAgentToolType.Postgres then supported else rejected),
                    if provider=SqlAgentToolType.Postgres then
                        "PostgreSQL INTERSECT ALL duplicate-preserving semantics are represented explicitly and rendered natively."
                    else
                        "Duplicate-preserving INTERSECT ALL remains fail-closed for this target provider until a provider/version-specific semantic contract is declared.")
                cap("set.except_all","query",(if provider=SqlAgentToolType.Postgres then supported else rejected),
                    if provider=SqlAgentToolType.Postgres then
                        "PostgreSQL EXCEPT ALL duplicate-preserving semantics are represented explicitly and rendered natively."
                    else
                        "Duplicate-preserving EXCEPT ALL remains fail-closed for this target provider until a provider/version-specific semantic contract is declared.")
                cap("select.cte_derived","query",nestedStatus, if nestedStatus=translated then "Derived-table-local CTEs preserve lexical scope." else "Nested CTE form remains fail-closed.")
                cap("select.cte_set_branch","query",nestedStatus, if nestedStatus=translated then "Set-operation branch CTEs preserve lexical scope." else "Nested CTE form remains fail-closed.")
                cap("select.cte_scalar_root","query",nestedStatus,
                    if nestedStatus=translated then "Scalar and EXISTS root CTE set queries preserve correlated outer references, combined output name ordering, and output ordinal ordering." else "Nested CTE form remains fail-closed.")
                cap("select.cte_definition_local","query",nestedStatus,
                    if nestedStatus=translated then
                        "A CTE body may declare its own local WITH scope. Core recursively validates and renders each nested scope directly from the canonical AST without hoisting local definitions. Same-name shadowing, positional binding order, and local set-operation bodies with outer ORDER BY/LIMIT/OFFSET are preserved; CTE definitions have no parent correlation scope in the Core binder."
                    else
                        match provider with
                        | SqlAgentToolType.Oracle -> "Oracle does not support nesting a WITH clause inside another WITH query block in the Core target profile, so CTE-definition-local WITH fails closed."
                        | SqlAgentToolType.MsSqlServer -> "SQL Server has no declared portable nested-WITH-inside-a-CTE-definition contract in the Core target profile, so this shape fails closed."
                        | _ -> "Firebird CTE-definition-local WITH remains fail-closed until a target-profile contract is modeled and integration-tested.")
                cap("select.cte_scope","query",rejected,
                    "For PostgreSQL, MySQL, and SQLite scalar/EXISTS root CTE set queries, Core preserves correlated outer scope for outer ORDER BY/LIMIT/OFFSET when ORDER BY references only combined output names or output ordinals. Richer set-result ORDER BY expressions remain fail-closed because removing the generated _set wrapper is not yet proven scope- and ordering-equivalent for those expressions. Provider-specific nested-WITH support is declared separately by select.cte_derived, select.cte_set_branch, select.cte_scalar_root, and select.cte_definition_local.")
                cap("expression.arithmetic","expression",translated,"Arithmetic operators are preserved structurally.")
                cap("numeric.decimal_extended","numeric",decimalStatus,decimalDetail)
                cap("expression.modulo","expression",modulo, if modulo=translated then "MOD(left, right) lowering preserves modulo semantics." else "Native modulo operator is supported.")
                cap("expression.concat","expression",concatStatus,
                    match provider with
                    | SqlAgentToolType.MySQL ->
                        "Canonical string concatenation is translated to CONCAT(left, right). Raw MySQL source || is accepted as concatenation only when the separate source capability profile declares PIPES_AS_CONCAT or ANSI sql_mode; without that source-session contract it remains fail-closed because MySQL otherwise interprets || as logical OR. A target profile alone never authorizes the source spelling."
                    | SqlAgentToolType.MsSqlServer ->
                        match SqlConcatCapabilityRules.EvaluateSqlServerTarget(profile) with
                        | SqlServerConcatTargetMode.NativePipes ->
                            "Declared SQL Server 2025 (17.x) / compatibility-level-170+ target emits native ANSI ||, whose NULL behavior does not depend on CONCAT_NULL_YIELDS_NULL."
                        | SqlServerConcatTargetMode.PlusOperator ->
                            "Canonical concatenation is translated to + only because the declared target proves ANSI NULL propagation through SQL Server 14.x+ or explicit CONCAT_NULL_YIELDS_NULL=ON."
                        | SqlServerConcatTargetMode.Rejected ->
                            "SQL Server concatenation is fail-closed without runtime proof: declare ServerVersion 14.0+ or CONCAT_NULL_YIELDS_NULL=ON; ServerVersion 17.0+ with CompatibilityLevel 170+ can emit native ANSI ||."
                        | _ ->
                            "Unknown SQL Server concatenation runtime mode remains fail-closed."
                    | _ ->
                        "The provider-native || operator is emitted.")
                cap("expression.like_escape","expression",translated,
                    "Explicit single-character literal LIKE ESCAPE is represented structurally and emitted for all target providers while the pattern remains parameterized. Dynamic, empty, multi-character, and control-character escape specifications fail-closed. MySQL NO_BACKSLASH_ESCAPES source requires the explicit escape contract for raw LIKE; target rendering does not rely on provider-default escape semantics.")
                cap("expression.boolean_select","expression",booleanProjection,"Boolean projection follows provider scalar-boolean capability.")
                cap("expression.boolean_literal_source","expression",translated,
                    "Structured Core boolean values remain canonical. Raw SQL Server source rejects bare TRUE/FALSE before AST canonicalization because T-SQL bit constants use 0/1 and Core does not reinterpret those bare tokens as identifiers; quoted identifiers and numeric bit predicates remain available.")
                cap("function.qualified","function",(if provider=SqlAgentToolType.Postgres then supported else rejected),
                    if provider=SqlAgentToolType.Postgres then
                        "Unquoted schema-qualified PostgreSQL function names are preserved structurally across the CLR compatibility AST and emitted natively. Quoted function identifier parts remain fail-closed until FunctionName carries identifier quoting metadata."
                    else
                        "Schema-qualified source function names remain target-gated until an equivalent provider-specific namespace contract is declared.")
                cap("expression.cast","expression",translated,
                    "Standard CAST input is normalized through a source-aware Core type model before provider-specific CAST spelling is emitted. Raw PostgreSQL :: cast spelling is accepted only when the declared source dialect is PostgreSQL; non-PostgreSQL raw sources fail before AST canonicalization. Unknown cross-dialect vendor types fail closed.")
                cap("expression.interval","expression",(if provider=SqlAgentToolType.Postgres then supported else rejected),
                    if provider=SqlAgentToolType.Postgres then
                        "PostgreSQL interval semantics are supported natively. Core canonicalizes INTERVAL 'literal' and emits the decoded interval value as a bound parameter cast to interval, so runtime data is kept out of target SQL text. Raw Core SQL accepts this PostgreSQL-style source literal only when the declared source dialect is PostgreSQL; structured Core input is independent of the raw source-syntax gate."
                    else
                        "PostgreSQL-style INTERVAL 'literal' has no declared target equivalent for this provider. Raw SQL that parses into this Core interval-literal shape is also rejected when the declared source dialect is non-PostgreSQL; provider-native interval forms such as MySQL INTERVAL expr unit require a separate structured translation contract.")
                cap("expression.filter","expression",filterStatus,filterDetail)
                cap("aggregate.string","aggregate",translated,"String aggregation lowers to provider-native syntax.")
                cap("aggregate.string.ordering","aggregate",aggregateOrderingStatus,aggregateOrderingDetail)
                cap("aggregate.string.dynamic_separator","aggregate",rejected,"Dynamic aggregate separators remain fail-closed.")
                cap("temporal.typed_literals","temporal",translated,
                    "Structured Core DATE, TIME, and TIMESTAMP values are represented as typed temporal values and bound as provider parameters. Raw typed-literal spelling is source-profiled before AST canonicalization: PostgreSQL accepts DATE, TIME, TIMESTAMP, TIMESTAMP WITH TIME ZONE, and TIMESTAMP WITHOUT TIME ZONE; TIME WITH TIME ZONE remains fail-closed because the Core scalar model has no offset-time representation. MySQL accepts the basic forms (DATE/TIME/TIMESTAMP) but not WITH/WITHOUT TIME ZONE qualifiers; SQLite rejects ANSI typed-literal spelling; Oracle accepts DATE and TIMESTAMP basic spelling but not standalone TIME or the Core TIMESTAMP WITH/WITHOUT TIME ZONE spelling; Firebird accepts basic DATE/TIME/TIMESTAMP spelling, with zone information carried inside the literal value rather than a WITH/WITHOUT TIME ZONE type qualifier; SQL Server rejects ANSI typed-literal spelling and uses string values with CAST/CONVERT instead.")
                cap("temporal.standalone_time","temporal",standaloneTime,"Standalone TIME follows provider type capability.")
                cap("temporal.offset_timestamp","temporal",offsetStatus,offsetDetail)
                cap("temporal.current_keywords","temporal",currentTemporal,"CURRENT_DATE/TIME/TIMESTAMP use provider translation where required.")
                cap("temporal.date_part.quarter","temporal",postgresNativeDateParts,
                    "QUARTER is represented canonically and currently has a declared lossless native lowering only for PostgreSQL targets.")
                cap("temporal.date_part.postgres_extended","temporal",postgresNativeDateParts,
                    "PostgreSQL-native EXTRACT fields HOUR, MINUTE, SECOND, DOW, DOY, ISODOW, ISOYEAR, WEEK, EPOCH, CENTURY, DECADE, MILLENNIUM, JULIAN, MILLISECONDS, MICROSECONDS, TIMEZONE, TIMEZONE_HOUR, and TIMEZONE_MINUTE are represented structurally. They render natively for PostgreSQL; other targets remain capability-gated until a lossless lowering is declared.")
                cap("temporal.date_arithmetic","temporal",translated,
                    "Raw SQL DATEADD/DATEDIFF input is accepted only in declared source-dialect forms, while structured Core input can use the portable date-arithmetic shapes independently of source-native syntax. Cross-dialect semantics and target-specific unit restrictions are validated before lowering.")
                cap("temporal.date_only","temporal",(if provider=SqlAgentToolType.MySQL then supported else rejected),
                    if provider = SqlAgentToolType.MySQL then
                        "MySQL DATE(expr) is canonicalized explicitly and lowered back to native DATE(expr)."
                    else
                        "MySQL DATE(expr) cross-dialect lowering remains fail-closed because untyped string coercion semantics are not proven equivalent.")
                cap("temporal.date_format","temporal",(if provider=SqlAgentToolType.Firebird then rejected else translated),"Date formatting uses declared provider lowering.")
                cap("temporal.formatted_parse","temporal",(if provider=SqlAgentToolType.Postgres || provider=SqlAgentToolType.MySQL || provider=SqlAgentToolType.Oracle then translated else rejected),"Formatted parse uses declared provider lowering.")
                cap("json.extract","json",jsonExtract,"Portable JSON extraction is provider-gated.")
                cap("json.path.simple","json",translated,"Portable JSON paths are limited to constant property chains beginning at $.")
                cap("json.set","json",jsonSet,"Portable JSON mutation is provider-gated.")
                cap("regex.match","regex",regexStatus,regexDetail)
                cap("window.basic","window",translated,"OVER with PARTITION BY and ORDER BY is represented structurally.")
                cap("window.frame","window",translated,"ROWS/RANGE frames are represented structurally.")
                cap("ordering.ordinal","ordering",translated,"Statement ORDER BY output positions are typed ordinals.")
                cap("ordering.nulls","ordering",nullOrdering,
                    if provider=SqlAgentToolType.MySQL || provider=SqlAgentToolType.MsSqlServer then
                        "Structured ASC NULLS FIRST and DESC NULLS LAST are canonicalized to the provider's identical native default ordering and the unsupported modifier is omitted. ASC NULLS LAST and DESC NULLS FIRST are translated with a CASE null-rank only when ORDER BY is a direct row-source column, including window ordering and nested DML SELECTs. DISTINCT statement tails, set-operation tails, projection alias references, and computed expressions remain fail-closed so Core does not duplicate arbitrary expression evaluation or violate provider ORDER BY select-list rules. Raw MySQL/SQL Server source syntax with NULLS modifiers is rejected at the source-dialect boundary."
                    else
                        "NULLS FIRST/LAST is emitted natively.")
                cap("parameter.unbound","parameter",rejected,"Unbound SQL parameters are rejected.")
                cap("dml.basic","dml",translated,"INSERT VALUES, UPDATE, and DELETE use the structured DML path.")
                cap("dml.update_expression","dml",translated,"UPDATE SET accepts structured scalar expressions.")
                cap("dml.target_alias","dml",(if provider=SqlAgentToolType.Postgres then supported else rejected),
                    if provider=SqlAgentToolType.Postgres then
                        "PostgreSQL UPDATE/DELETE target aliases are represented structurally, preserved across the CLR compatibility AST, participate in binder qualifier resolution, hide the original target name as PostgreSQL requires, and render natively."
                    else
                        "DML target aliases remain target-gated until an equivalent provider-specific mutation alias contract is declared.")
                cap("dml.update.from","dml",(if provider=SqlAgentToolType.Postgres then translated else rejected),"UPDATE FROM is currently PostgreSQL-only.")
                cap("dml.update.boolean_assignment","dml",booleanUpdate,"Boolean UPDATE assignment follows scalar-boolean capability.")
                cap("dml.delete.using","dml",(if provider=SqlAgentToolType.Postgres then translated else rejected),"DELETE USING is currently PostgreSQL-only.")
                cap("dml.insert_select","dml",translated,"INSERT SELECT is supported for statically-known source width.")
                cap("dml.insert_select.cte_scope","dml",translated,"Statement-root CTE INSERT SELECT placement is provider-aware.")
                cap("dml.nested_cte_scope","dml",nestedStatus,
                    if nestedStatus=translated then "Nested DML CTEs use scope-preserving direct lowering, including output ordinal ordering." else "Nested DML CTE scope remains fail-closed.")
                cap("dml.advanced","dml",rejected,
                    "Portable column-only DML RETURNING is tracked separately by dml.returning_output, and deterministic explicit-target INSERT conflict handling is tracked by dml.upsert_merge. Firebird metadata-assured UPDATE OR INSERT is also tracked by dml.upsert_merge; general MERGE, MySQL any-unique-key ON DUPLICATE KEY lowering without a sole-enforced-key equivalence proof, arbitrary conflict-update expressions, and INSERT ... SELECT upsert remain outside the portable DML contract.")
                cap("dml.returning_output","dml",returningStatus,
                    if returningStatus=translated then
                        match provider with
                        | SqlAgentToolType.Postgres ->
                            "PostgreSQL RETURNING preserves SELECT-like output lists through the structured DML path, including mixed wildcard/column outputs, qualified target columns, and the proven scalar/predicate expression subset over binder-resolved local target/FROM/USING row sources. Subqueries, windows, correlated outer references, and unproven functions remain fail-closed. Result-producing mutations are materialized through the DML execution boundary, and the returned-row count must still match the approved affected-row count before commit."
                        | SqlAgentToolType.Sqlite ->
                            "SQLite ServerVersion 3.35+ target profiles may return unqualified target columns or a lone wildcard through native RETURNING. The explicit target version is required; returned-row count remains part of approval revalidation before commit."
                        | SqlAgentToolType.Firebird ->
                            "Firebird ServerVersion 5.0+ target profiles may use the portable multi-row DSQL RETURNING contract for unqualified target columns or a lone wildcard. The explicit target version is required; returned-row count remains part of approval revalidation before commit."
                        | _ -> "DML RETURNING result rows are enabled by the provider/runtime contract."
                    else
                        match provider with
                        | SqlAgentToolType.Sqlite ->
                            "SQLite DML RETURNING remains fail-closed unless the target capability profile explicitly declares ServerVersion 3.35 or newer."
                        | SqlAgentToolType.Firebird ->
                            "Portable multi-row Firebird DSQL RETURNING remains fail-closed unless the target capability profile explicitly declares ServerVersion 5.0 or newer."
                        | SqlAgentToolType.MsSqlServer ->
                            "SQL Server OUTPUT without INTO is trigger-sensitive. Core does not yet carry target-table trigger capability metadata, so result rows remain fail-closed instead of assuming OUTPUT can be returned directly to the client."
                        | SqlAgentToolType.Oracle ->
                            "Oracle DML RETURNING requires RETURNING INTO host or bind variables, which are outside the Core result-row execution contract."
                        | SqlAgentToolType.MySQL ->
                            "MySQL has no declared INSERT/UPDATE/DELETE RETURNING result-row equivalent in the Core MySQL 8.4 target profile."
                        | _ -> "DML RETURNING result rows remain fail-closed.")
                cap("dml.upsert_merge","dml",upsertStatus,
                    if upsertStatus=translated then
                        if provider=SqlAgentToolType.Postgres then
                            "PostgreSQL supports the deterministic Core INSERT VALUES conflict contract with an explicit conflict-column target. DO NOTHING permits multiple proposed rows; DO UPDATE is limited to exactly one proposed row and closed assignments of the form target = EXCLUDED.source. Arbitrary expressions, predicates, named constraints, partial-index predicates, INSERT ... SELECT upsert, and typed approval execution remain fail-closed."
                        else
                            "SQLite ServerVersion 3.24+ target profiles support the deterministic Core INSERT VALUES conflict contract with an explicit conflict-column target. DO NOTHING permits multiple proposed rows; DO UPDATE is limited to exactly one proposed row and target = EXCLUDED.source assignments. The target version must be explicit; richer SQLite UPSERT grammar and typed approval execution remain fail-closed."
                    else
                        match provider with
                        | SqlAgentToolType.Sqlite ->
                            "SQLite UPSERT remains fail-closed unless the target capability profile explicitly declares ServerVersion 3.24 or newer."
                        | SqlAgentToolType.MySQL ->
                            "MySQL ON DUPLICATE KEY UPDATE can fire on any UNIQUE or PRIMARY KEY and has no explicit conflict target. Core inventories provider-native enforced unique keys, including richer partial/expression/prefix shapes. The compiler has a conditional single-row DO UPDATE path only when an explicit ServerVersion 8.0.19+ target profile and statement-level assurance prove the matched explicit conflict target is the sole enforced native conflict source; it uses a proposed-row alias rather than deprecated VALUES(column). Because this capability matrix has no per-statement assurance input, the default capability remains Rejected and fail-closed; DO NOTHING, multiple native conflict sources, richer unsupported enforced unique sources, and typed approval execution remain rejected."
                        | SqlAgentToolType.Firebird ->
                            "Firebird raw UPDATE OR INSERT ... MATCHING is canonicalized only with an explicit MATCHING column list. Firebird target lowering is available only when DmlConflictTargetAssurance proves that the canonical conflict target equals the complete resolved primary key and the conflict update mirrors every supplied INSERT column as the same proposed-row column. Because this capability matrix has no per-statement primary-key assurance input, the default Firebird capability remains Rejected and fail-closed; DO NOTHING, partial updates, general UNIQUE-key matching, and general MERGE remain rejected."
                        | _ ->
                            "This provider requires MERGE-style source and match semantics. Core has not yet modeled the source-row cardinality and match guarantees needed for a portable MERGE contract, so upsert remains fail-closed.")
            ]

        ProviderSqlCapabilities(
            SqlCapabilityMatrix.Version,
            provider,
            (capabilities |> List.toArray :> IReadOnlyList<SqlCapability>))
