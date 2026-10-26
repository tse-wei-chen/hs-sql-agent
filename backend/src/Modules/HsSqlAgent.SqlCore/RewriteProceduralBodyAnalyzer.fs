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

    type private ParsedHeader =
        | ProcedureHeader of SqlCatalogQualifiedName * string * int
        | FunctionHeader of SqlCatalogQualifiedName * string * int * SqlFunctionReturnType * SqlFunctionParameterSymbol list

    let private returnType token =
        match word token with
        | "INTEGER" | "INT" | "INT4" -> Some SqlFunctionReturnType.Integer
        | "BIGINT" | "INT8" -> Some SqlFunctionReturnType.BigInteger
        | "BOOLEAN" | "BOOL" -> Some SqlFunctionReturnType.Boolean
        | "TEXT" -> Some SqlFunctionReturnType.Text
        | "VOID" -> Some SqlFunctionReturnType.Void
        | _ -> None

    [<Literal>]
    let private MaxParameters = 8

    /// IN-only, named primitive parameters. Parser tracks *tokens*, never
    /// rewrites user SQL or promotes a parameter to an identifier/value.
    let private parameters profile (syntax: SqlScriptStatementSyntax) (tokens: Token[]) first =
        let mutable cursor = first
        let found = ResizeArray<SqlFunctionParameterSymbol>()
        let mutable valid = true
        let mutable doneReading = false
        while valid && not doneReading && cursor < tokens.Length do
            RewriteResourceBudget.checkpoint ()
            if isSymbol ')' tokens[cursor] then
                doneReading <- true
            elif found.Count >= MaxParameters || cursor + 1 >= tokens.Length then
                valid <- false
            else
                let named =
                    match tokens[cursor].Kind with
                    | RewriteLexer.Identifier(value,false) ->
                        name profile tokens[cursor]
                    | _ -> None
                match named, returnType tokens[cursor+1] with
                | Some paramName, Some paramType when paramType <> SqlFunctionReturnType.Void
                                            && not (found |> Seq.exists (fun item -> item.Name = paramName)) ->
                    let sp = SqlDiagnosticSpan(
                        syntax.Span.Start + tokens[cursor].Start,
                        tokens[cursor].Length + tokens[cursor+1].Start
                            + tokens[cursor+1].Length - tokens[cursor].Start)
                    found.Add(SqlFunctionParameterSymbol(found.Count+1,paramName,paramType,sp))
                    cursor <- cursor + 2
                    if cursor < tokens.Length && isSymbol ',' tokens[cursor] then
                        cursor <- cursor+1
                        if cursor >= tokens.Length || isSymbol ')' tokens[cursor] then
                            valid <- false
                    elif cursor < tokens.Length && isSymbol ')' tokens[cursor] then
                        doneReading <- true
                    else valid <- false
                | _ -> valid <- false
        if valid && doneReading then Some (cursor,List.ofSeq found)
        else None

    let private bodyLiteral (syntax: SqlScriptStatementSyntax) (token: Token) =
        match token.Kind with
        | StringLiteral body ->
            let source = syntax.Text
            if token.Start >= source.Length || source[token.Start] <> '$' then None
            else
                let closeTag = source.IndexOf('$', token.Start+1)
                if closeTag < 0 then None
                else
                    let delimiter = source.Substring(token.Start, closeTag-token.Start+1)
                    let beginIndex = token.Start + delimiter.Length
                    let bodyLength = token.Length - 2*delimiter.Length
                    if bodyLength < 0 || bodyLength <> body.Length
                        || beginIndex + bodyLength + delimiter.Length > source.Length
                        || not (String.Equals(source.Substring(beginIndex,bodyLength),body,StringComparison.Ordinal))
                        || not (String.Equals(source.Substring(beginIndex+bodyLength,delimiter.Length),delimiter,StringComparison.Ordinal)) then None
                    else Some (body, syntax.Span.Start + beginIndex)
        | _ -> None

    /// Admits either PostgreSQL header ordering, with no volatility,
    /// SECURITY DEFINER, SET, output parameters or implicit result casts.
    let private header profile (syntax: SqlScriptStatementSyntax) (tokens: Token[]) =
        let at i value = i < tokens.Length && word tokens[i] = value
        let symbol i c = i < tokens.Length && isSymbol c tokens[i]
        let offset =
            if at 0 "CREATE" && at 1 "OR" && at 2 "REPLACE" then Some 3
            elif at 0 "CREATE" then Some 1
            else None
        match offset with
        | Some i when tokens.Length >= i+10
                      && symbol (i+2) '.'
                      && symbol (i+4) '(' ->
            match name profile tokens[i+1], name profile tokens[i+3] with
            | Some schema, Some routineName ->
                let routine =
                    SqlCatalogQualifiedName(Array.AsReadOnly([|schema;routineName|]))
                if at i "PROCEDURE" && tokens.Length = i+10
                    && symbol (i+5) ')'
                    && at (i+6) "LANGUAGE" && at (i+7) "SQL"
                    && at (i+8) "AS" then
                    bodyLiteral syntax tokens[i+9]
                    |> Option.map (fun (body,offset) -> ProcedureHeader(routine,body,offset))
                elif at i "FUNCTION" then
                    match parameters profile syntax tokens (i+5) with
                    | None -> None
                    | Some (closeIndex, symbols) ->
                        let r = closeIndex+1
                        if tokens.Length <> r+6 || not (at r "RETURNS") then None
                        else
                            match returnType tokens[r+1] with
                            | None -> None
                            | Some declared ->
                                let tokenIndex =
                                    if at (r+2) "LANGUAGE" && at (r+3) "SQL"
                                       && at (r+4) "AS" then Some (r+5)
                                    elif at (r+2) "AS" && at (r+4) "LANGUAGE"
                                         && at (r+5) "SQL" then Some (r+3)
                                    else None
                                tokenIndex |> Option.bind (fun index ->
                                    bodyLiteral syntax tokens[index]
                                    |> Option.map (fun (body,offset) ->
                                        FunctionHeader(routine,body,offset,declared,symbols)))
                else None
            | _ -> None
        | _ -> None

    /// No server type inference, implicit casts, column lookups, subqueries
    /// or expression evaluation. Only a single direct closed AST literal
    /// or an explicit primitive CAST of a compatible literal is accepted.
    let private literalCompatible declared (value: ScalarValue) =
        match declared, value with
        | SqlFunctionReturnType.Integer, ScalarValue.Integer n ->
            n >= int64 Int32.MinValue && n <= int64 Int32.MaxValue
        | SqlFunctionReturnType.BigInteger, ScalarValue.Integer _ -> true
        | SqlFunctionReturnType.Boolean, ScalarValue.Boolean _ -> true
        | SqlFunctionReturnType.Text, ScalarValue.Text _ -> true
        | _ -> false

    /// Unqualified, unquoted names are admitted only in the FROM-free,
    /// single-expression SELECT shape checked below. No Catalog lookup is
    /// silently replaced by an in-memory function parameter.
    let private parameterType (symbols: SqlFunctionParameterSymbol list) identifier =
        match Identifier.parts identifier with
        | [part] when not part.WasQuoted ->
            symbols
            |> List.tryPick (fun symbol ->
                if symbol.Name = part.Value then Some symbol.DeclaredType else None)
        | _ -> None

    /// Bounded, PostgreSQL-specific primitive result-type evidence.
    /// Only closed AST nodes with known built-in integer/boolean operator
    /// result types are traversed; functions, division, comparisons, NULLs,
    /// arbitrary CASTs and identifiers lacking typed local symbols fail closed.
    /// This is a nominal type shape, not proof of execution or that a runtime
    /// operation is error-free.
    [<Literal>]
    let private MaxReturnExpressionNodes = 64

    let private primitiveExpressionType (symbols: SqlFunctionParameterSymbol list) expression =
        let mutable remaining = MaxReturnExpressionNodes
        let rec infer expression =
            RewriteResourceBudget.checkpoint ()
            remaining <- remaining - 1
            if remaining < 0 then None
            else
                match Expr.unspan expression with
                | Expr.Literal (ScalarValue.Integer number) ->
                    if number >= int64 Int32.MinValue
                       && number <= int64 Int32.MaxValue then
                        Some SqlFunctionReturnType.Integer
                    else Some SqlFunctionReturnType.BigInteger
                | Expr.Literal (ScalarValue.Boolean _) ->
                    Some SqlFunctionReturnType.Boolean
                | Expr.Literal (ScalarValue.Text _) ->
                    Some SqlFunctionReturnType.Text
                | Expr.Column identifier -> parameterType symbols identifier
                | Expr.Unary (UnaryOperator.Not, value) ->
                    match infer value with
                    | Some SqlFunctionReturnType.Boolean ->
                        Some SqlFunctionReturnType.Boolean
                    | _ -> None
                | Expr.Unary ((UnaryOperator.Positive | UnaryOperator.Negate), value) ->
                    match infer value with
                    | Some SqlFunctionReturnType.Integer -> Some SqlFunctionReturnType.Integer
                    | Some SqlFunctionReturnType.BigInteger -> Some SqlFunctionReturnType.BigInteger
                    | _ -> None
                | Expr.Binary ((BinaryOperator.And | BinaryOperator.Or), left, right) ->
                    match infer left, infer right with
                    | Some SqlFunctionReturnType.Boolean, Some SqlFunctionReturnType.Boolean ->
                        Some SqlFunctionReturnType.Boolean
                    | _ -> None
                | Expr.Binary (op, left, right) when
                    op = BinaryOperator.Add || op = BinaryOperator.Subtract
                    || op = BinaryOperator.Multiply ->
                    match infer left, infer right with
                    | Some SqlFunctionReturnType.Integer, Some SqlFunctionReturnType.Integer ->
                        Some SqlFunctionReturnType.Integer
                    | Some SqlFunctionReturnType.BigInteger, Some SqlFunctionReturnType.Integer
                    | Some SqlFunctionReturnType.Integer, Some SqlFunctionReturnType.BigInteger
                    | Some SqlFunctionReturnType.BigInteger, Some SqlFunctionReturnType.BigInteger ->
                        Some SqlFunctionReturnType.BigInteger
                    | _ -> None
                | _ -> None
        infer expression

    /// No arbitrary SQL CAST or implicit cast proof: a typed named symbol may
    /// only preserve its declared primitive type, or widen int4 to int8.
    let private castCompatible declared symbols (target: CastType) (value: Expr) =
        let castMatches =
            match declared, CastType.semantic target with
            | SqlFunctionReturnType.Integer, Some SqlType.SqlInteger
            | SqlFunctionReturnType.BigInteger, Some SqlType.SqlBigInteger
            | SqlFunctionReturnType.Boolean, Some SqlType.SqlBoolean
            | SqlFunctionReturnType.Text, Some SqlType.SqlText -> true
            | _ -> false
        castMatches
        && (match Expr.unspan value with
            | Expr.Literal literal -> literalCompatible declared literal
            | Expr.Column identifier ->
                match parameterType symbols identifier with
                | Some source ->
                    source = declared
                    || (source = SqlFunctionReturnType.Integer
                        && declared = SqlFunctionReturnType.BigInteger)
                | None -> false
            | _ -> false)

    let private returnEvidence declared (symbols: SqlFunctionParameterSymbol list) (last: Document) =
        if declared = SqlFunctionReturnType.Void then
            Some SqlFunctionReturnEvidence.VoidNoValueRequired
        else
            match last.Statement with
            | Statement.QueryStatement query when
                query.Head.Ctes.IsEmpty
                && query.Head.From.IsNone
                && query.Head.Joins.IsEmpty
                && query.Head.Where.IsNone
                && query.Head.GroupBy.IsEmpty
                && query.Head.Having.IsNone
                && query.SetOperations.IsEmpty
                && query.OrderBy.IsEmpty
                && query.Limit.IsNone && query.Offset.IsNone
                && query.FetchPercent.IsNone && not query.FetchWithTies
                && query.Head.DistinctMode = SelectDistinct.AllRows
                && query.Head.Projection.Length = 1 ->
                match Expr.unspan query.Head.Projection.Head.Expression with
                | Expr.Literal literal when
                    declared <> SqlFunctionReturnType.BigInteger
                    && literalCompatible declared literal ->
                    Some SqlFunctionReturnEvidence.LiteralMatch
                | Expr.Cast(value,target) when castCompatible declared symbols target value ->
                    Some SqlFunctionReturnEvidence.ExplicitCastMatch
                | Expr.Column identifier ->
                    if parameterType symbols identifier = Some declared then
                        Some SqlFunctionReturnEvidence.ParameterSymbolMatch
                    else None
                | Expr.Unary _ | Expr.Binary _ ->
                    if primitiveExpressionType symbols query.Head.Projection.Head.Expression
                       = Some declared then
                        Some SqlFunctionReturnEvidence.PrimitiveExpressionMatch
                    else None
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
                    let mutable lastDocument : Document option = None
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
                            lastDocument <- Some ast
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
                    if supported then
                        lastDocument |> Option.map (fun last -> readOnly statements, last)
                    else None
            with
            | :? SqlParseException as ex ->
                if not (isNull ex.Diagnostic)
                   && ex.Diagnostic.Category = SqlDiagnosticCategory.Resource then
                    reraise()
                None

    // This local, non-executable grammar is intentionally NOT a mode on the
    // general SQL lexer/parser. PostgreSQL positional $n parameter syntax
    // belongs only to a single bounded SQL-language FUNCTION body; raw SQL,
    // procedure bodies and other providers retain their strict lexical rules.
    // Each admitted expression becomes a private typed syntax node; the text
    // is never rewritten, interpolated, bound as an ordinary SQL identifier,
    // or forwarded to executable rendering.
    type private PositionalExpr =
        | OrdinalRef of int
        | NotOrdinal of int
        | BinaryOrdinals of int * BinaryOperator * int

    let private positionalRegex (pattern: string) =
        System.Text.RegularExpressions.Regex(
            pattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant
            ||| System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds 100.)

    // Deliberately refuse comments, aliases, functions, literals, joins,
    // nested SELECT, quoted identifiers, arbitrary casts or more statements.
    // ASCII whitespace is explicit, avoiding dialect-dependent token gaps.
    let private positionalDirect = positionalRegex
        @"\A[ \t\r\n]*SELECT[ \t\r\n]+\$(?<one>[1-9][0-9]*)[ \t\r\n]*;?[ \t\r\n]*\z"
    let private positionalNot = positionalRegex
        @"\A[ \t\r\n]*SELECT[ \t\r\n]+NOT[ \t\r\n]+\$(?<one>[1-9][0-9]*)[ \t\r\n]*;?[ \t\r\n]*\z"
    let private positionalBinary = positionalRegex
        @"\A[ \t\r\n]*SELECT[ \t\r\n]+\$(?<one>[1-9][0-9]*)[ \t\r\n]+(?<op>AND|OR|\+|-|\*)[ \t\r\n]+\$(?<two>[1-9][0-9]*)[ \t\r\n]*;?[ \t\r\n]*\z"

    let private boundedOrdinal (group: System.Text.RegularExpressions.Group) =
        let mutable result = 0
        if group.Success
           && Int32.TryParse(group.Value, &result)
           && result >= 1 && result <= MaxParameters then Some result
        else None

    let private positionalExpr (body: string) =
        let direct = positionalDirect.Match(body)
        if direct.Success then
            boundedOrdinal direct.Groups["one"] |> Option.map OrdinalRef
        else
            let negated = positionalNot.Match(body)
            if negated.Success then
                boundedOrdinal negated.Groups["one"] |> Option.map NotOrdinal
            else
                let binary = positionalBinary.Match(body)
                if not binary.Success then None
                else
                    let op =
                        match binary.Groups["op"].Value.ToUpperInvariant() with
                        | "+" -> Some BinaryOperator.Add
                        | "-" -> Some BinaryOperator.Subtract
                        | "*" -> Some BinaryOperator.Multiply
                        | "AND" -> Some BinaryOperator.And
                        | "OR" -> Some BinaryOperator.Or
                        | _ -> None
                    match boundedOrdinal binary.Groups["one"],
                          op, boundedOrdinal binary.Groups["two"] with
                    | Some left, Some operation, Some right ->
                        Some(BinaryOrdinals(left, operation, right))
                    | _ -> None

    let private positionalReturn (symbols: SqlFunctionParameterSymbol list)
                                 (expression: PositionalExpr) =
        let ordinalType ordinal =
            symbols |> List.tryPick (fun symbol ->
                if symbol.Ordinal = ordinal then Some symbol.DeclaredType else None)
        let numeric = function
            | SqlFunctionReturnType.Integer
            | SqlFunctionReturnType.BigInteger -> true
            | _ -> false
        match expression with
        | OrdinalRef i ->
            ordinalType i
            |> Option.map (fun actual ->
                actual, SqlFunctionReturnEvidence.PositionalParameterMatch)
        | NotOrdinal i ->
            match ordinalType i with
            | Some SqlFunctionReturnType.Boolean ->
                Some (SqlFunctionReturnType.Boolean,
                      SqlFunctionReturnEvidence.PrimitiveExpressionMatch)
            | _ -> None
        | BinaryOrdinals(left,op,right) ->
            match ordinalType left, ordinalType right with
            | Some a, Some b when
                op = BinaryOperator.And || op = BinaryOperator.Or ->
                if a = SqlFunctionReturnType.Boolean && b = SqlFunctionReturnType.Boolean then
                    Some (SqlFunctionReturnType.Boolean,
                          SqlFunctionReturnEvidence.PrimitiveExpressionMatch)
                else None
            | Some a, Some b when
                op = BinaryOperator.Add || op = BinaryOperator.Subtract
                || op = BinaryOperator.Multiply ->
                if numeric a && numeric b then
                    let result =
                        if a = SqlFunctionReturnType.BigInteger
                           || b = SqlFunctionReturnType.BigInteger then
                            SqlFunctionReturnType.BigInteger
                        else SqlFunctionReturnType.Integer
                    Some (result, SqlFunctionReturnEvidence.PrimitiveExpressionMatch)
                else None
            | _ -> None

    /// One FROM-free SELECT, one typed positional expression, no SQL execution.
    /// The body source span remains the original absolute string-literal span.
    let private positionalBody (body: string) bodyStart
                               (symbols: SqlFunctionParameterSymbol list) declared =
        RewriteResourceBudget.checkpoint ()
        if body.Length = 0 || body.Length > MaxCharacters || symbols.IsEmpty then None
        else
            positionalExpr body
            |> Option.bind (positionalReturn symbols)
            |> Option.bind (fun (inferred,evidence) ->
                if inferred <> declared then None
                else
                    let statement =
                        SqlProceduralBodyStatement(
                            SqlScriptStatementKind.Query,
                            SqlStatementEffectOperation.ReadTable,
                            SqlEffectKind.ReadData ||| SqlEffectKind.UnknownEffects,
                            readOnly Seq.empty<string>,
                            SqlDiagnosticSpan(bodyStart, body.Length))
                    Some (readOnly [statement], evidence))

    let private error (syntax: SqlScriptStatementSyntax) code message =
        SqlDiagnostic(
            code, SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Capability, message, syntax.Span)

    /// Do not produce partial function contracts on return-shape mismatch.
    let analyze profile (syntax: SqlScriptStatementSyntax) (tokens: Token[]) =
        match header profile syntax tokens with
        | None ->
            SqlStatementAnalysisStatus.Unsupported, [],
            [error syntax "SQL_PROCEDURAL_UNSUPPORTED"
                "Only qualified zero-argument PostgreSQL LANGUAGE SQL dollar-quoted PROCEDURE/FUNCTION headers with narrow scalar RETURNS are modeled."]
        | Some header ->
            let routine, body, bodyStart, declared, symbols =
                match header with
                | ProcedureHeader(name,sql,start) -> name,sql,start,None,[]
                | FunctionHeader(name,sql,start,returnType,parameters) ->
                    name,sql,start,Some returnType,parameters
            let candidate =
                match bodyStatements profile body bodyStart with
                | Some (statements, last) ->
                    // The ordinary closed SQL AST remains authoritative for
                    // existing body shapes. Do not reinterpret it as $n syntax.
                    let parametersSafe =
                        symbols.IsEmpty || (statements.Count = 1
                                            && statements[0].Kind = SqlScriptStatementKind.Query)
                    let contract =
                        match declared with
                        | None -> Some null
                        | Some returnType ->
                            if not parametersSafe then None
                            else returnEvidence returnType symbols last
                            |> Option.map (fun evidence ->
                                let span = (statements |> Seq.last).Span
                                SqlFunctionReturnContract(returnType,evidence,span))
                    match contract with
                    | Some result -> Ok (statements,result)
                    | None -> Error "SQL_PROCEDURAL_RETURN_UNPROVEN"
                | None ->
                    // Only a PostgreSQL FUNCTION with declared IN parameters
                    // can enter this private positional syntax-only path.
                    match declared with
                    | Some returnType when not symbols.IsEmpty ->
                        match positionalBody body bodyStart symbols returnType with
                        | Some (statements,evidence) ->
                            let span = (statements |> Seq.last).Span
                            Ok (statements,SqlFunctionReturnContract(returnType,evidence,span))
                        | None -> Error "SQL_PROCEDURAL_BODY_UNSUPPORTED"
                    | _ -> Error "SQL_PROCEDURAL_BODY_UNSUPPORTED"

            match candidate with
            | Error "SQL_PROCEDURAL_RETURN_UNPROVEN" ->
                SqlStatementAnalysisStatus.Unsupported, [],
                [error syntax "SQL_PROCEDURAL_RETURN_UNPROVEN"
                    "Function return type is unproven by the bounded literal, typed parameter, primitive expression or explicit-cast AST rules."]
            | Error _ ->
                SqlStatementAnalysisStatus.Unsupported, [],
                [error syntax "SQL_PROCEDURAL_BODY_UNSUPPORTED"
                    "SQL-language routine body exceeds budget or contains invalid/unmodeled SQL."]
            | Ok (statements, contract) ->
                let effects =
                    statements |> Seq.fold (fun all item -> all ||| item.PotentialEffects)
                        SqlEffectKind.UnknownEffects
                let kind =
                    if declared.IsSome then SqlProceduralDefinitionKind.Function
                    else SqlProceduralDefinitionKind.Procedure
                let definition =
                    SqlProceduralDefinition(
                        kind,routine,statements,effects,syntax.Span,contract,
                        readOnly symbols)
                SqlStatementAnalysisStatus.PartiallyAnalyzed, [definition], []
