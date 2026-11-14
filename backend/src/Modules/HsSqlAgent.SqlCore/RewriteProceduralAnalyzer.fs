namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// Opaque, non-executable procedure invocation surface. Recognized syntax is
/// not evidence that a routine exists, is callable, or has known body effects.
/// This deliberately refuses argument expressions, dynamic SQL, routine
/// definitions/bodies and ambiguous name resolution.
module internal RewriteProceduralAnalyzer =

    let private potentialEffects =
        SqlEffectKind.ReadData ||| SqlEffectKind.WriteData
        ||| SqlEffectKind.ChangeSchema ||| SqlEffectKind.ChangePermissions
        ||| SqlEffectKind.ChangeTransaction ||| SqlEffectKind.ChangeSession
        ||| SqlEffectKind.Administrative ||| SqlEffectKind.UnknownEffects

    let private word token =
        match token.Kind with
        | Keyword value | Identifier(value, false) -> value.ToUpperInvariant()
        | _ -> ""

    let private tokenName sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                          (token: Token) =
        match token.Kind with
        | Identifier(name, false) when
            RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier name
            && name.Length <= 63 ->
            Some (RewriteCatalogIdentifierProof.canonicalName
                      sourceDialect sourceProfile name false)
        | _ -> None

    let private isSymbol c token =
        match token.Kind with
        | Symbol actual -> actual = c
        | _ -> false

    let private parsedInvocation sourceDialect sourceProfile (tokens: Token[]) =
        let isWord i value = i < tokens.Length && word tokens[i] = value
        let is i c = i < tokens.Length && isSymbol c tokens[i]
        let name i = if i < tokens.Length then tokenName sourceDialect sourceProfile tokens[i] else None
        let qualify first second =
            match first, second with
            | Some schema, Some routine ->
                Some (SqlCatalogQualifiedName(
                    Array.AsReadOnly([|schema; routine|])))
            | _ -> None
        let bare value =
            match value with
            | Some routine ->
                Some (SqlCatalogQualifiedName(Array.AsReadOnly([|routine|])))
            | _ -> None

        match sourceDialect with
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.MySQL
        | SqlAgentToolType.Oracle when isWord 0 "CALL" ->
            let target = qualify (name 1) (name 3)
            if not (is 2 '.') then None
            elif tokens.Length = 6 && is 4 '(' && is 5 ')' then
                target |> Option.map (fun routine -> SqlProceduralInvocationKind.Call, routine)
            elif sourceDialect = SqlAgentToolType.MySQL && tokens.Length = 4 then
                target |> Option.map (fun routine -> SqlProceduralInvocationKind.Call, routine)
            else None

        | SqlAgentToolType.MsSqlServer when
            isWord 0 "EXEC" || isWord 0 "EXECUTE" ->
            if tokens.Length = 4 && is 2 '.' then
                qualify (name 1) (name 3)
                |> Option.map (fun routine -> SqlProceduralInvocationKind.Exec, routine)
            else None

        | SqlAgentToolType.Firebird when
            isWord 0 "EXECUTE" && isWord 1 "PROCEDURE" ->
            if tokens.Length = 3 then
                bare (name 2)
                |> Option.map (fun routine -> SqlProceduralInvocationKind.ExecuteProcedure, routine)
            else None
        | _ -> None

    let private diagnostic (syntax: SqlScriptStatementSyntax) =
        SqlDiagnostic(
            "SQL_PROCEDURAL_UNSUPPORTED",
            SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Capability,
            "Only narrowly modeled zero-argument static procedure calls are recognized. "
            + "Dynamic EXEC, arguments, anonymous blocks and procedure/function/trigger bodies "
            + "are opaque: neither syntax nor server-side effects are proven.",
            syntax.Span)

    let analyze sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                (catalog: SqlCatalogState) (syntax: SqlScriptStatementSyntax) =
        let tokens =
            RewriteResourceBudget.withStage "ProceduralLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeScriptValidated syntax.Text sourceDialect sourceProfile)
            |> List.filter (fun token -> token.Kind <> End)
            |> List.toArray
        let next = RewriteCatalogModel.taintAll catalog
        if sourceDialect = SqlAgentToolType.Postgres
           && tokens.Length > 0 && word tokens[0] = "CREATE" then
            let status, definitions, diagnostics =
                RewriteProceduralBodyAnalyzer.analyze sourceProfile syntax tokens
            status, [], definitions, next, diagnostics
        else
            match parsedInvocation sourceDialect sourceProfile tokens with
            | Some (kind, routine) ->
                let intent =
                    SqlProceduralIntent(kind, routine, potentialEffects, syntax.Span)
                // Invocation is NEVER a body proof, even if a definition
                // was analyzed earlier in this same script.
                SqlStatementAnalysisStatus.PartiallyAnalyzed, [intent], [], next, []
            | None ->
                SqlStatementAnalysisStatus.Unsupported, [], [], next,
                [diagnostic syntax]
