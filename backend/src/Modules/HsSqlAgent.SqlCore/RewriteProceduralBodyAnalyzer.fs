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
                | Expr.Literal literal -> RewritePrimitiveTypeEvidence.literalType literal
                | Expr.Column identifier -> parameterType symbols identifier
                | Expr.Unary(op,operand) ->
                    infer operand |> Option.bind (RewritePrimitiveTypeEvidence.unaryType op)
                | Expr.Binary(op,left,right) ->
                    match infer left, infer right with
                    | Some a, Some b -> RewritePrimitiveTypeEvidence.binaryType op a b
                    | _ -> None
                | _ -> None
        infer expression

    /// No arbitrary CAST or implicit cast proof. Typed parameters use the
    /// same exact/widening rule as the private positional parser.
    let private castCompatible declared symbols (target: CastType) (value: Expr) =
        RewritePrimitiveTypeEvidence.castTargetType target = Some declared
        && (match Expr.unspan value with
            | Expr.Literal literal ->
                RewritePrimitiveTypeEvidence.literalCompatible declared literal
            | Expr.Column identifier ->
                match parameterType symbols identifier with
                | Some source ->
                    RewritePrimitiveTypeEvidence.parameterCastType source declared = Some declared
                | None -> false
            | _ -> false)

    /// Shared FROM-free single-expression return shape for both successful
    /// evidence and narrowly proven mismatch diagnostics. All other SQL
    /// shapes are *unproven*, never treated as a provable type conflict.
    let private singleReturnExpression (last: Document) =
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
            Some query.Head.Projection.Head.Expression
        | _ -> None

    let private returnEvidence declared (symbols: SqlFunctionParameterSymbol list) (last: Document) =
        if declared = SqlFunctionReturnType.Void then
            Some SqlFunctionReturnEvidence.VoidNoValueRequired
        else
            match singleReturnExpression last with
            | Some expression ->
                match Expr.unspan expression with
                | Expr.Literal literal when
                    declared <> SqlFunctionReturnType.BigInteger
                    && RewritePrimitiveTypeEvidence.literalCompatible declared literal ->
                    Some SqlFunctionReturnEvidence.LiteralMatch
                | Expr.Cast(value,target) when castCompatible declared symbols target value ->
                    Some SqlFunctionReturnEvidence.ExplicitCastMatch
                | Expr.Column identifier ->
                    if parameterType symbols identifier = Some declared then
                        Some SqlFunctionReturnEvidence.ParameterSymbolMatch
                    else None
                | Expr.Unary _ | Expr.Binary _ ->
                    if primitiveExpressionType symbols expression = Some declared then
                        Some SqlFunctionReturnEvidence.PrimitiveExpressionMatch
                    else None
                | _ -> None
            | None -> None

    /// Only an exact source-validated primitive expression can produce a
    /// *static* type mismatch. A CAST, function call, NULL, division, sourced
    /// column or arbitrary expression is deliberately unproven here.
    /// This does not decide PostgreSQL's runtime assignment casts.
    let private knownReturnType (symbols: SqlFunctionParameterSymbol list) (last: Document) =
        singleReturnExpression last
        |> Option.bind (fun expression ->
            match Expr.unspan expression with
            | Expr.Literal literal -> RewritePrimitiveTypeEvidence.literalType literal
            | Expr.Column identifier -> parameterType symbols identifier
            | Expr.Unary _ | Expr.Binary _ -> primitiveExpressionType symbols expression
            | _ -> None)

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

    // Private Function-only parser. The normal SQL Lexer and executable
    // compiler still reject PostgreSQL positional parameters. This parser
    // never rewrites SQL or treats textual substitutions as typed binding.
    type private PositionalExpr =
        | OrdinalRef of int
        | NamedRef of string
        | IntegerValue of int64
        | BooleanValue of bool
        | UnaryValue of UnaryOperator * PositionalExpr
        | BinaryValue of BinaryOperator * PositionalExpr * PositionalExpr
        | CastValue of PositionalExpr * SqlFunctionReturnType

    type private PositionalToken =
        | WordToken of string
        | OrdinalToken of int
        | IntegerToken of int64
        | PunctuationToken of char

    [<Literal>]
    let private MaxPositionalTokens = 64

    [<Literal>]
    let private MaxPositionalDepth = 16

    let private isAsciiLetter ch =
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch = '_'

    let private isAsciiDigit ch = ch >= '0' && ch <= '9'

    let private isAsciiSpace ch =
        ch = ' ' || ch = '\t' || ch = '\r' || ch = '\n'

    let private positionalTokens (body: string) =
        if body.Length = 0 || body.Length > MaxCharacters then None
        else
            let tokens = ResizeArray<PositionalToken>()
            let mutable cursor = 0
            let mutable valid = true
            let mutable hasOrdinal = false
            while valid && cursor < body.Length && tokens.Count <= MaxPositionalTokens do
                RewriteResourceBudget.checkpoint ()
                let ch = body[cursor]
                if isAsciiSpace ch then
                    cursor <- cursor + 1
                elif ch = '$' then
                    let first = cursor + 1
                    cursor <- first
                    while cursor < body.Length && isAsciiDigit body[cursor] do
                        cursor <- cursor + 1
                    let digits = body.Substring(first, cursor - first)
                    let mutable ordinal = 0
                    if digits.Length = 0 || digits[0] = '0'
                       || not (Int32.TryParse(digits, &ordinal))
                       || ordinal < 1 || ordinal > MaxParameters then
                        valid <- false
                    else
                        hasOrdinal <- true
                        tokens.Add(OrdinalToken ordinal)
                elif isAsciiLetter ch then
                    let first = cursor
                    cursor <- cursor + 1
                    // $ is part of PostgreSQL unquoted identifier spelling,
                    // so SELECT$1 is never split into keyword and ordinal.
                    while cursor < body.Length
                          && (isAsciiLetter body[cursor]
                              || isAsciiDigit body[cursor]
                              || body[cursor] = '$') do
                        cursor <- cursor + 1
                    tokens.Add(WordToken(body.Substring(first, cursor - first).ToUpperInvariant()))
                elif isAsciiDigit ch then
                    let first = cursor
                    cursor <- cursor + 1
                    while cursor < body.Length && isAsciiDigit body[cursor] do
                        cursor <- cursor + 1
                    let digits = body.Substring(first, cursor-first)
                    let mutable value = 0L
                    if not (Int64.TryParse(digits, &value)) then valid <- false
                    else tokens.Add(IntegerToken value)
                elif ch = '-' && cursor + 1 < body.Length && body[cursor+1] = '-' then
                    // Never reinterpret the SQL line-comment introducer
                    // as adjacent arithmetic/unary minus operators.
                    valid <- false
                elif ch = '(' || ch = ')' || ch = '+' || ch = '-'
                     || ch = '*' || ch = ';' then
                    tokens.Add(PunctuationToken ch)
                    cursor <- cursor + 1
                else valid <- false
            if not valid || not hasOrdinal || cursor <> body.Length
               || tokens.Count > MaxPositionalTokens then None
            else Some (tokens.ToArray())

    let private positionalExpr (symbols: SqlFunctionParameterSymbol list) body =
        positionalTokens body
        |> Option.bind (fun tokens ->
            let mutable index = 0
            let current () =
                if index < tokens.Length then Some tokens[index] else None
            let accept token =
                if current () = Some token then
                    index <- index + 1
                    true
                else false
            let knownName (name: string) =
                symbols |> List.exists (fun symbol ->
                    symbol.Name = name.ToLowerInvariant())
            let operatorAt () =
                match current () with
                | Some (WordToken "OR") -> Some (BinaryOperator.Or, 1)
                | Some (WordToken "AND") -> Some (BinaryOperator.And, 2)
                | Some (PunctuationToken '+') -> Some (BinaryOperator.Add, 3)
                | Some (PunctuationToken '-') -> Some (BinaryOperator.Subtract, 3)
                | Some (PunctuationToken '*') -> Some (BinaryOperator.Multiply, 4)
                | _ -> None
            let rec parseExpr minimum depth =
                if depth > MaxPositionalDepth then None
                else
                    match parseUnary (depth+1) with
                    | None -> None
                    | Some first ->
                        let mutable left = first
                        let mutable valid = true
                        let mutable going = true
                        while going && valid do
                            RewriteResourceBudget.checkpoint ()
                            match operatorAt () with
                            | Some (op, precedence) when precedence >= minimum ->
                                index <- index + 1
                                match parseExpr (precedence+1) (depth+1) with
                                | Some right -> left <- BinaryValue(op,left,right)
                                | None -> valid <- false
                            | _ -> going <- false
                        if valid then Some left else None
            and parseUnary depth =
                if depth > MaxPositionalDepth then None
                else
                    match current () with
                    | Some (WordToken "NOT") ->
                        index <- index+1
                        parseUnary (depth+1)
                        |> Option.map (fun inner -> UnaryValue(UnaryOperator.Not,inner))
                    | Some (PunctuationToken '-') ->
                        index <- index+1
                        parseUnary (depth+1)
                        |> Option.map (fun inner -> UnaryValue(UnaryOperator.Negate,inner))
                    | Some (PunctuationToken '+') ->
                        index <- index+1
                        parseUnary (depth+1)
                        |> Option.map (fun inner -> UnaryValue(UnaryOperator.Positive,inner))
                    | _ -> parseAtom (depth+1)
            and parseAtom depth =
                if depth > MaxPositionalDepth then None
                else
                    match current () with
                    | Some (OrdinalToken ordinal) ->
                        index <- index+1
                        Some (OrdinalRef ordinal)
                    | Some (IntegerToken value) ->
                        index <- index+1
                        Some (IntegerValue value)
                    | Some (WordToken "TRUE") ->
                        index <- index+1
                        Some (BooleanValue true)
                    | Some (WordToken "FALSE") ->
                        index <- index+1
                        Some (BooleanValue false)
                    | Some (PunctuationToken '(') ->
                        index <- index+1
                        match parseExpr 1 (depth+1) with
                        | Some inner when accept (PunctuationToken ')') -> Some inner
                        | _ -> None
                    | Some (WordToken "CAST") ->
                        index <- index+1
                        if not (accept (PunctuationToken '(')) then None
                        else
                            match parseExpr 1 (depth+1) with
                            | None -> None
                            | Some value when accept (WordToken "AS") ->
                                let castType =
                                    match current () with
                                    | Some (WordToken ("INT" | "INT4" | "INTEGER")) ->
                                        Some SqlFunctionReturnType.Integer
                                    | Some (WordToken ("INT8" | "BIGINT")) ->
                                        Some SqlFunctionReturnType.BigInteger
                                    | Some (WordToken ("BOOLEAN" | "BOOL")) ->
                                        Some SqlFunctionReturnType.Boolean
                                    | Some (WordToken "TEXT") ->
                                        Some SqlFunctionReturnType.Text
                                    | _ -> None
                                match castType with
                                | Some target ->
                                    index <- index+1
                                    if accept (PunctuationToken ')') then
                                        // Only a single declared argument can
                                        // be cast: no unproven expression casts.
                                        match value with
                                        | OrdinalRef _ | NamedRef _ -> Some(CastValue(value,target))
                                        | _ -> None
                                    else None
                                | None -> None
                            | _ -> None
                    | Some (WordToken name) when knownName name ->
                        index <- index+1
                        Some (NamedRef(name.ToLowerInvariant()))
                    | _ -> None
            if not (accept (WordToken "SELECT")) then None
            else
                match parseExpr 1 0 with
                | Some expression ->
                    accept (PunctuationToken ';') |> ignore
                    if index = tokens.Length then Some expression else None
                | None -> None)

    let private positionalReturn (symbols: SqlFunctionParameterSymbol list)
                                 (expression: PositionalExpr) =
        let mutable remaining = MaxPositionalTokens
        let rec infer expr =
            RewriteResourceBudget.checkpoint ()
            remaining <- remaining - 1
            if remaining < 0 then None
            else
                match expr with
                | OrdinalRef ordinal ->
                    symbols |> List.tryPick (fun symbol ->
                        if symbol.Ordinal = ordinal then
                            Some(symbol.DeclaredType, SqlFunctionReturnEvidence.PositionalParameterMatch)
                        else None)
                | NamedRef name ->
                    symbols |> List.tryPick (fun symbol ->
                        if symbol.Name = name then
                            Some(symbol.DeclaredType, SqlFunctionReturnEvidence.ParameterSymbolMatch)
                        else None)
                | IntegerValue number ->
                    Some(RewritePrimitiveTypeEvidence.integerType number,
                         SqlFunctionReturnEvidence.PrimitiveExpressionMatch)
                | BooleanValue _ ->
                    Some(SqlFunctionReturnType.Boolean,
                         SqlFunctionReturnEvidence.PrimitiveExpressionMatch)
                | UnaryValue(op,inner) ->
                    match infer inner with
                    | Some(source,_) ->
                        RewritePrimitiveTypeEvidence.unaryType op source
                        |> Option.map (fun t ->
                            t, SqlFunctionReturnEvidence.PrimitiveExpressionMatch)
                    | _ -> None
                | BinaryValue(op,left,right) ->
                    match infer left, infer right with
                    | Some(a,_), Some(b,_) ->
                        RewritePrimitiveTypeEvidence.binaryType op a b
                        |> Option.map (fun t ->
                            t, SqlFunctionReturnEvidence.PrimitiveExpressionMatch)
                    | _ -> None
                | CastValue(value,target) ->
                    match infer value with
                    | Some(source,_) ->
                        RewritePrimitiveTypeEvidence.parameterCastType source target
                        |> Option.map (fun t ->
                            t, SqlFunctionReturnEvidence.ExplicitCastMatch)
                    | _ -> None
        infer expression

    /// Function-only body assessment; never converted into an executable
    /// Query AST. Unknown type evidence is distinct from a proven mismatch.
    type private PositionalBodyResult =
        | PositionalProven of IReadOnlyList<SqlProceduralBodyStatement> * SqlFunctionReturnEvidence
        | PositionalMismatch of SqlFunctionReturnType
        | PositionalUnproven
        | PositionalUnsupported

    /// One FROM-free SELECT and one expression, with no execution or
    /// server-side type/authorization proof. The original absolute span
    /// and conservative potential effects remain unchanged.
    let private positionalBody (body: string) bodyStart
                               (symbols: SqlFunctionParameterSymbol list) declared =
        RewriteResourceBudget.checkpoint ()
        if body.Length = 0 || body.Length > MaxCharacters || symbols.IsEmpty then
            PositionalUnsupported
        else
            match positionalExpr symbols body with
            | None -> PositionalUnsupported
            | Some expression ->
                match positionalReturn symbols expression with
                | None -> PositionalUnproven
                | Some(inferred,_) when inferred <> declared ->
                    PositionalMismatch inferred
                | Some(_, evidence) ->
                    let statement =
                        SqlProceduralBodyStatement(
                            SqlScriptStatementKind.Query,
                            SqlStatementEffectOperation.ReadTable,
                            SqlEffectKind.ReadData ||| SqlEffectKind.UnknownEffects,
                            readOnly Seq.empty<string>,
                            SqlDiagnosticSpan(bodyStart, body.Length))
                    PositionalProven(readOnly [statement], evidence)

    /// A static proof failure is *not* proof that PostgreSQL would reject
    /// CREATE FUNCTION or apply no implicit assignment cast.
    type private BodyProofFailure =
        | KnownPrimitiveMismatch of SqlFunctionReturnType * SqlDiagnosticSpan
        | ReturnTypeUnproven of SqlDiagnosticSpan
        | BodyGrammarUnsupported of SqlDiagnosticSpan

    let private errorAt span code category message =
        SqlDiagnostic(
            code, SqlDiagnosticStage.SemanticValidation,
            category, message, span)

    /// Do not produce partial function contracts on return-shape mismatch.
    let analyze profile (syntax: SqlScriptStatementSyntax) (tokens: Token[]) =
        match header profile syntax tokens with
        | None ->
            SqlStatementAnalysisStatus.Unsupported, [],
            [errorAt syntax.Span "SQL_PROCEDURAL_UNSUPPORTED" SqlDiagnosticCategory.Capability
                "Only qualified zero-argument PostgreSQL LANGUAGE SQL dollar-quoted PROCEDURE/FUNCTION headers with narrow scalar RETURNS are modeled."]
        | Some header ->
            let routine, body, bodyStart, declared, symbols =
                match header with
                | ProcedureHeader(name,sql,start) -> name,sql,start,None,[]
                | FunctionHeader(name,sql,start,returnType,parameters) ->
                    name,sql,start,Some returnType,parameters
            // Body-local, absolute diagnostic positions: the failure is
            // about the bounded SELECT result rather than the CREATE header.
            let fallbackSpan = SqlDiagnosticSpan(bodyStart, body.Length)
            let candidate =
                match bodyStatements profile body bodyStart with
                | Some (statements, last) ->
                    let span = (statements |> Seq.last).Span
                    // Keep all existing safety gates on parameterized
                    // multi-statement bodies and unmodeled SQL expressions.
                    let parametersSafe =
                        symbols.IsEmpty || (statements.Count = 1
                                            && statements[0].Kind = SqlScriptStatementKind.Query)
                    match declared with
                    | None -> Ok (statements, null)
                    | Some returnType when not parametersSafe ->
                        Error (ReturnTypeUnproven span)
                    | Some returnType ->
                        match returnEvidence returnType symbols last with
                        | Some evidence ->
                            Ok (statements, SqlFunctionReturnContract(returnType,evidence,span))
                        | None ->
                            match knownReturnType symbols last with
                            | Some actual when actual <> returnType ->
                                Error (KnownPrimitiveMismatch(actual,span))
                            | _ -> Error (ReturnTypeUnproven span)
                | None ->
                    // Only the named-parameter PostgreSQL Function grammar
                    // may enter the private positional-only parser.
                    match declared with
                    | Some returnType when not symbols.IsEmpty ->
                        match positionalBody body bodyStart symbols returnType with
                        | PositionalProven(statements,evidence) ->
                            let span = (statements |> Seq.last).Span
                            Ok (statements,SqlFunctionReturnContract(returnType,evidence,span))
                        | PositionalMismatch actual ->
                            Error (KnownPrimitiveMismatch(actual,fallbackSpan))
                        | PositionalUnproven -> Error (ReturnTypeUnproven fallbackSpan)
                        | PositionalUnsupported -> Error (BodyGrammarUnsupported fallbackSpan)
                    | _ -> Error (BodyGrammarUnsupported fallbackSpan)

            match candidate with
            | Error (KnownPrimitiveMismatch(actual,span)) ->
                SqlStatementAnalysisStatus.Unsupported, [],
                [errorAt span "SQL_PROCEDURAL_RETURN_TYPE_MISMATCH"
                    SqlDiagnosticCategory.Semantic
                    (sprintf "Modeled static primitive result %A differs from RETURNS %A. Implicit casts, server behavior and execution have not been proven."
                        actual (Option.get declared))]
            | Error (ReturnTypeUnproven span) ->
                SqlStatementAnalysisStatus.Unsupported, [],
                [errorAt span "SQL_PROCEDURAL_RETURN_UNPROVEN"
                    SqlDiagnosticCategory.Capability
                    "Function return type is unproven by the bounded literal, typed parameter, primitive expression or explicit-cast AST rules."]
            | Error (BodyGrammarUnsupported span) ->
                SqlStatementAnalysisStatus.Unsupported, [],
                [errorAt span "SQL_PROCEDURAL_BODY_UNSUPPORTED"
                    SqlDiagnosticCategory.Capability
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
