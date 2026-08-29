namespace HsSqlAgent.SqlCore.Models

open System
open HsSqlAgent.SqlCore.Enums

/// F# ownership boundary for target-provider support of canonical date formatting and formatted parsing.
type internal SqlTemporalFormatCapabilityRules =

    static member SupportsDateFormat(provider: SqlAgentToolType) =
        provider <> SqlAgentToolType.Firebird

    static member SupportsFormattedParse(provider: SqlAgentToolType) =
        provider = SqlAgentToolType.Postgres
        || provider = SqlAgentToolType.MySQL
        || provider = SqlAgentToolType.Oracle

    static member TargetValidationError(canonicalFunctionName: string, provider: SqlAgentToolType) : string | null =
        match canonicalFunctionName with
        | "CORE_DATE_FORMAT" when SqlTemporalFormatCapabilityRules.SupportsDateFormat(provider) -> null
        | "CORE_DATE_FORMAT" ->
            "portable date formatting is not supported by Firebird. " +
            "SQL capability 'function.date_format' is not supported by provider " +
            string provider + " for this Core plan."
        | "CORE_DATE_PARSE" when SqlTemporalFormatCapabilityRules.SupportsFormattedParse(provider) -> null
        | "CORE_DATE_PARSE" ->
            "formatted date parsing is not supported by this provider. " +
            "SQL capability 'function.date_parse' is not supported by provider " +
            string provider + " for this Core plan."
        | _ ->
            raise (ArgumentOutOfRangeException(
                "canonicalFunctionName",
                canonicalFunctionName,
                "Unsupported canonical temporal format function."))

    static member DateFormatMatrixCapability(provider: SqlAgentToolType) =
        let supported = SqlTemporalFormatCapabilityRules.SupportsDateFormat(provider)
        SqlCapability(
            "temporal.date_format",
            "temporal",
            (if supported then SqlCapabilityStatus.Translated else SqlCapabilityStatus.Rejected),
            (if supported then
                "Declared source date-format functions and tokens are normalized and translated to provider-native syntax."
             else
                "Portable date formatting is rejected because no complete translation is declared."))

    static member FormattedParseMatrixCapability(provider: SqlAgentToolType) =
        let supported = SqlTemporalFormatCapabilityRules.SupportsFormattedParse(provider)
        SqlCapability(
            "temporal.formatted_parse",
            "temporal",
            (if supported then SqlCapabilityStatus.Translated else SqlCapabilityStatus.Rejected),
            (if supported then
                "TO_DATE input and format tokens are translated to the provider-native function."
             else
                "Formatted date parsing is rejected because no complete provider translation is declared."))
