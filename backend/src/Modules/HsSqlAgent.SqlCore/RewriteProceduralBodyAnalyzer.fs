namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.SqlParsing
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// Syntax-only bounded PostgreSQL LANGUAGE SQL procedure body inspection.
/// A parsed body is NOT proof of validity against server metadata, successful
/// routine creation, successful CALL, effective rights, or runtime effects.
module internal RewriteProceduralBodyAnalyzer =

    [<Literal>]
    let private MaxStatements = 16

    [<Literal>]
    let private MaxCharacters = 16384

    let private readOnly (xs: seq<'T>) : IReadOnlyList<'T> =
        (xs |> Seq.toArray |> Array.AsReadOnly) :> IReadOnlyList<'T>

    let private word token =
        match token.Kind with
        | Keyword value
        | RewriteLexer.Identifier(value, false) -> value.ToUpperInvariant()
        | _ -> ""

    let private isSymbol ch token =
        match token.Kind with
        | Symbol actual -> actual = ch
        | _ -> false

    let private name (profile: SqlProviderCapabilityProfile | null) token =
        match token.Kind with
        | RewriteLexer.Identifier(value, false) when
            RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier value
            && value.Length <= 63 ->
            Some (RewriteCatalogIdentifierProof.canonicalName
                      SqlAgentToolType.Postgres profile value false)
        | _ -> None

    /// The only accepted header is CREATE [OR REPLACE] PROCEDURE
    /// schema.name() LANGUAGE SQL AS $tag$...$tag$.
    /// The body source span must be exactly recoverable from the original
    /// dollar-tag string token, not a decoded/escaped approximation.
    let private header profile (syntax: SqlScriptStatementSyntax) (tokens: Token[]) =
        let at i value = i < tokens.Length && word tokens[i] = value
        let symbol i c = i < tokens.Length && isSymbol c tokens[i]
        let offset =
            if at 0 "CREATE" && at 1 "OR" && at 2 "REPLACE" then Some 3
            elif at 0 "CREATE" then Some 1
            else None
        match offset with
        | Some i when at i "PROCEDURE" ->
            if tokens.Length <> i+10
                || not (symbol (i+2) '.')
                || not (symbol (i+4) '(')
                || not (symbol (i+5) ')')
                || not (at (i+6) "LANGUAGE")
                || not (at (i+7) "SQL")
                || not (at (i+8) "AS") then None
            else
                match name profile tokens[i+1], name profile tokens[i+3], tokens[i+9].Kind with
                | Some schema, Some routineName, StringLiteral body ->
                    let source = syntax.Text
                    let tok = tokens[i+9]
                    if tok.Start >= source.Length || source[tok.Start] <> '$' then None
                    else
                        let lastTagChar = source.IndexOf('$', tok.Start+1)
                        if lastTagChar < 0 then None
                        else
                            let delimiter = source.Substring(tok.Start, lastTagChar-tok.Start+1)
                            let bodyOffset = tok.Start + delimiter.Length
                            let rawLength = tok.Length - (2*delimiter.Length)
                            if rawLength < 0 || rawLength <> body.Length
                                || bodyOffset + rawLength + delimiter.Length > source.Length
                                || not (String.Equals(
                                    source.Substring(bodyOffset, rawLength), body,
                                    StringComparison.Ordinal))
                                || not (String.Equals(
                                    source.Substring(bodyOffset+rawLength, delimiter.Length),
                                    delimiter, StringComparison.Ordinal)) then None
                            else
                                let qualified =
                                    SqlCatalogQualifiedName(Array.AsReadOnly([|schema; routineName|]))
                                Some (qualified, body, syntax.Span.Start + bodyOffset)
                | _ -> None
        | _ -> None

    let private bodyStatements profile (body: string) absoluteBodyStart =
        if body.Length > MaxCharacters then None
        else
            try
                let parsed = RewriteScriptParser.parse body SqlAgentToolType.Postgres profile
                if parsed.Statements.Count = 0
                   || parsed.Statements.Count > MaxStatements
                   || (parsed.Statements |> Seq.exists (fun st ->
                           st.Status <> SqlScriptParseStatus.Parsed)) then None
                else
                    let statements = ResizeArray<SqlProceduralBodyStatement>()
                    let mutable supported = true
                    for item in parsed.Statements do
                        RewriteResourceBudget.checkpoint ()
                        let ast =
                            RewriteFacadeAdapter.parseSourceValidated
                                item.Text SqlAgentToolType.Postgres profile
                            |> Typestate.Parsed.value
                        let statementKind =
                            match ast.Statement with
                            | Statement.QueryStatement _ ->
                                Some (SqlScriptStatementKind.Query, SqlStatementEffectOperation.ReadTable)
                            | Statement.InsertStatement _ ->
                                Some (SqlScriptStatementKind.Mutation, SqlStatementEffectOperation.Insert)
                            | Statement.UpdateStatement _ ->
                                Some (SqlScriptStatementKind.Mutation, SqlStatementEffectOperation.Update)
                            | Statement.DeleteStatement _ ->
                                Some (SqlScriptStatementKind.Mutation, SqlStatementEffectOperation.Delete)
                            | _ -> None
                        match statementKind with
                        | Some (kind, op) when kind = item.Kind ->
                            let refs = RewriteInspection.inspectDocument ast
                            let tables =
                                refs.ReferencedTables
                                |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b))
                                |> readOnly
                            let potential =
                                (if kind = SqlScriptStatementKind.Query then SqlEffectKind.ReadData
                                 else SqlEffectKind.ReadData ||| SqlEffectKind.WriteData)
                                ||| SqlEffectKind.UnknownEffects
                            let sourceSpan =
                                SqlDiagnosticSpan(absoluteBodyStart + item.Span.Start, item.Span.Length)
                            statements.Add(
                                SqlProceduralBodyStatement(kind, op, potential, tables, sourceSpan))
                        | _ -> supported <- false
                    if supported then Some (readOnly statements) else None
            with
            | :? SqlParseException as ex ->
                if not (isNull ex.Diagnostic)
                   && ex.Diagnostic.Category = SqlDiagnosticCategory.Resource then
                    reraise()
                None

    let private error (syntax: SqlScriptStatementSyntax) code message =
        SqlDiagnostic(
            code, SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Capability, message, syntax.Span)

    /// No partially built body is ever exposed on an unsupported definition.
    let analyze profile (syntax: SqlScriptStatementSyntax) (tokens: Token[]) =
        match header profile syntax tokens with
        | None ->
            SqlStatementAnalysisStatus.Unsupported, [],
            [error syntax "SQL_PROCEDURAL_UNSUPPORTED"
                "Only qualified zero-argument PostgreSQL LANGUAGE SQL AS dollar-quoted PROCEDURE definitions are modeled."]
        | Some (routine, body, bodyStart) ->
            match bodyStatements profile body bodyStart with
            | None ->
                SqlStatementAnalysisStatus.Unsupported, [],
                [error syntax "SQL_PROCEDURAL_BODY_UNSUPPORTED"
                    "Procedure body exceeds the statement budget or contains unmodeled, invalid, or opaque SQL."]
            | Some statements ->
                let effects =
                    statements |> Seq.fold (fun all item -> all ||| item.PotentialEffects)
                        SqlEffectKind.UnknownEffects
                let definition =
                    SqlProceduralDefinition(
                        SqlProceduralDefinitionKind.Procedure, routine, statements, effects,
                        syntax.Span)
                SqlStatementAnalysisStatus.PartiallyAnalyzed, [definition], []
