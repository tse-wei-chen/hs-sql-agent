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
    static member Version = "2026-08-28.53"

    static member private Capability(id, category, status, detail) =
        SqlCapability(id, category, status, detail)

    static member private VersionAtLeast(profile: SqlProviderCapabilityProfile, provider, minimum: Version) =
        not (isNull profile)
        && profile.Provider = provider
        && not (isNull profile.ServerVersion)
        && profile.ServerVersion.CompareTo(minimum) >= 0

    static member ForProvider(provider: SqlAgentToolType, ?targetProfile: SqlProviderCapabilityProfile) =
        let profile = defaultArg targetProfile null
        match SqlProviderCapabilityProfileRules.ValidationIssue(profile, provider) with
        | SqlProviderCapabilityProfileValidationIssue.ProviderMismatch ->
            raise (ArgumentException(
                "Target capability profile declares provider " + string profile.Provider
                + ", but matrix provider is " + string provider + ".",
                "targetProfile"))
        | SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel ->
            raise (ArgumentOutOfRangeException(
                "targetProfile",
                profile.CompatibilityLevel.Value,
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
                if SqlConcatCapabilityRules.EvaluateSqlServerTarget(profile) = SqlServerConcatTargetMode.Rejected then rejected else translated
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

        let filterStatus =
            match provider with
            | SqlAgentToolType.Postgres ->
                if not (isNull profile) && not (isNull profile.ServerVersion) && profile.ServerVersion.CompareTo(Version(9,4)) < 0 then rejected else supported
            | SqlAgentToolType.Sqlite -> if SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(3,30)) then supported else rejected
            | SqlAgentToolType.Firebird -> if SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(4,0)) then supported else rejected
            | SqlAgentToolType.Oracle -> if SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(26,0)) then supported else rejected
            | _ -> rejected

        let aggregateOrderingStatus, aggregateOrderingDetail =
            match provider with
            | SqlAgentToolType.Postgres -> supported, "PostgreSQL STRING_AGG supports inline ORDER BY."
            | SqlAgentToolType.MySQL -> supported, "MySQL GROUP_CONCAT supports inline ORDER BY and SEPARATOR."
            | SqlAgentToolType.Sqlite ->
                if SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(3,44)) then supported, "SQLite 3.44+ supports aggregate-local ORDER BY."
                else rejected, "SQLite aggregate-local ORDER BY remains fail-closed unless the target capability profile explicitly declares ServerVersion 3.44 or newer."
            | SqlAgentToolType.MsSqlServer ->
                let ok =
                    SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(14,0))
                    && profile.CompatibilityLevel.HasValue
                    && profile.CompatibilityLevel.Value >= 110
                if ok then supported, "SQL Server 14.0+ with CompatibilityLevel 110+ supports ordered STRING_AGG."
                else rejected, "SQL Server ordered STRING_AGG remains fail-closed unless the target capability profile explicitly declares ServerVersion 14.0+ and CompatibilityLevel 110+."
            | SqlAgentToolType.Oracle ->
                if SqlCapabilityMatrix.VersionAtLeast(profile, provider, Version(11,2)) then supported, "Oracle 11.2+ supports LISTAGG WITHIN GROUP ordering."
                else rejected, "Oracle ordered LISTAGG remains fail-closed unless the target capability profile explicitly declares ServerVersion 11.2 or newer."
            | SqlAgentToolType.Firebird -> rejected, "Firebird aggregate.string.ordering remains fail-closed."

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
        let quarter = if provider = SqlAgentToolType.Postgres then supported else rejected
        let modulo = if provider = SqlAgentToolType.Oracle || provider = SqlAgentToolType.Firebird then translated else supported
        let nullOrdering = if provider = SqlAgentToolType.MySQL || provider = SqlAgentToolType.MsSqlServer then translated else supported

        let capabilities : SqlCapability list =
            [
                cap("provider.target_profile","provider",supported,
                    "Core accepts optional target runtime metadata including server version, compatibility level, session modes, and session settings.")
                cap("provider.source_profile","provider",supported,
                    "Raw SQL compilation accepts a separate optional source runtime profile; absent profile-dependent capabilities remain fail-closed.")
                cap("provider.unique_key_metadata","provider",supported,"Provider metadata inventories enforced unique-key sources.")
                cap("select.basic","query",translated,"SELECT/WHERE/GROUP BY/HAVING/ORDER BY and JOIN are represented structurally.")
                cap("join.right","query",rightJoinStatus,
                    if provider = SqlAgentToolType.Sqlite && rightJoinStatus = translated then "SQLite 3.39+ RIGHT JOIN runtime contract is satisfied." else "RIGHT JOIN follows provider capability rules.")
                cap("join.full","query",fullJoinStatus,
                    if provider = SqlAgentToolType.Sqlite && fullJoinStatus = translated then "SQLite 3.39+ FULL OUTER JOIN runtime contract is satisfied." else "FULL JOIN follows provider capability rules.")
                cap("select.row_limit","query",translated,"Row-count limits are translated to provider-native syntax.")
                cap("select.singleton","query",translated,"SELECT without FROM preserves singleton-row semantics.")
                cap("select.cte_set","query",translated,"Root CTEs and set operations are represented structurally.")
                cap("select.cte_derived","query",nestedStatus, if nestedStatus=translated then "Derived-table-local CTEs preserve lexical scope." else "Nested CTE form remains fail-closed.")
                cap("select.cte_set_branch","query",nestedStatus, if nestedStatus=translated then "Set-operation branch CTEs preserve lexical scope." else "Nested CTE form remains fail-closed.")
                cap("select.cte_scalar_root","query",nestedStatus,
                    if nestedStatus=translated then "Scalar and EXISTS root CTE set queries preserve correlated outer references, combined output name ordering, and output ordinal ordering." else "Nested CTE form remains fail-closed.")
                cap("select.cte_definition_local","query",nestedStatus, if nestedStatus=translated then "CTE-definition-local WITH preserves nested scope." else "Nested CTE form remains fail-closed.")
                cap("select.cte_scope","query",rejected,"Richer set-result ORDER BY expressions remain fail-closed under the scope-preserving subset.")
                cap("expression.arithmetic","expression",translated,"Arithmetic operators are preserved structurally.")
                cap("numeric.decimal_extended","numeric",decimalStatus,decimalDetail)
                cap("expression.modulo","expression",modulo, if modulo=translated then "MOD(left, right) lowering preserves modulo semantics." else "Native modulo operator is supported.")
                cap("expression.concat","expression",concatStatus, if provider=SqlAgentToolType.MySQL then "Canonical concatenation lowers through CONCAT(left, right)." else "Canonical concatenation uses the declared provider contract.")
                cap("expression.like_escape","expression",translated,"Explicit LIKE ESCAPE is represented structurally.")
                cap("expression.boolean_select","expression",booleanProjection,"Boolean projection follows provider scalar-boolean capability.")
                cap("expression.boolean_literal_source","expression",translated,"Raw boolean literal source syntax is dialect-profiled.")
                cap("expression.cast","expression",translated,"CAST is normalized through a source-aware type model.")
                cap("expression.interval_literal","expression", if provider=SqlAgentToolType.Postgres then supported else rejected,"Portable interval-literal support is provider-gated.")
                cap("expression.filter","expression",filterStatus,"Aggregate FILTER is gated by provider/runtime profile.")
                cap("aggregate.string","aggregate",translated,"String aggregation lowers to provider-native syntax.")
                cap("aggregate.string.ordering","aggregate",aggregateOrderingStatus,aggregateOrderingDetail)
                cap("aggregate.string.dynamic_separator","aggregate",rejected,"Dynamic aggregate separators remain fail-closed.")
                cap("temporal.typed_literals","temporal",translated,"Typed temporal literals and values are normalized structurally.")
                cap("temporal.standalone_time","temporal",standaloneTime,"Standalone TIME follows provider type capability.")
                cap("temporal.offset_timestamp","temporal",offsetStatus,offsetDetail)
                cap("temporal.current_keywords","temporal",currentTemporal,"CURRENT_DATE/TIME/TIMESTAMP use provider translation where required.")
                cap("temporal.date_part.quarter","temporal",quarter,"QUARTER is in the declared portable subset only for PostgreSQL targets.")
                cap("temporal.date_arithmetic","temporal",translated,"DATEADD/DATEDIFF forms are normalized and target-unit restrictions are validated before lowering.")
                cap("temporal.date_format","temporal",if provider=SqlAgentToolType.Firebird then rejected else translated,"Date formatting uses declared provider lowering.")
                cap("temporal.formatted_parse","temporal",if provider=SqlAgentToolType.Postgres || provider=SqlAgentToolType.MySQL || provider=SqlAgentToolType.Oracle then translated else rejected,"Formatted parse uses declared provider lowering.")
                cap("json.extract","json",jsonExtract,"Portable JSON extraction is provider-gated.")
                cap("json.path.simple","json",translated,"Portable JSON paths are limited to constant property chains beginning at $.")
                cap("json.set","json",jsonSet,"Portable JSON mutation is provider-gated.")
                cap("regex.match","regex",regexStatus,regexDetail)
                cap("window.basic","window",translated,"OVER with PARTITION BY and ORDER BY is represented structurally.")
                cap("window.frame","window",translated,"ROWS/RANGE frames are represented structurally.")
                cap("ordering.ordinal","ordering",translated,"Statement ORDER BY output positions are typed ordinals.")
                cap("ordering.nulls","ordering",nullOrdering,"NULL ordering follows provider-native or translated behavior.")
                cap("parameter.unbound","parameter",rejected,"Unbound SQL parameters are rejected.")
                cap("dml.basic","dml",translated,"INSERT VALUES, UPDATE, and DELETE use the structured DML path.")
                cap("dml.update_expression","dml",translated,"UPDATE SET accepts structured scalar expressions.")
                cap("dml.update.from","dml",if provider=SqlAgentToolType.Postgres then translated else rejected,"UPDATE FROM is currently PostgreSQL-only.")
                cap("dml.update.boolean_assignment","dml",booleanUpdate,"Boolean UPDATE assignment follows scalar-boolean capability.")
                cap("dml.delete.using","dml",if provider=SqlAgentToolType.Postgres then translated else rejected,"DELETE USING is currently PostgreSQL-only.")
                cap("dml.insert_select","dml",translated,"INSERT SELECT is supported for statically-known source width.")
                cap("dml.insert_select.cte_scope","dml",translated,"Statement-root CTE INSERT SELECT placement is provider-aware.")
                cap("dml.nested_cte_scope","dml",nestedStatus,
                    if nestedStatus=translated then "Nested DML CTEs use scope-preserving direct lowering, including output ordinal ordering." else "Nested DML CTE scope remains fail-closed.")
                cap("dml.advanced","dml",rejected,"Advanced DML remains fail-closed unless explicitly modeled.")
                cap("dml.returning_output","dml",returningStatus,
                    if returningStatus=translated then "Portable DML RETURNING result rows are enabled by the provider/runtime contract." else "DML RETURNING result rows remain fail-closed.")
                cap("dml.upsert_merge","dml",upsertStatus,
                    if upsertStatus=translated then "Deterministic explicit-target upsert is enabled by the provider/runtime contract." else "Portable upsert remains fail-closed.")
            ]

        ProviderSqlCapabilities(
            SqlCapabilityMatrix.Version,
            provider,
            (capabilities |> List.toArray :> IReadOnlyList<SqlCapability>))
