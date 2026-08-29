namespace HsSqlAgent.SqlCore.Models

open System
open System.Text.RegularExpressions
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums

/// F# ownership boundary for portable JSON extraction/mutation capability and path validation.
type internal SqlJsonCapabilityRules =

    static let portableJsonPropertyPath =
        Regex(@"^\$\.[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)

    static member SupportsExtract(provider: SqlAgentToolType) =
        provider = SqlAgentToolType.Postgres
        || provider = SqlAgentToolType.MySQL
        || provider = SqlAgentToolType.Sqlite

    static member SupportsSet(provider: SqlAgentToolType) =
        SqlJsonCapabilityRules.SupportsExtract(provider)
        || provider = SqlAgentToolType.MsSqlServer

    static member TargetValidationError(canonicalFunctionName: string, provider: SqlAgentToolType) : string | null =
        match canonicalFunctionName with
        | "CORE_JSON_EXTRACT" when SqlJsonCapabilityRules.SupportsExtract(provider) -> null
        | "CORE_JSON_EXTRACT" ->
            "SQL capability 'function.json_extract' is not supported by provider " + string provider + " for this Core plan."
        | "CORE_JSON_SET" when SqlJsonCapabilityRules.SupportsSet(provider) -> null
        | "CORE_JSON_SET" ->
            "SQL capability 'function.json_set' is not supported by provider " + string provider + " for this Core plan."
        | _ ->
            raise (ArgumentOutOfRangeException(
                "canonicalFunctionName",
                canonicalFunctionName,
                "Unsupported canonical JSON function."))

    static member private CapabilityError(provider: SqlAgentToolType, capability: string, detail: string) =
        detail.Trim() + " SQL capability '" + capability + "' is not supported by provider " + string provider + " for this Core plan."

    static member PathValidationError(functionCall: FunctionCallExpr, canonicalFunctionName: string, provider: SqlAgentToolType) : string | null =
        if functionCall.Arguments.Length < 2 then
            SqlJsonCapabilityRules.CapabilityError(
                provider,
                "json.path.constant",
                canonicalFunctionName + " requires a constant JSON path in the portable Core model.")
        else
            match functionCall.Arguments[1] with
            | :? LiteralExpr as literal ->
                match literal.Value with
                | :? string as path when portableJsonPropertyPath.IsMatch(path) -> null
                | :? string as path ->
                    SqlJsonCapabilityRules.CapabilityError(
                        provider,
                        "json.path.property_chain",
                        "JSON path '" + path + "' is outside the portable Core property-chain subset. Only paths such as '$.user.name' are supported; root-only paths, array indexes, wildcards, filters, quoted property names, and recursive descent fail closed.")
                | _ ->
                    SqlJsonCapabilityRules.CapabilityError(
                        provider,
                        "json.path.constant",
                        canonicalFunctionName + " requires a constant JSON path in the portable Core model.")
            | _ ->
                SqlJsonCapabilityRules.CapabilityError(
                    provider,
                    "json.path.constant",
                    canonicalFunctionName + " requires a constant JSON path in the portable Core model.")

    static member PropertyPathSegments(functionCall: FunctionCallExpr) =
        if functionCall.Arguments.Length < 2 then
            raise (SqlCompilationException("Canonical JSON lowering requires a validated constant property-chain path."))

        match functionCall.Arguments[1] with
        | :? LiteralExpr as literal ->
            match literal.Value with
            | :? string as path when portableJsonPropertyPath.IsMatch(path) ->
                path.Substring(2).Split('.', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            | _ -> raise (SqlCompilationException("Canonical JSON lowering requires a validated constant property-chain path."))
        | _ -> raise (SqlCompilationException("Canonical JSON lowering requires a validated constant property-chain path."))

    static member PathMatrixCapability() =
        SqlCapability(
            "json.path.simple",
            "json",
            SqlCapabilityStatus.Translated,
            "Portable JSON paths are limited to constant property chains beginning at $, for example $.user.name; root-only, array-index, wildcard, filter, quoted property names, recursive descent, and dynamic paths are rejected before lowering.")

    static member ExtractMatrixCapability(provider: SqlAgentToolType) =
        let detail =
            match provider with
            | SqlAgentToolType.MsSqlServer
            | SqlAgentToolType.Oracle ->
                "Ambiguous JSON_EXTRACT is rejected because the scalar/object result type is unknown; use an explicit JSON_VALUE or JSON_QUERY contract."
            | SqlAgentToolType.Firebird ->
                "Portable JSON extraction has no declared Firebird equivalent."
            | _ ->
                "Constant JSON property-chain paths such as $.user.name are normalized and translated; root-only, array-index, wildcard, filter, quoted-property, recursive-descent, and dynamic paths fail closed."
        SqlCapability(
            "json.extract",
            "json",
            (if SqlJsonCapabilityRules.SupportsExtract(provider) then SqlCapabilityStatus.Translated else SqlCapabilityStatus.Rejected),
            detail)

    static member SetMatrixCapability(provider: SqlAgentToolType) =
        let supported = SqlJsonCapabilityRules.SupportsSet(provider)
        SqlCapability(
            "json.set",
            "json",
            (if supported then SqlCapabilityStatus.Translated else SqlCapabilityStatus.Rejected),
            (if supported then
                "Portable JSON mutation is rendered with provider-native functions after constant property-chain path validation."
             else
                "Portable JSON mutation has no declared equivalent for this provider."))