namespace HsSqlAgent.SqlCore.Models

open System
open HsSqlAgent.SqlCore.Enums

/// F# ownership boundary for portable regex target/provider capability decisions.
type internal SqlRegexCapabilityRules =

    static member SqlServerMinimumVersion = Version(17, 0)
    static member SqlServerMinimumCompatibilityLevel = 170

    static member RequiresTargetProfileRewrite(provider: SqlAgentToolType) =
        provider = SqlAgentToolType.MsSqlServer

    static member ProviderValidationError(provider: SqlAgentToolType) : string | null =
        match provider with
        | SqlAgentToolType.Sqlite
        | SqlAgentToolType.Firebird ->
            "SQL capability 'function.regex_match' is not supported by provider " +
            string provider + " for this Core plan."
        | _ -> null

    static member SupportsTarget(provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null) =
        match provider with
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.MySQL
        | SqlAgentToolType.Oracle -> true
        | SqlAgentToolType.MsSqlServer ->
            match Option.ofObj targetProfile with
            | None -> false
            | Some profile when profile.Provider <> SqlAgentToolType.MsSqlServer -> false
            | Some profile ->
                match Option.ofObj profile.ServerVersion with
                | None -> false
                | Some version when not profile.CompatibilityLevel.HasValue -> false
                | Some version ->
                    profile.CompatibilityLevel.Value >= SqlRegexCapabilityRules.SqlServerMinimumCompatibilityLevel
                    && version.CompareTo(SqlRegexCapabilityRules.SqlServerMinimumVersion) >= 0
        | SqlAgentToolType.Sqlite
        | SqlAgentToolType.Firebird -> false
        | _ -> raise (ArgumentOutOfRangeException("provider", provider, "Unsupported SQL provider."))

    static member TargetValidationError(provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null) : string | null =
        if SqlRegexCapabilityRules.SupportsTarget(provider, targetProfile) then null
        elif provider = SqlAgentToolType.MsSqlServer then
            "SQL capability 'function.regex_match' requires a declared SQL Server target capability profile with ServerVersion 17.0 or newer and compatibility level 170 or above."
        else
            "SQL capability 'function.regex_match' is not supported by provider " +
            string provider + " for this Core plan."

    static member MatrixCapability(provider: SqlAgentToolType, targetProfile: SqlProviderCapabilityProfile | null) =
        let supported = SqlRegexCapabilityRules.SupportsTarget(provider, targetProfile)
        let detail =
            match provider with
            | SqlAgentToolType.Postgres
            | SqlAgentToolType.MySQL
            | SqlAgentToolType.Oracle ->
                "REGEXP_LIKE semantics are rendered using the provider's declared regex syntax."
            | SqlAgentToolType.MsSqlServer when supported ->
                "SQL Server REGEXP_LIKE is enabled by the declared SQL Server 17.x+ target profile at compatibility level 170 or above and is emitted natively."
            | SqlAgentToolType.MsSqlServer ->
                "SQL Server REGEXP_LIKE requires a declared target capability profile with ServerVersion 17.0+ and compatibility level 170 or above; absent, older, or lower-compatibility profiles remain fail-closed."
            | _ ->
                "Regex matching is rejected because no reliable native equivalent is declared."
        SqlCapability(
            "regex.match",
            "regex",
            (if supported then SqlCapabilityStatus.Translated else SqlCapabilityStatus.Rejected),
            detail)
