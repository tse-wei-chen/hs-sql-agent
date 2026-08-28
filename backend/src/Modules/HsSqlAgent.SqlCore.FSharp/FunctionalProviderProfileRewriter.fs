namespace HsSqlAgent.SqlCore.Internal

open System
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// Target-runtime capability rewrites implemented in F#.
///
/// The structural recursion is delegated to FunctionalAstRewriter; this module
/// owns only provider/profile-sensitive expression-node decisions.
module internal FunctionalProviderProfileRewriter =

    let validateProfile
        targetProvider
        (targetProfile: SqlProviderCapabilityProfile | null) =

        match SqlProviderCapabilityProfileRules.ValidationIssue(
            targetProfile,
            targetProvider) with

        | SqlProviderCapabilityProfileValidationIssue.None ->
            ()

        | SqlProviderCapabilityProfileValidationIssue.ProviderMismatch ->
            match Option.ofObj targetProfile with
            | Some profile ->
                raise (SqlCompilationException(
                    $"Target capability profile declares provider {profile.Provider}, but compilation targets {targetProvider}."))
            | None ->
                raise (SqlCompilationException(
                    "Unsupported target capability profile validation issue."))

        | SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel ->
            raise (SqlCompilationException(
                "Provider compatibility level must be non-negative."))

        | _ ->
            raise (SqlCompilationException(
                "Unsupported target capability profile validation issue."))

    let private requiresProviderProfilePass provider =
        SqlFirebirdTimeZoneTypeCapabilityRules.RequiresTargetProfileValidation(provider)
        || SqlFirebirdDecimalCapabilityRules.RequiresTargetProfileValidation(provider)
        || SqlConcatCapabilityRules.RequiresTargetProfileRewrite(provider)
        || SqlRegexCapabilityRules.RequiresTargetProfileRewrite(provider)

    let private identifierText (identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    let private rewriteLiteral
        targetProvider
        targetProfile
        (literal: LiteralExpr) =

        match literal.Value with
        | :? SqlOffsetDateTimeValue
        | :? DateTimeOffset ->
            match Option.ofObj (
                SqlOffsetTimestampCapabilityRules.TargetValidationError(
                    targetProvider,
                    targetProfile)) with
            | Some message ->
                raise (SqlCompilationException(message))
            | None ->
                literal :> SqlExpr

        | :? decimal as decimalValue ->
            match Option.ofObj (
                SqlFirebirdDecimalCapabilityRules.TargetValidationError(
                    targetProvider,
                    targetProfile,
                    decimalValue)) with
            | Some message ->
                raise (SqlCompilationException(message))
            | None ->
                literal :> SqlExpr

        | _ ->
            literal :> SqlExpr

    let private rewriteCast
        targetProvider
        targetProfile
        (cast: CastExpr) =

        match Option.ofObj (
            SqlFirebirdTimeZoneTypeCapabilityRules.CastTargetValidationError(
                targetProvider,
                targetProfile,
                cast.TypeName)) with
        | Some message ->
            raise (SqlCompilationException(message))
        | None ->
            cast :> SqlExpr

    let private rewriteBinary
        targetProvider
        targetProfile
        (binary: BinaryExpr) =

        if not (
            binary.Operator.Equals(
                "||",
                StringComparison.OrdinalIgnoreCase))
           || not (
               SqlConcatCapabilityRules.RequiresTargetProfileRewrite(
                   targetProvider)) then
            binary :> SqlExpr
        else
            match SqlConcatCapabilityRules.EvaluateSqlServerTarget(
                targetProfile) with

            | SqlServerConcatTargetMode.NativePipes ->
                binary :> SqlExpr

            | SqlServerConcatTargetMode.PlusOperator ->
                CoreBindingAstClone.BinaryOperator(
                    binary,
                    "+")
                :> SqlExpr

            | SqlServerConcatTargetMode.Rejected ->
                raise (SqlCompilationException(
                    SqlConcatCapabilityRules.SqlServerTargetValidationError(
                        targetProfile)))

            | _ ->
                raise (SqlCompilationException(
                    "Unsupported SQL Server concat target mode."))

    let private rewriteFunction
        targetProvider
        targetProfile
        (functionCall: FunctionCallExpr) =

        let name =
            identifierText functionCall.Name

        let rewriteKind =
            match Option.ofObj (
                SqlCanonicalFunctionRegistry.Find(name)) with
            | Some contract ->
                contract.ProviderProfileRewriteKind
            | None ->
                SqlCanonicalProviderProfileRewriteKind.None

        match rewriteKind with
        | SqlCanonicalProviderProfileRewriteKind.None ->
            functionCall :> SqlExpr

        | SqlCanonicalProviderProfileRewriteKind.Regex ->
            match Option.ofObj (
                SqlRegexCapabilityRules.TargetValidationError(
                    targetProvider,
                    targetProfile)) with
            | Some message ->
                raise (SqlCompilationException(message))

            | None ->
                CoreBindingAstClone.FunctionName(
                    functionCall,
                    SqlIdentifier.Unquoted(
                        "REGEXP_LIKE",
                        functionCall.Name.Span))
                :> SqlExpr

        | other ->
            raise (SqlCompilationException(
                $"Unsupported canonical provider-profile rewrite kind '{other}' for function '{name}'."))

    let private rewriteExpressionNode
        targetProvider
        targetProfile
        (expression: SqlExpr) =

        match expression with
        | :? LiteralExpr as literal ->
            rewriteLiteral
                targetProvider
                targetProfile
                literal

        | :? CastExpr as cast ->
            rewriteCast
                targetProvider
                targetProfile
                cast

        | :? BinaryExpr as binary ->
            rewriteBinary
                targetProvider
                targetProfile
                binary

        | :? FunctionCallExpr as functionCall ->
            rewriteFunction
                targetProvider
                targetProfile
                functionCall

        | _ ->
            expression

    let rewrite
        (statement: SqlStatement)
        targetProvider
        (targetProfile: SqlProviderCapabilityProfile | null) =

        validateProfile
            targetProvider
            targetProfile

        if not (
            requiresProviderProfilePass(
                targetProvider)) then
            statement
        else
            FunctionalAstRewriter.rewrite
                "provider-profile"
                (rewriteExpressionNode
                    targetProvider
                    targetProfile)
                statement
