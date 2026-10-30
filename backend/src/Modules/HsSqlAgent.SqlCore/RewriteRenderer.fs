namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Text
open System.Text.RegularExpressions
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteRenderer =

    type Provider = PostgreSql | MySql | SqlServer | SQLite | Oracle | Firebird
    type RenderedCommand = { Sql: string; Parameters: (obj | null) list; ReturnsRows: bool }

    // Independent of the source lexer character/token caps: a dialect
    // renderer can expand validated AST nodes when lowering SQL.
    [<Literal>]
    let private MaxRenderedSqlLength = 16777216 // UTF-16 code units

    // Guard transient SQL-emitter copies independently of final SQL size.
    // This is a conservative work ceiling, NOT an absolute process RSS cap.
    [<Literal>]
    let private MaxRenderedSqlWorkLength = 134217728L // aggregate appended UTF-16 units

    type private BindingState =
        ImmutableList<obj | null> * ImmutableDictionary<string, string * (obj | null)> * string option * int

    type private RenderFrame(action: unit -> obj | null, start: BindingState) =
        let children = ResizeArray<RenderFrame>()
        member _.Run() = action ()
        member _.Start = start
        member _.Children = children
        member val NextChild = 0 with get, set
        member val Result: ((obj | null) * BindingState) voption = ValueNone with get, set

    type private RenderSuspension(owner: obj, frame: RenderFrame) =
        inherit Exception()
        member _.Owner = owner
        member _.Frame = frame

    type private RenderContext(provider: Provider, targetRuntime: TargetRuntime, sourceSpan: Span) =
        let mutable parameters = ImmutableList<obj | null>.Empty
        let mutable sharedParameters = ImmutableDictionary.Create<string, string * (obj | null)>(StringComparer.Ordinal)
        let mutable sharedScope: string option = None
        let mutable sharedOrdinal = 0
        let mutable appendedWork = 0L
        let owner = obj ()
        let mutable driving = false
        let mutable current: RenderFrame option = None
        let mutable inlineDepth = 0
        let mutable activeQuery: Query option = None
        // Structural eligibility only: no SQL, scopes or bindings are cached
        // by Query identity. Emission results remain occurrence-specific.
        let localQueryProofs = Dictionary<Query, bool>(HashIdentity.Reference)
        let capture () : BindingState = parameters, sharedParameters, sharedScope, sharedOrdinal
        let restore (values, shared, scope, ordinal) =
            parameters <- values
            sharedParameters <- shared
            sharedScope <- scope
            sharedOrdinal <- ordinal

        let failBudget code detail : 'T =
            let location =
                if sourceSpan.Start >= 0 && sourceSpan.Length >= 0 then
                    SqlDiagnosticSpan(sourceSpan.Start, sourceSpan.Length)
                else
                    Unchecked.defaultof<SqlDiagnosticSpan>
            raise (
                SqlCompilationException(
                    detail,
                    SqlDiagnostic(
                        code,
                        SqlDiagnosticStage.RenderingInvariant,
                        SqlDiagnosticCategory.Resource,
                        detail,
                        location)))

        let parameterName index =
            match provider with Oracle -> ":p" + string index | _ -> "@p" + string index

        member _.Provider = provider
        member _.TargetRuntime = targetRuntime

        member this.Bind(value: obj | null) =
            RewriteResourceBudget.checkpoint ()
            match sharedScope with
            | Some scope ->
                let key = scope + ":" + string sharedOrdinal
                sharedOrdinal <- sharedOrdinal + 1
                this.BindShared(key, value)
            | None ->
                let name = parameterName parameters.Count
                parameters <- parameters.Add(value)
                name

        member _.BindShared(key: string, value: obj | null) =
            RewriteResourceBudget.checkpoint ()
            match sharedParameters.TryGetValue(key) with
            | true, (name, existing) ->
                if not (Object.Equals(existing, value)) then
                    raise (SqlCompilationException(
                        "Native SQL renderer reused semantic binding key '" + key + "' for different values."))
                name
            | false, _ ->
                let name = parameterName parameters.Count
                parameters <- parameters.Add(value)
                sharedParameters <- sharedParameters.Add(key, (name, value))
                name

        member _.WithSharedBindings(scope: string, action: unit -> 'T) : 'T =
            let previousScope = sharedScope
            let previousOrdinal = sharedOrdinal
            sharedScope <- Some scope
            sharedOrdinal <- 0
            try action ()
            finally
                sharedScope <- previousScope
                sharedOrdinal <- previousOrdinal

        member _.Parameters = parameters |> Seq.toList

        /// Resume rendering at occurrence boundaries. Persistent binding
        /// snapshots make retries deterministic without retaining/copying an
        /// accumulated parameter array at every Query depth. Work/allocation
        /// budgets are never rolled back along with semantic binding state.
        member this.ResolveLocal(action: unit -> 'T) : 'T =
            let previousCurrent = current
            let previousDriving = driving
            let previousDepth = inlineDepth
            driving <- false
            try this.Resolve(action, false)
            finally
                current <- previousCurrent
                driving <- previousDriving
                inlineDepth <- previousDepth

        member this.ResolveQuery(query: Query, action: unit -> 'T) : 'T =
            let isLocal () =
                match localQueryProofs.TryGetValue query with
                | true, value -> value
                | _ ->
                    let value = RewriteAstTraversal.isLocalQuery query
                    localQueryProofs.Add(query, value)
                    value
            let run () =
                let previous = activeQuery
                activeQuery <- Some query
                try action ()
                finally activeQuery <- previous
            match activeQuery with
            | Some active when Object.ReferenceEquals(active, query) -> action ()
            // Entry/core/subquery helpers for one occurrence are a fixed
            // call chain, not a child Query edge. Schedule that occurrence
            // once; each actual child Query still crosses the heap driver.
            | _ when isLocal () -> this.ResolveLocal run
            | _ -> this.Resolve(run, false)

        member _.Resolve(action: unit -> 'T, inlineAllowed: bool) : 'T =
            RewriteResourceBudget.checkpoint ()
            if driving && inlineAllowed && inlineDepth < 4 then
                inlineDepth <- inlineDepth + 1
                try action ()
                finally inlineDepth <- inlineDepth - 1
            elif driving then
                let parent = Option.get current
                let index = parent.NextChild
                parent.NextChild <- index + 1
                let child =
                    if index < parent.Children.Count then parent.Children.[index]
                    else
                        let frame = RenderFrame((fun () -> box (action ())), capture ())
                        parent.Children.Add frame
                        frame
                match child.Result with
                | ValueSome(value, finish) -> restore finish; unbox<'T> value
                | ValueNone -> raise (RenderSuspension(owner, child))
            else
                let root = RenderFrame((fun () -> box (action ())), capture ())
                let pending = ResizeArray<RenderFrame>()
                pending.Add root
                driving <- true
                try
                    while pending.Count > 0 do
                        RewriteResourceBudget.checkpoint ()
                        let last = pending.Count - 1
                        let frame = pending.[last]
                        current <- Some frame
                        inlineDepth <- 0
                        frame.NextChild <- 0
                        restore frame.Start
                        try
                            let value = frame.Run()
                            frame.Result <- ValueSome(value, capture ())
                            pending.RemoveAt last
                        with :? RenderSuspension as suspension
                            when Object.ReferenceEquals(owner, suspension.Owner) ->
                            pending.Add suspension.Frame
                    match root.Result with
                    | ValueSome(value, finish) -> restore finish; unbox<'T> value
                    | ValueNone -> invalidOp "Renderer continuation did not produce a document."
                finally
                    current <- None
                    driving <- false
                    inlineDepth <- 0

        member _.CheckDocumentLength(length: int64) =
            RewriteResourceBudget.checkpoint ()
            if length > int64 MaxRenderedSqlLength then
                failBudget "SQL_COMPILER_RENDERED_OUTPUT_LIMIT"
                    ("Rendered SQL exceeds the maximum " + string MaxRenderedSqlLength + " UTF-16 code units.")

        // The limit is checked BEFORE StringBuilder grows, so a huge
        // intermediate buffer cannot allocate above the rendered-SQL cap.
        // Aggregate emitted work also bounds repeated text-copy paths.
        member _.CheckAppend(existingLength: int, addedLength: int) =
            RewriteResourceBudget.checkpoint ()
            if int64 existingLength + int64 addedLength > int64 MaxRenderedSqlLength then
                failBudget
                    "SQL_COMPILER_RENDERED_OUTPUT_LIMIT"
                    ("Rendered SQL exceeds the maximum "
                     + string MaxRenderedSqlLength + " UTF-16 code units.")
            if appendedWork + int64 addedLength > MaxRenderedSqlWorkLength then
                failBudget
                    "SQL_COMPILER_RENDER_WORK_LIMIT"
                    ("SQL rendering exceeds the maximum "
                     + string MaxRenderedSqlWorkLength + " appended UTF-16 code units.")
            appendedWork <- appendedWork + int64 addedLength

    /// SQL documents compose by reference. No parent Query materializes a
    /// child's complete SQL text; only the final bounded emitter copies text.
    type private SqlDocument(ctx: RenderContext, node: SqlDocumentNode, length: int) =
        member _.Context = ctx
        member _.Node = node
        member _.Length = length
        member this.Substring(start: int) =
            if start < 0 || start > length then invalidArg "start" "Invalid SQL document slice."
            RewriteResourceBudget.checkpoint ()
            SqlDocument(ctx, DocumentSlice(this, start), length - start)
        static member ReservedText(ctx: RenderContext, value: string) =
            ctx.CheckDocumentLength(int64 value.Length)
            SqlDocument(ctx, DocumentText value, value.Length)
        static member Text(ctx: RenderContext, value: string) =
            ctx.CheckAppend(0, value.Length)
            SqlDocument(ctx, DocumentText value, value.Length)
        static member Sequence(ctx: RenderContext, values: SqlDocument array) =
            let mutable length = 0L
            for value in values do
                length <- length + int64 value.Length
                ctx.CheckDocumentLength length
            SqlDocument(ctx, DocumentSequence values, int length)
    and private SqlDocumentNode =
        | DocumentText of string
        | DocumentSequence of SqlDocument array
        | DocumentSlice of SqlDocument * int

    let private doc ctx value = SqlDocument.Text(ctx, value)
    let private addDocument ctx (left: obj) (right: obj) =
        let convert (value: obj) =
            match value with
            | :? SqlDocument as value -> value
            | :? string as value -> doc ctx value
            | _ -> invalidOp "Only SQL documents/text can be concatenated."
        SqlDocument.Sequence(ctx, [| convert left; convert right |])
    let private joinDocuments ctx separator (items: seq<SqlDocument>) =
        let parts = ResizeArray<SqlDocument>()
        for item in items do
            if parts.Count > 0 then parts.Add(doc ctx separator)
            parts.Add item
        SqlDocument.Sequence(ctx, parts.ToArray())

    type private RenderBuffer(ctx: RenderContext, initial: SqlDocument) =
        let parts = ResizeArray<SqlDocument>()
        let mutable length = 0L
        do parts.Add initial; length <- int64 initial.Length
        new(ctx, initial: string) = RenderBuffer(ctx, doc ctx initial)
        member this.Append(value: SqlDocument) =
            ctx.CheckDocumentLength(length + int64 value.Length)
            parts.Add value
            length <- length + int64 value.Length
            this
        member this.Append(value: string) = this.Append(doc ctx value)
        member this.Append(value: char) = this.Append(doc ctx (string value))
        member _.ToDocument() = SqlDocument.Sequence(ctx, parts.ToArray())

    let private emitDocument (ctx: RenderContext) (root: SqlDocument) =
        let output = StringBuilder()
        let pending = ResizeArray<struct (SqlDocument * int * int)>()
        pending.Add(struct (root, 0, root.Length))
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let struct (current, start, length) = pending.[last]
            pending.RemoveAt last
            match current.Node with
            | DocumentText value ->
                ctx.CheckAppend(output.Length, length)
                output.Append(value, start, length) |> ignore
            | DocumentSlice(inner, offset) -> pending.Add(struct (inner, start + offset, length))
            | DocumentSequence values ->
                let mutable childEnd = current.Length
                for index = values.Length - 1 downto 0 do
                    let child = values.[index]
                    let childStart = childEnd - child.Length
                    let lower = max start childStart
                    let upper = min (start + length) childEnd
                    if upper > lower then pending.Add(struct (child, lower - childStart, upper - lower))
                    childEnd <- childStart
        ctx.CheckAppend(0, output.Length)
        output.ToString()

    let private messageText ctx (message: obj) =
        match message with
        | :? string as value -> value
        | :? SqlDocument as value -> emitDocument ctx value
        | _ -> invalidOp "Compiler diagnostic message must be text."

    let private invalidDocument ctx message = invalidOp (messageText ctx message)
    let private quotePart (ctx: RenderContext) (part: IdentifierPart) =
        let provider = ctx.Provider
        let value = part.Value
        ctx.CheckDocumentLength(int64 value.Length + 2L)
        let escaped = match provider with MySql -> '`' | SqlServer -> ']' | _ -> '"'
        let mutable length = int64 value.Length + 2L
        let mutable index = 0
        while index < value.Length do
            let finish = min value.Length (index + 256)
            RewriteResourceBudget.charge (int64 (finish - index))
            for position = index to finish - 1 do
                if value.[position] = escaped then length <- length + 1L
            ctx.CheckDocumentLength length
            index <- finish
        // Reserve copied identifier text before case folding / escaping.
        ctx.CheckAppend(0, int length)
        let raw =
            if part.WasQuoted || part.PreserveSpelling then part.Value
            else
                match provider with
                | PostgreSql -> part.Value.ToLowerInvariant()
                | Oracle | Firebird -> part.Value.ToUpperInvariant()
                | _ -> part.Value
        let quoted =
            match provider with
            | MySql -> "`" + raw.Replace("`", "``") + "`"
            | SqlServer -> "[" + raw.Replace("]", "]]" ) + "]"
            | _ -> "\"" + raw.Replace("\"", "\"\"") + "\""
        SqlDocument.ReservedText(ctx, quoted)

    let private renderIdentifier ctx identifier =
        identifier |> Identifier.parts |> List.map (quotePart ctx) |> joinDocuments ctx "."
    let private renderAlias ctx alias = quotePart ctx alias

    let private renderFunctionName (ctx: RenderContext) functionName =
        functionName
        |> FunctionName.parts
        |> List.map (fun part ->
            if part.WasQuoted || part.PreserveSpelling then quotePart ctx part
            else
                ctx.CheckAppend(0, part.Value.Length)
                let value =
                    match ctx.Provider with
                    | PostgreSql | Oracle | Firebird -> part.Value.ToUpperInvariant()
                    | MySql | SqlServer | SQLite -> part.Value
                SqlDocument.ReservedText(ctx, value))
        |> joinDocuments ctx "."
    let private quoteSqlLiteral (ctx: RenderContext) (value: string) =
        ctx.CheckDocumentLength(int64 value.Length + 2L)
        let mutable quotedLength = int64 value.Length + 2L
        let mutable needsHex = false
        let mutable index = 0
        while index < value.Length do
            let finish = min value.Length (index + 256)
            RewriteResourceBudget.charge (int64 (finish - index))
            for position = index to finish - 1 do
                let character = value.[position]
                if character = '\'' then quotedLength <- quotedLength + 1L
                if character = '\\' || Char.IsControl(character) then needsHex <- true
            ctx.CheckDocumentLength quotedLength
            index <- finish
        if ctx.Provider = MySql && needsHex then
            ctx.CheckDocumentLength(2L + 2L * int64 value.Length)
            let byteCount = Encoding.UTF8.GetByteCount(value)
            let length = 2L + 2L * int64 byteCount
            ctx.CheckDocumentLength length
            ctx.CheckAppend(0, int length - 2)
            ctx.CheckAppend(0, int length)
            let bytes = Encoding.UTF8.GetBytes(value)
            SqlDocument.ReservedText(ctx, String.Concat("0x", Convert.ToHexString(bytes)))
        else
            if quotedLength > int64 value.Length + 2L then
                ctx.CheckAppend(0, int quotedLength - 2)
            ctx.CheckAppend(0, int quotedLength)
            SqlDocument.ReservedText(ctx, String.Concat("'", value.Replace("'", "''", StringComparison.Ordinal), "'"))

    let private tableAliasPrefix = function Oracle -> " " | _ -> " AS "
    let private providerName = function
        | PostgreSql -> "Postgres"
        | MySql -> "MySQL"
        | SqlServer -> "MsSqlServer"
        | SQLite -> "Sqlite"
        | Oracle -> "Oracle"
        | Firebird -> "Firebird"

    let private providerTool = function
        | PostgreSql -> SqlAgentToolType.Postgres
        | MySql -> SqlAgentToolType.MySQL
        | SqlServer -> SqlAgentToolType.MsSqlServer
        | SQLite -> SqlAgentToolType.Sqlite
        | Oracle -> SqlAgentToolType.Oracle
        | Firebird -> SqlAgentToolType.Firebird

    let private capabilityError provider capability =
        "SQL capability '" + capability + "' is not supported by provider " + providerName provider + " for this Core plan."

    let private scalarObject value : obj | null =
        match value with
        | ScalarValue.Null -> null
        | ScalarValue.Boolean value -> box value
        | ScalarValue.Integer value when value >= int64 Int32.MinValue && value <= int64 Int32.MaxValue -> box (int value)
        | ScalarValue.Integer value -> box (decimal value)
        | ScalarValue.Decimal value -> box value
        | ScalarValue.ExactNumeric _ ->
            raise (SqlCompilationException(
                "Exact NUMERIC values require a provider-proven, explicitly cast bound-text parameter."))
        | ScalarValue.Floating value -> box value
        | ScalarValue.Text value -> box value
        | ScalarValue.Date value -> box (value.ToDateTime(TimeOnly.MinValue))
        | ScalarValue.Time value -> box (value.ToTimeSpan())
        | ScalarValue.LocalDateTime value -> box (DateTime.SpecifyKind(value, DateTimeKind.Unspecified))
        | ScalarValue.OffsetDateTime value -> box value
        | ScalarValue.Duration value -> box value
        | ScalarValue.Bytes value -> box value

    let private renderLiteral (ctx: RenderContext) value =
        let bind scalar = ctx.Bind(scalarObject scalar)
        match ctx.Provider, value with
        | PostgreSql, ScalarValue.ExactNumeric number ->
            // A text parameter is never a SQL fragment. PostgreSQL performs
            // the explicit exact-numeric cast on the server; CLR decimal or
            // double conversion would silently alter the supplied value.
            "CAST(" + ctx.Bind(box (ExactNumeric.bindingText number)) + " AS NUMERIC)"
        | _, ScalarValue.ExactNumeric _ ->
            raise (SqlCompilationException(capabilityError ctx.Provider "numeric.exact_literal"))
        | PostgreSql, ScalarValue.OffsetDateTime offset ->
            ctx.Bind(box (offset.ToUniversalTime()))
        | Firebird, ScalarValue.Text text ->
            if text.Length > 8191 then invalidOp "Firebird string literal exceeds the safe UTF8 VARCHAR limit of 8191 characters."
            let placeholder = bind value
            "CAST(" + placeholder + " AS VARCHAR(" + string (max 1 text.Length) + "))"
        | Firebird, ScalarValue.Boolean _ ->
            "CAST(" + bind value + " AS BOOLEAN)"
        | Firebird, ScalarValue.Integer integer when integer >= int64 Int32.MinValue && integer <= int64 Int32.MaxValue ->
            "CAST(" + bind value + " AS INTEGER)"
        | Firebird, ScalarValue.Integer _ ->
            "CAST(" + bind value + " AS BIGINT)"
        | Firebird, ScalarValue.Decimal decimalValue ->
            "CAST(" + bind value + " AS " + SqlFirebirdDecimalCapabilityRules.FirebirdCastType(decimalValue) + ")"
        | Firebird, ScalarValue.Floating _ ->
            "CAST(" + bind value + " AS DOUBLE PRECISION)"
        | Firebird, ScalarValue.Date _ ->
            "CAST(" + bind value + " AS DATE)"
        | Firebird, ScalarValue.Time _ ->
            "CAST(" + bind value + " AS TIME)"
        | Firebird, ScalarValue.LocalDateTime _ ->
            "CAST(" + bind value + " AS TIMESTAMP)"
        | Firebird, ScalarValue.OffsetDateTime _ ->
            "CAST(" + bind value + " AS TIMESTAMP WITH TIME ZONE)"
        | _ -> bind value

    let private renderLikeEscape provider escape =
        let value = LikeEscape.value escape
        let escaped = (string value).Replace("'", "''", StringComparison.Ordinal)
        match value, provider with
        | '\\', PostgreSql -> "E'\\\\'"
        | '\\', MySql -> "CHAR(92)"
        | _ -> "'" + escaped + "'"

    let private binaryText = function
        | BinaryOperator.Add -> "+" | BinaryOperator.Subtract -> "-" | BinaryOperator.Multiply -> "*"
        | BinaryOperator.Divide -> "/" | BinaryOperator.Modulo -> "%" | BinaryOperator.Concat -> "||"
        | BinaryOperator.Equal -> "=" | BinaryOperator.NotEqual -> "<>" | BinaryOperator.GreaterThan -> ">"
        | BinaryOperator.LessThan -> "<" | BinaryOperator.GreaterThanOrEqual -> ">=" | BinaryOperator.LessThanOrEqual -> "<="
        | BinaryOperator.DistinctFrom -> "IS DISTINCT FROM" | BinaryOperator.NotDistinctFrom -> "IS NOT DISTINCT FROM"
        | BinaryOperator.And -> "AND" | BinaryOperator.Or -> "OR"

    let private joinText = function
        | JoinKind.Inner -> "INNER JOIN" | JoinKind.Left -> "LEFT JOIN" | JoinKind.Right -> "RIGHT JOIN"
        | JoinKind.Full -> "FULL OUTER JOIN" | JoinKind.Cross -> "CROSS JOIN"

    let private naturalJoinText = function
        | OnJoinKind.Inner -> "NATURAL JOIN"
        | OnJoinKind.Left -> "NATURAL LEFT JOIN"
        | OnJoinKind.Right -> "NATURAL RIGHT JOIN"
        | OnJoinKind.Full -> "NATURAL FULL OUTER JOIN"

    let private setText provider operator =
        match provider, operator with
        | Oracle, SetOperator.Except -> "MINUS"
        | Oracle, SetOperator.ExceptAll -> "MINUS ALL"
        | _, SetOperator.Union -> "UNION"
        | _, SetOperator.UnionAll -> "UNION ALL"
        | _, SetOperator.Intersect -> "INTERSECT"
        | _, SetOperator.IntersectAll -> "INTERSECT ALL"
        | _, SetOperator.Except -> "EXCEPT"
        | _, SetOperator.ExceptAll -> "EXCEPT ALL"

    let private frameBoundText = function
        | WindowFrameBound.UnboundedPreceding -> "UNBOUNDED PRECEDING"
        | WindowFrameBound.Preceding value -> string (FrameOffset.value value) + " PRECEDING"
        | WindowFrameBound.CurrentRow -> "CURRENT ROW"
        | WindowFrameBound.Following value -> string (FrameOffset.value value) + " FOLLOWING"
        | WindowFrameBound.UnboundedFollowing -> "UNBOUNDED FOLLOWING"

    let private projectionOutputName (item: SelectItem) =
        match item.Alias, Expr.unspan item.Expression with
        | Some alias, _ -> alias
        | None, Column identifier
        | None, BoundColumn(identifier, _) -> Identifier.parts identifier |> List.last
        | _ -> invalidOp "Pagination requires every projected output to have a stable name; use explicit aliases for computed expressions."

    let private isSetQuery (query: Query) = not query.SetOperations.IsEmpty
    let private hasTail (query: Query) =
        not query.OrderBy.IsEmpty || query.Limit.IsSome || query.Offset.IsSome || query.FetchPercent.IsSome || query.FetchWithTies

    // The renderer's Boolean CASE classifier must not recursively consume
    // one CLR call frame per nested CASE result. Keep its original proof:
    // only non-NULL result arms count, and WHEN predicates are not inspected.
    let private isBooleanExpression expression =
        let pending = ResizeArray<Expr>()
        pending.Add expression
        let mutable isBoolean = true
        while isBoolean && pending.Count > 0 do
            let index = pending.Count - 1
            let current = Expr.unspan pending.[index]
            pending.RemoveAt index
            match current with
            | Literal(ScalarValue.Boolean _) -> ()
            | IsNull _ | InList _ | InSubquery _ | Between _
            | Exists _ | Like _ | RegexMatch _ -> ()
            | Unary(UnaryOperator.Not, _) -> ()
            | Binary((BinaryOperator.Equal
                     | BinaryOperator.NotEqual
                     | BinaryOperator.GreaterThan
                     | BinaryOperator.LessThan
                     | BinaryOperator.GreaterThanOrEqual
                     | BinaryOperator.LessThanOrEqual
                     | BinaryOperator.DistinctFrom
                     | BinaryOperator.NotDistinctFrom
                     | BinaryOperator.And
                     | BinaryOperator.Or), _, _) -> ()
            | SimpleCase(_, branches, fallback) ->
                let mutable nonNull = 0
                let push value =
                    match Expr.unspan value with
                    | Literal ScalarValue.Null -> ()
                    | _ ->
                        nonNull <- nonNull + 1
                        pending.Add value
                fallback |> Option.iter push
                NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                    push branch.Result) branches
                if nonNull = 0 then isBoolean <- false
            | SearchedCase(branches, fallback) ->
                let mutable nonNull = 0
                let push value =
                    match Expr.unspan value with
                    | Literal ScalarValue.Null -> ()
                    | _ ->
                        nonNull <- nonNull + 1
                        pending.Add value
                fallback |> Option.iter push
                NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                    push branch.Result) branches
                if nonNull = 0 then isBoolean <- false
            | _ -> isBoolean <- false
        isBoolean

    let private renderBooleanTruthValue expression =
        match Expr.unspan expression with
        | Literal(ScalarValue.Boolean true) -> "1"
        | Literal(ScalarValue.Boolean false) -> "0"
        | Literal ScalarValue.Null -> "NULL"
        | _ ->
            raise (SqlCompilationException(
                "Boolean CASE predicates currently require literal TRUE, FALSE, or NULL branch results; richer predicate results remain fail-closed."))

    // Most SQL predicates contain a bound column and a literal, not a
    // recursive scalar graph. Preserve the old allocation-free leaf path
    // instead of constructing a StringBuilder and two frame stacks for
    // each comparison in a long AND/OR chain.
    let private isScalarLeaf expression =
        match Expr.unspan expression with
        | Expr.Column _ | Expr.BoundColumn _ | Expr.Wildcard _
        | Expr.OrderOrdinal _ | Expr.Literal _ | Expr.Interval _ -> true
        | _ -> false

    // SQL fragments are emitted in source order. Text frames and Expr
    // frames share one heap-backed stack, so right-deep and alternating
    // arithmetic / unary / predicate trees do not consume CLR call frames.
    // Distinct field names are required by an F# struct discriminated union.
    [<Struct>]
    type private ScalarRenderFrame =
        | ScalarNode of expression: Expr
        | ScalarText of emittedText: string
        | ScalarRegexCheck
        | ScalarCastComplete of castExpression: Expr
        | ScalarConcatProof of unprovenMessage: string

    // Nested PostgreSQL SET branches share an append-only SQL buffer.
    // Explicit frames avoid a CLR Query -> branch Query recursion and the
    // previous O(depth^2) intermediate SQL materialization.
    type private SetEmitFrame =
        | EnterSetBody of Query
        | EmitSetBranch of SetBranch
        | CloseNestedSet

    // Used by the mixed CTE / derived source streaming path. A single
    // output buffer owns every nested Query boundary, so alternating
    // wrappers never allocate and copy a complete child SQL string.
    type private MixedQueryEmitFrame =
        | FinishMixedCte of outer: Query * cte: Cte
        | FinishMixedDerived of outer: Query * alias: IdentifierPart
        | FinishMixedLateral of outer: Query * alias: IdentifierPart
        | FinishMixedJoinedLateral of outer: Query * preceding: TableSource * alias: IdentifierPart * onPredicate: Expr
        | FinishMixedPrefixedJoinedLateral of outer: Query * preceding: TableSource * earlier: Join list * alias: IdentifierPart * onPredicate: Expr

    let rec private renderExpr (ctx: RenderContext) expression : SqlDocument =
        let plain = Expr.unspan expression
        match plain with
        | Column _ | BoundColumn _ | Literal _ | Wildcard _ | OrderOrdinal _ | Interval _ ->
            renderExprCore ctx plain
        | _ -> ctx.Resolve((fun () -> renderExprCore ctx plain), true)

    and private renderExprCore (ctx: RenderContext) expression : SqlDocument =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        RewriteStackSafety.compile SqlDiagnosticStage.RenderingInvariant
        match expression with
        | Expr.Spanned _ -> renderExpr ctx (Expr.unspan expression)
        | Expr.Column identifier
        | Expr.BoundColumn(identifier, _) -> renderIdentifier ctx identifier
        | Expr.Wildcard None -> doc ctx "*"
        | Expr.Wildcard(Some identifier) -> renderIdentifier ctx identifier + ".*"
        | Expr.OrderOrdinal ordinal -> doc ctx (string (PositiveRowCount.value ordinal))
        | Expr.Literal value -> doc ctx (renderLiteral ctx value)
        | Expr.Interval literal ->
            match ctx.Provider with
            | PostgreSql -> "CAST(" + ctx.Bind(box (IntervalLiteral.value literal)) + " AS interval)"
            | _ -> invalidOp "INTERVAL lowering is not available for this provider."
        | Expr.DateAdd(unit, amountExpression, valueExpression) ->
            match SqlDateMathCapabilityRules.TargetValidationError(unit, providerTool ctx.Provider, "CORE_DATE_ADD") with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
            let amount = renderExpr ctx amountExpression
            let value = renderExpr ctx valueExpression
            let keyword = SqlDateMathUnit.Keyword unit
            match ctx.Provider with
            | SqlServer ->
                "DATEADD(" + keyword + ", " + amount + ", " + value + ")"
            | MySql ->
                "TIMESTAMPADD(" + keyword + ", " + amount + ", " + value + ")"
            | Firebird ->
                match unit with
                | SqlDateMathUnit.Quarter ->
                    "DATEADD(MONTH, (" + amount + " * 3), " + value + ")"
                | _ ->
                    "DATEADD(" + keyword + ", " + amount + ", " + value + ")"
            | PostgreSql ->
                let interval =
                    match unit with
                    | SqlDateMathUnit.Day -> "INTERVAL '1 day'"
                    | SqlDateMathUnit.Week -> "INTERVAL '1 week'"
                    | SqlDateMathUnit.Month -> "INTERVAL '1 month'"
                    | SqlDateMathUnit.Quarter -> "INTERVAL '3 months'"
                    | SqlDateMathUnit.Year -> "INTERVAL '1 year'"
                    | SqlDateMathUnit.Hour -> "INTERVAL '1 hour'"
                    | SqlDateMathUnit.Minute -> "INTERVAL '1 minute'"
                    | SqlDateMathUnit.Second -> "INTERVAL '1 second'"
                "(" + value + " + (" + interval + " * " + amount + "))"
            | Oracle ->
                let interval =
                    match unit with
                    | SqlDateMathUnit.Year
                    | SqlDateMathUnit.Quarter
                    | SqlDateMathUnit.Month ->
                        invalidOp "Oracle calendar-unit DATEADD reached rendering without a lossless rollover proof."
                    | SqlDateMathUnit.Week ->
                        "NUMTODSINTERVAL((" + amount + " * 7), 'DAY')"
                    | SqlDateMathUnit.Day ->
                        "NUMTODSINTERVAL(" + amount + ", 'DAY')"
                    | SqlDateMathUnit.Hour ->
                        "NUMTODSINTERVAL(" + amount + ", 'HOUR')"
                    | SqlDateMathUnit.Minute ->
                        "NUMTODSINTERVAL(" + amount + ", 'MINUTE')"
                    | SqlDateMathUnit.Second ->
                        "NUMTODSINTERVAL(" + amount + ", 'SECOND')"
                "(" + value + " + " + interval + ")"
            | SQLite ->
                let multiplier, modifier =
                    match unit with
                    | SqlDateMathUnit.Month
                    | SqlDateMathUnit.Quarter
                    | SqlDateMathUnit.Year ->
                        invalidOp "SQLite calendar-unit DATEADD reached rendering without a versioned floor-modifier proof."
                    | SqlDateMathUnit.Week -> 7, "day"
                    | SqlDateMathUnit.Day -> 1, "day"
                    | SqlDateMathUnit.Hour -> 1, "hour"
                    | SqlDateMathUnit.Minute -> 1, "minute"
                    | SqlDateMathUnit.Second -> 1, "second"
                let adjusted =
                    if multiplier = 1 then amount
                    else "(" + amount + " * " + string multiplier + ")"
                "DATETIME(" + value + ", PRINTF('%+d " + modifier + "', " + adjusted + "))"
        | Expr.DateDiff(unit, startExpression, finishExpression) ->
            match SqlDateMathCapabilityRules.TargetValidationError(unit, providerTool ctx.Provider, "CORE_DATE_DIFF") with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
            let keyword = SqlDateMathUnit.Keyword unit
            let startValue = renderExpr ctx startExpression
            let finish = renderExpr ctx finishExpression
            match ctx.Provider with
            | PostgreSql ->
                "(CAST(" + finish + " AS date) - CAST(" + startValue + " AS date))"
            | Oracle ->
                "(CAST(" + finish + " AS DATE) - CAST(" + startValue + " AS DATE))"
            | SQLite ->
                "(JULIANDAY(" + finish + ") - JULIANDAY(" + startValue + "))"
            | SqlServer ->
                "DATEDIFF(" + keyword + ", " + startValue + ", " + finish + ")"
            | MySql ->
                "TIMESTAMPDIFF(" + keyword + ", " + startValue + ", " + finish + ")"
            | Firebird ->
                "DATEDIFF(" + keyword + " FROM " + startValue + " TO " + finish + ")"
        | Expr.Unary _ -> renderScalarGraph ctx expression
        | Expr.Binary((BinaryOperator.And | BinaryOperator.Or), _, _) as chain ->
            renderBooleanGraph ctx renderExpr chain
        | Expr.Binary((BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom as op), left, right)
            when ctx.Provider = Oracle ->
            // Oracle null-safe comparison expands the already rendered
            // operands several times. Cache their SQL text once so bind
            // parameters are NOT allocated twice.
            let leftSql = renderExpr ctx left
            let rightSql = renderExpr ctx right
            let equal =
                "CASE WHEN " + leftSql + " IS NULL THEN CASE WHEN " + rightSql
                + " IS NULL THEN 1 ELSE 0 END WHEN " + rightSql
                + " IS NULL THEN 0 WHEN " + leftSql + " = " + rightSql
                + " THEN 1 ELSE 0 END"
            "(" + equal
            + (if op = BinaryOperator.NotDistinctFrom then " = 1)" else " = 0)")
        | Expr.Binary(operator, left, right)
            when isScalarLeaf left && isScalarLeaf right ->
            // The scalar leaves have no nested render graph, so the original
            // direct binary path remains cheaper and retains the exact
            // provider/parameter semantics.
            let leftSql = renderExpr ctx left
            let rightSql = renderExpr ctx right
            match operator, ctx.Provider, ctx.TargetRuntime with
            | BinaryOperator.Modulo, (Oracle | Firebird), _ ->
                "MOD(" + leftSql + ", " + rightSql + ")"
            | BinaryOperator.Concat, MySql, _ ->
                "CONCAT(" + leftSql + ", " + rightSql + ")"
            | BinaryOperator.Concat, SqlServer, SqlServerRuntime(Proven NativePipes) ->
                "(" + leftSql + " || " + rightSql + ")"
            | BinaryOperator.Concat, SqlServer, SqlServerRuntime(Proven PlusOperator) ->
                "(" + leftSql + " + " + rightSql + ")"
            | BinaryOperator.Concat, SqlServer, SqlServerRuntime(Unproven message) ->
                invalidOp (
                    "Validated SQL reached rendering without SQL Server concat proof: "
                    + message)
            | BinaryOperator.NotDistinctFrom, MySql, _ ->
                "(" + leftSql + " <=> " + rightSql + ")"
            | BinaryOperator.DistinctFrom, MySql, _ ->
                "NOT (" + leftSql + " <=> " + rightSql + ")"
            | _ ->
                "(" + leftSql + " " + binaryText operator + " " + rightSql + ")"
        | Expr.Binary _ | Expr.Like _ -> renderScalarGraph ctx expression
        | Expr.RawRegexCall _ ->
            invalidOp "Raw REGEXP_LIKE reached rendering before canonicalization."
        | Expr.RegexMatch _ | Expr.PostgresJsonAccess _ ->
            renderScalarGraph ctx expression
        | Expr.FunctionCall call ->
            renderFunction ctx call
        | Expr.FilteredAggregate(value, predicate) ->
            match ctx.Provider with
            | PostgreSql | SQLite | Oracle | Firebird ->
                renderExpr ctx value + " FILTER (WHERE " + renderPredicate ctx predicate + ")"
            | _ -> invalidOp "Aggregate FILTER requires provider-specific lowering before rendering."
        | Expr.Windowed(value, window) -> renderExpr ctx value + " OVER (" + renderWindow ctx window + ")"
        | Expr.Cast _ | Expr.Extract _ -> renderScalarGraph ctx expression
        | Expr.SimpleCase(input, branches, fallback) ->
            let cases = branches |> NonEmpty.toList |> List.map (fun branch -> " WHEN " + renderExpr ctx branch.Match + " THEN " + renderExpr ctx branch.Result) |> joinDocuments ctx ""
            "CASE " + renderExpr ctx input + cases + (fallback |> Option.map (fun value -> " ELSE " + renderExpr ctx value) |> Option.defaultValue (doc ctx "")) + " END"
        | Expr.SearchedCase(branches, fallback) ->
            let cases =
                branches
                |> NonEmpty.toList
                |> List.map (fun branch ->
                    " WHEN " + renderPredicate ctx branch.Condition + " THEN " + renderExpr ctx branch.Result)
                |> joinDocuments ctx ""
            "CASE" + cases + (fallback |> Option.map (fun value -> " ELSE " + renderExpr ctx value) |> Option.defaultValue (doc ctx "")) + " END"
        | Expr.InList _ -> renderScalarGraph ctx expression
        | Expr.InSubquery(value, query, negated) ->
            "(" + renderExpr ctx value
            + (if negated then " NOT IN (" else " IN (")
            + renderSubquery ctx query + "))"
        | Expr.Between _ | Expr.IsNull _ ->
            renderScalarGraph ctx expression
        | Expr.ScalarSubquery query ->
            // Materializing the entire inner SELECT at every level was O(n^2)
            // in SQL-string allocations for a pure nested scalar SELECT chain.
            // Preserve provider-specific dummy FROM and bind the innermost
            // literal exactly once, without changing any expression order.
            let mutable current = query
            let mutable wrappers = 0
            let mutable scanning = true
            while scanning do
                let head = current.Head
                let simple =
                    head.Ctes.IsEmpty
                    && head.DistinctMode = SelectDistinct.AllRows
                    && head.From.IsNone && head.Joins.IsEmpty
                    && head.Where.IsNone && head.GroupBy.IsEmpty
                    && head.Having.IsNone
                    && current.SetOperations.IsEmpty && not (hasTail current)
                if not simple then scanning <- false
                else
                    match head.Projection with
                    | [ { Alias = None; Expression = value } ] ->
                        match Expr.unspan value with
                        | Expr.ScalarSubquery child ->
                            wrappers <- Operators.(+) wrappers 1
                            current <- child
                        | _ -> scanning <- false
                    | _ -> scanning <- false

            if wrappers = 0 then "(" + renderSubquery ctx query + ")"
            else
                let dummySource =
                    match ctx.Provider with
                    | Oracle -> " FROM DUAL"
                    | Firebird -> " FROM RDB$DATABASE"
                    | _ -> ""
                let builder = RenderBuffer(ctx, "")
                builder.Append('(') |> ignore
                for _ in 1 .. wrappers do builder.Append("SELECT (") |> ignore
                builder.Append(renderSubquery ctx current) |> ignore
                for _ in 1 .. wrappers do
                    builder.Append(')').Append(dummySource) |> ignore
                builder.Append(')') |> ignore
                builder.ToDocument()
        | Expr.Exists(query, negated) ->
            renderExistsQueryChain ctx query negated

    // A SELECT-with-EXISTS-WHERE chain forms one SQL output stream, not
    // one completed SQL string per enclosing Query. Materializing every
    // inner SELECT first causes quadratic allocations for mixed Expr/Query
    // nesting even after Binder and Normalizer have become iterative.
    //
    // Restrict this fast path to inert, single-leaf SELECTs so every dialect
    // keeps its exact projection parameter and dummy-FROM order. Any CTE,
    // set branch, paging tail, non-EXISTS predicate, or richer SELECT returns
    // to the original provider-aware renderSubquery path.
    and private renderExistsQueryChain (ctx: RenderContext) query negated =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let dummyFrom =
            match ctx.Provider with
            | Oracle -> " FROM DUAL"
            | Firebird -> " FROM RDB$DATABASE"
            | _ -> ""
        let tryWrapper (current: Query) =
            let head = current.Head
            if not head.Ctes.IsEmpty
               || head.DistinctMode <> SelectDistinct.AllRows
               || not head.Joins.IsEmpty
               || not head.GroupBy.IsEmpty || head.Having.IsSome
               || not current.SetOperations.IsEmpty || hasTail current then
                None
            else
                match head.Projection, head.Where with
                | [ { Alias = None; Expression = projected } ], Some predicate ->
                    match head.From, Expr.unspan projected, Expr.unspan predicate with
                    | (None | Some(TableSource.NamedTable _ | TableSource.CteTable _)),
                      (Literal _ | Column _ | BoundColumn _),
                      Exists(child, isNegated) ->
                        Some(projected, head.From, child, isNegated)
                    | _ -> None
                | _ -> None
        // Ordinary EXISTS has no wrappers: don't allocate a builder or
        // per-query continuation state just to surround one subquery.
        if Option.isNone (tryWrapper query) then
            (if negated then "NOT EXISTS (" else "EXISTS (")
            + renderSubquery ctx query + ")"
        else
            let mutable current = query
            let mutable currentNegated = negated
            let mutable wrapping = true
            let mutable levels = 0
            let output = RenderBuffer(ctx, "")
            while wrapping do
                match tryWrapper current with
                | Some(projected, source, child, innerNegated) ->
                    RewriteStackSafety.compile SqlDiagnosticStage.RenderingInvariant
                    output.Append(if currentNegated then "NOT EXISTS (" else "EXISTS (")
                          .Append("SELECT ")
                          .Append(renderExpr ctx projected) |> ignore
                    match source with
                    | Some named -> output.Append(" FROM ").Append((renderSource ctx named) : SqlDocument) |> ignore
                    | None -> output.Append(dummyFrom) |> ignore
                    output.Append(" WHERE ") |> ignore
                    levels <- Operators.(+) levels 1
                    current <- child
                    currentNegated <- innerNegated
                | None -> wrapping <- false
            output.Append(if currentNegated then "NOT EXISTS (" else "EXISTS (")
                  .Append(renderSubquery ctx current)
                  .Append(')') |> ignore
            for _ in 1 .. levels do output.Append(')') |> ignore
            output.ToDocument()

    and private renderPredicate (ctx: RenderContext) expression : SqlDocument =
        ctx.Resolve((fun () -> renderPredicateCore ctx (Expr.unspan expression)), true)

    and private renderPredicateCore (ctx: RenderContext) expression : SqlDocument =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        match ctx.Provider with
        | Oracle | SqlServer ->
            match expression with
            | Spanned(_, inner) -> renderPredicate ctx inner
            | Literal(ScalarValue.Boolean true) -> doc ctx "(1 = 1)"
            | Literal(ScalarValue.Boolean false) -> doc ctx "(1 = 0)"
            | Unary(UnaryOperator.Not, operand) ->
                "NOT (" + renderPredicate ctx operand + ")"
            | Binary((BinaryOperator.And | BinaryOperator.Or), _, _) as chain ->
                renderBooleanGraph ctx renderPredicate chain
            | SimpleCase(input, branches, fallback) when isBooleanExpression expression ->
                let cases =
                    branches
                    |> NonEmpty.toList
                    |> List.map (fun branch ->
                        " WHEN " + renderExpr ctx branch.Match
                        + " THEN " + renderBooleanTruthValue branch.Result)
                    |> joinDocuments ctx ""
                let otherwise =
                    fallback
                    |> Option.map (fun value -> " ELSE " + renderBooleanTruthValue value)
                    |> Option.defaultValue (doc ctx "")
                "(CASE " + renderExpr ctx input + cases + otherwise + " END = 1)"
            | SearchedCase(branches, fallback) when isBooleanExpression expression ->
                let cases =
                    branches
                    |> NonEmpty.toList
                    |> List.map (fun branch ->
                        " WHEN " + renderPredicate ctx branch.Condition
                        + " THEN " + renderBooleanTruthValue branch.Result)
                    |> joinDocuments ctx ""
                let otherwise =
                    fallback
                    |> Option.map (fun value -> " ELSE " + renderBooleanTruthValue value)
                    |> Option.defaultValue (doc ctx "")
                "(CASE" + cases + otherwise + " END = 1)"
            | _ -> renderExpr ctx expression
        | PostgreSql | MySql | SQLite | Firebird ->
            renderExpr ctx expression

    // A Boolean subtree can be left-deep, right-deep or balanced. Each
    // pending binary uses ONE struct frame: after rendering its left child,
    // append its operator; after rendering its right child, close exactly
    // that parent's parentheses. No node reordering, Boolean reassociation,
    // per-node option allocations or managed recursion is needed.
    and private renderBooleanGraph
        (ctx: RenderContext)
        (renderLeaf: RenderContext -> Expr -> SqlDocument)
        expression =
        let pending = ResizeArray<struct (BinaryOperator * Expr * bool)>()
        let builder = RenderBuffer(ctx, "")
        let mutable current = expression
        let mutable visiting = true
        while visiting || pending.Count > 0 do
            if visiting then
                match Expr.unspan current with
                | Binary((BinaryOperator.And | BinaryOperator.Or as op), left, right) ->
                    RewriteStackSafety.compile SqlDiagnosticStage.RenderingInvariant
                    builder.Append('(') |> ignore
                    pending.Add(struct (op, right, false))
                    current <- left
                | _ ->
                    builder.Append(renderLeaf ctx current) |> ignore
                    visiting <- false
            else
                let last = pending.Count - 1
                let struct (op, right, leftFinished) = pending.[last]
                if leftFinished then
                    pending.RemoveAt last
                    builder.Append(')') |> ignore
                else
                    pending.[last] <- struct (op, right, true)
                    builder.Append(' ').Append(binaryText op).Append(' ') |> ignore
                    current <- right
                    visiting <- true
        builder.ToDocument()

    // Preserve exact nested parentheses and left-to-right parameter
    // binding while avoiding depth-proportional recursion and intermediate
    // SQL prefixes for left-deep arithmetic trees. Complex operators (e.g.
    // MOD, CONCAT, IS DISTINCT FROM) continue through their dialect-aware
    // renderer instead of being incorrectly treated as infix arithmetic.
    // Emit the compositional scalar grammar into ONE StringBuilder. The
    // old renderExpr recursively entered itself through every unary,
    // right-nested binary, CAST, LIKE, BETWEEN and IN-list node. Arithmetic
    // is not flattened or reassociated: parentheses are emitted exactly
    // as for the recursive implementation, including dialect-specific
    // MOD/CONCAT/NULL-safe forms and left-to-right parameter binding.
    and private renderScalarGraph (ctx: RenderContext) root =
        let pending = ResizeArray<ScalarRenderFrame>()
        let output = RenderBuffer(ctx, "")
        let inline node value = pending.Add(ScalarNode value)
        let inline text (value: string) = pending.Add(ScalarText value)
        let append (value: string) = output.Append(value) |> ignore
        let wrap prefix suffix child =
            append prefix
            text suffix
            node child
        let binary prefix separator suffix left right =
            append prefix
            text suffix
            node right
            text separator
            node left
        node root
        while pending.Count > 0 do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt last
            match frame with
            | ScalarText fragment -> append fragment
            | ScalarRegexCheck ->
                match ctx.Provider with
                | SQLite | Firebird ->
                    invalidOp (capabilityError ctx.Provider "function.regex_match")
                | _ -> ()
            | ScalarConcatProof message ->
                invalidOp (
                    "Validated SQL reached rendering without SQL Server concat proof: "
                    + message)
            | ScalarCastComplete original ->
                match Expr.unspan original with
                | Expr.Cast(_, targetType) ->
                    append (
                        " AS "
                        + RewriteCastTypes.renderTarget
                            (providerTool ctx.Provider) targetType
                        + ")")
                | _ -> invalidOp "Scalar CAST completion frame lost its source AST."
            | ScalarNode original ->
                RewriteStackSafety.compile SqlDiagnosticStage.RenderingInvariant
                match Expr.unspan original with
                | Expr.Unary(UnaryOperator.Not, operand) ->
                    wrap "NOT (" ")" operand
                | Expr.Unary(UnaryOperator.Negate, operand) ->
                    wrap "(-" ")" operand
                | Expr.Unary(UnaryOperator.Positive, operand) ->
                    wrap "(+" ")" operand
                | Expr.Binary((BinaryOperator.DistinctFrom
                              | BinaryOperator.NotDistinctFrom), _, _)
                    when ctx.Provider = Oracle ->
                    // Oracle expands operands more than once; renderExpr
                    // caches them in the special-case branch above.
                    output.Append(renderExpr ctx original) |> ignore
                | Expr.Binary(op, left, right) ->
                    match op, ctx.Provider, ctx.TargetRuntime with
                    | BinaryOperator.Modulo, (Oracle | Firebird), _ ->
                        binary "MOD(" ", " ")" left right
                    | BinaryOperator.Concat, MySql, _ ->
                        binary "CONCAT(" ", " ")" left right
                    | BinaryOperator.Concat, SqlServer,
                        SqlServerRuntime(Proven NativePipes) ->
                        binary "(" " || " ")" left right
                    | BinaryOperator.Concat, SqlServer,
                        SqlServerRuntime(Proven PlusOperator) ->
                        binary "(" " + " ")" left right
                    | BinaryOperator.Concat, SqlServer,
                        SqlServerRuntime(Unproven message) ->
                        // A nonportable concat proof originally fails AFTER
                        // its left and right operands have been rendered.
                        pending.Add(ScalarConcatProof message)
                        binary "(" " + " ")" left right
                    | BinaryOperator.NotDistinctFrom, MySql, _ ->
                        binary "(" " <=> " ")" left right
                    | BinaryOperator.DistinctFrom, MySql, _ ->
                        binary "NOT (" " <=> " ")" left right
                    | _ ->
                        binary "(" (" " + binaryText op + " ") ")" left right
                | Expr.IsNull(value, negated) ->
                    wrap "(" (if negated then " IS NOT NULL)" else " IS NULL)") value
                | Expr.Between(value, lower, upper, negated) ->
                    append "("
                    text ")"
                    node upper
                    text " AND "
                    node lower
                    text (if negated then " NOT BETWEEN " else " BETWEEN ")
                    node value
                | Expr.InList(value, items, negated) ->
                    append "("
                    text "))"
                    let entries = NonEmpty.toList items |> List.toArray
                    for index = entries.Length - 1 downto 0 do
                        node entries.[index]
                        if index > 0 then text ", "
                    text (if negated then " NOT IN (" else " IN (")
                    node value
                | Expr.Like(value, pattern, escape, negated, insensitive) ->
                    if insensitive && ctx.Provider <> PostgreSql then
                        invalidOp (capabilityError ctx.Provider "operator.ilike")
                    append (if negated then "NOT ((" else "(")
                    text (if negated then "))" else ")")
                    text (escape
                          |> Option.map (fun value ->
                              " ESCAPE " + renderLikeEscape ctx.Provider value)
                          |> Option.defaultValue "")
                    node pattern
                    text (if insensitive then " ILIKE " else " LIKE ")
                    node value
                | Expr.RegexMatch(value, pattern) ->
                    match ctx.Provider with
                    | PostgreSql ->
                        binary "(" " ~ " ")" value pattern
                    | MySql | Oracle | SqlServer ->
                        binary "REGEXP_LIKE(" ", " ")" value pattern
                    | SQLite | Firebird ->
                        // Historically target checks happen after both
                        // operand subtrees are rendered.
                        pending.Add ScalarRegexCheck
                        binary "REGEXP_LIKE(" ", " ")" value pattern
                | Expr.PostgresJsonAccess(value, selector, resultKind) ->
                    if ctx.Provider <> PostgreSql then
                        invalidOp (capabilityError ctx.Provider "json.operator.postgres_arrow")
                    // The legacy renderer binds the selector first, then
                    // visits the value expression. Retain that exact order.
                    let selectorSql =
                        match selector with
                        | PostgresJsonProperty key -> ctx.Bind(box key)
                        | PostgresJsonArrayIndex index -> ctx.Bind(box index)
                    let operator =
                        match resultKind with
                        | JsonResult -> "->"
                        | TextResult -> "->>"
                    wrap "(" (" " + operator + " " + selectorSql + ")") value
                | Expr.Cast(value, _) ->
                    // CAST target validation/rendering runs only after its
                    // operand, retaining the previous diagnostic precedence.
                    append "CAST("
                    pending.Add(ScalarCastComplete original)
                    node value
                | Expr.Extract(field, value) ->
                    let part =
                        ExtractField.value field
                        |> fun value -> value.Trim().ToUpperInvariant()
                    match SqlDatePartCapabilityRules.TargetValidationError(
                              part, providerTool ctx.Provider) with
                    | null -> ()
                    | message -> raise (SqlCompilationException(message))
                    match ctx.Provider with
                    | SqlServer ->
                        wrap ("DATEPART(" + part + ", ") ")" value
                    | MySql ->
                        wrap (part + "(") ")" value
                    | PostgreSql | Oracle ->
                        wrap ("EXTRACT(" + part + " FROM ") ")" value
                    | Firebird ->
                        wrap ("EXTRACT(" + part + " FROM CAST(") " AS DATE))" value
                    | SQLite ->
                        let fmt =
                            match part with
                            | "YEAR" -> "%Y" | "MONTH" -> "%m"
                            | "DAY" -> "%d" | "HOUR" -> "%H"
                            | "MINUTE" -> "%M" | "SECOND" -> "%S"
                            | "QUARTER" -> "%m"
                            | _ ->
                                raise (SqlCompilationException(
                                    "SQLite does not support date part " + part + "."))
                        let prefix =
                            if part = "QUARTER" then
                                "CAST(((CAST(STRFTIME('" + fmt + "', "
                            else "CAST(STRFTIME('" + fmt + "', "
                        let suffix =
                            if part = "QUARTER" then
                                ") AS INTEGER) + 2) / 3) AS INTEGER)"
                            else ") AS INTEGER)"
                        wrap prefix suffix value
                | _ ->
                    // Functions, CASE, windows and Query bodies still have
                    // their specialized semantic/parameter pathways. Only
                    // non-compositional leaves are delegated.
                    output.Append(renderExpr ctx original) |> ignore
        output.ToDocument()

    and private renderFunction (ctx: RenderContext) (call: FunctionCall) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let nativeName = renderFunctionName ctx call.Name
        let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
        let dispatchName =
            if FunctionName.hasQuotedParts call.Name then String.Empty
            else name
        let tool = providerTool ctx.Provider

        let fail message = raise (SqlCompilationException(messageText ctx message))
        let requireCount count =
            if call.Arguments.Length <> count then
                fail ("Canonical function '" + name + "' requires " + string count + " argument(s).")

        let literalText label expression =
            match Expr.unspan expression with
            | Literal(ScalarValue.Text value) -> value
            | _ -> fail (label + " must be a string literal.")

        let literalKeyword label expression =
            let value = literalText label expression |> fun value -> value.Trim().ToUpperInvariant()
            if not (Regex.IsMatch(value, "^[A-Z_]+$", RegexOptions.CultureInvariant)) then
                fail ("Unsafe " + label + " '" + value + "'.")
            value

        let sqlStringLiteral expression label =
            let value = literalText label expression
            quoteSqlLiteral ctx value

        let renderOrdinary () =
            if dispatchName.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase) then
                fail ("Canonical function '" + name + "' has no native lowering implementation; compilation was rejected.")
            if not call.AggregateOrderBy.IsEmpty || call.AggregateSeparator.IsSome then
                fail ("Aggregate-local modifiers are not supported for ordinary function '" + name + "'.")
            let rendered =
                call.Arguments
                |> List.mapi (fun index argument ->
                    let sql = renderExpr ctx argument
                    if ctx.Provider = PostgreSql && dispatchName = "ROUND" && call.Arguments.Length = 2 && index = 0 then
                        "CAST(" + sql + " AS numeric)"
                    else sql)
            let args = joinDocuments ctx ", " rendered
            nativeName + "(" + (if call.IsDistinct then "DISTINCT " else "") + args + ")"

        match dispatchName with
        | "CORE_DATE_ADD" ->
            requireCount 3
            let unit =
                literalKeyword "DATEADD unit" call.Arguments[0]
                |> fun value -> SqlDateMathUnit.Parse(value, "DATEADD")
            renderExpr ctx (DateAdd(unit, call.Arguments[1], call.Arguments[2]))

        | "CORE_DATE_DIFF" ->
            requireCount 3
            let unit =
                literalKeyword "DATEDIFF unit" call.Arguments[0]
                |> fun value -> SqlDateMathUnit.Parse(value, "DATEDIFF")
            renderExpr ctx (DateDiff(unit, call.Arguments[1], call.Arguments[2]))

        | "CORE_DATE_PART" ->
            requireCount 2
            let part = literalKeyword "date part" call.Arguments[0]
            renderExpr ctx (Extract(ExtractField.create part, call.Arguments[1]))

        | "CORE_DATE_FORMAT" ->
            requireCount 2
            match SqlTemporalFormatCapabilityRules.TargetValidationError("CORE_DATE_FORMAT", tool) with
            | null -> ()
            | message -> fail message
            let value = renderExpr ctx call.Arguments[0]
            let formatValue = literalText "date format" call.Arguments[1]
            let format = ctx.BindShared(String.Concat("date-format:", formatValue), box formatValue)
            match ctx.Provider with
            | SqlServer -> "FORMAT(" + value + ", " + format + ")"
            | PostgreSql
            | Oracle -> "TO_CHAR(" + value + ", " + format + ")"
            | MySql -> "DATE_FORMAT(" + value + ", " + format + ")"
            | SQLite -> "STRFTIME(" + format + ", " + value + ")"
            | Firebird -> fail "portable date formatting is not supported by Firebird."

        | "CORE_DATE_ONLY" ->
            requireCount 1
            match SqlDateOnlyCapabilityRules.TargetValidationError(tool) with
            | null -> ()
            | message -> fail message
            "DATE(" + renderExpr ctx call.Arguments[0] + ")"

        | "CORE_DATE_PARSE" ->
            requireCount 2
            match SqlTemporalFormatCapabilityRules.TargetValidationError("CORE_DATE_PARSE", tool) with
            | null -> ()
            | message -> fail message
            let value = renderExpr ctx call.Arguments[0]
            let formatValue = literalText "date parse format" call.Arguments[1]
            let format = ctx.BindShared(String.Concat("date-parse-format:", formatValue), box formatValue)
            match ctx.Provider with
            | MySql -> "DATE(STR_TO_DATE(" + value + ", " + format + "))"
            | PostgreSql
            | Oracle -> "TO_DATE(" + value + ", " + format + ")"
            | _ -> fail "formatted date parsing is not supported by this provider."

        | "CORE_POSITION" ->
            requireCount 2
            let haystack = renderExpr ctx call.Arguments[0]
            let needle = renderExpr ctx call.Arguments[1]
            match ctx.Provider with
            | SqlServer -> "CHARINDEX(" + needle + ", " + haystack + ")"
            | PostgreSql -> "STRPOS(" + haystack + ", " + needle + ")"
            | MySql -> "LOCATE(" + needle + ", " + haystack + ")"
            | SQLite
            | Oracle -> "INSTR(" + haystack + ", " + needle + ")"
            | Firebird -> "POSITION(" + needle + ", " + haystack + ")"

        | "CORE_JSON_EXTRACT"
        | "CORE_JSON_SET" as canonical ->
            let expected = if canonical = "CORE_JSON_EXTRACT" then 2 else 3
            requireCount expected
            match SqlJsonCapabilityRules.TargetValidationError(canonical, tool) with
            | null -> ()
            | message -> fail message
            let path = literalText canonical call.Arguments[1]
            let parsedPath =
                match JsonPath.tryParse path with
                | Ok value -> value
                | Error _ ->
                    fail (
                        "JSON path '" + path + "' is outside the portable Core property/array-index subset. "
                        + "SQL capability 'json.path.property_chain' is not supported by provider "
                        + string tool + " for this Core plan.")
            if canonical = "CORE_JSON_SET" && JsonPath.hasArrayIndex parsedPath then
                fail (capabilityError ctx.Provider "json.path.mutation_array_index")
            let value = renderExpr ctx call.Arguments[0]
            let pathExpr () = renderExpr ctx call.Arguments[1]
            if canonical = "CORE_JSON_EXTRACT" then
                match ctx.Provider with
                | MySql
                | SQLite ->
                    "JSON_EXTRACT(" + value + ", " + pathExpr () + ")"
                | PostgreSql ->
                    let placeholders =
                        JsonPath.postgresSegments parsedPath
                        |> List.map (fun segment -> doc ctx (ctx.Bind(box segment)))
                        |> joinDocuments ctx ", "
                    "JSONB_EXTRACT_PATH(CAST(" + value + " AS jsonb), " + placeholders + ")"
                | _ -> fail "JSON_EXTRACT is not supported losslessly by this provider."
            else
                let newValue =
                    match ctx.Provider with
                    | PostgreSql ->
                        let pgPathPlaceholder = ctx.Bind(box (JsonPath.postgresTextArray parsedPath))
                        let rendered = renderExpr ctx call.Arguments[2]
                        "JSONB_SET(CAST(" + value + " AS jsonb), CAST(" + pgPathPlaceholder + " AS text[]), TO_JSONB(" + rendered + "))"
                    | MySql
                    | SQLite ->
                        let pathSql = pathExpr ()
                        let rendered = renderExpr ctx call.Arguments[2]
                        "JSON_SET(" + value + ", " + pathSql + ", " + rendered + ")"
                    | SqlServer ->
                        let pathSql = pathExpr ()
                        let rendered = renderExpr ctx call.Arguments[2]
                        "JSON_MODIFY(" + value + ", " + pathSql + ", " + rendered + ")"
                    | _ -> fail "JSON_SET is not supported by this provider."
                newValue

        | "CORE_CURRENT_DATE" ->
            requireCount 0
            if call.IsDistinct then fail "Canonical current temporal function 'CORE_CURRENT_DATE' cannot be DISTINCT."
            doc ctx (if ctx.Provider = SqlServer then "CAST(CURRENT_TIMESTAMP AS date)" else "CURRENT_DATE")

        | "CORE_CURRENT_TIME" ->
            requireCount 0
            if call.IsDistinct then fail "Canonical current temporal function 'CORE_CURRENT_TIME' cannot be DISTINCT."
            if ctx.Provider = Oracle then fail "CURRENT_TIME is not supported by Oracle."
            elif ctx.Provider = SqlServer then doc ctx "CAST(CURRENT_TIMESTAMP AS time)"
            else doc ctx "CURRENT_TIME"

        | "CORE_CURRENT_TIMESTAMP" ->
            requireCount 0
            if call.IsDistinct then fail "Canonical current temporal function 'CORE_CURRENT_TIMESTAMP' cannot be DISTINCT."
            doc ctx "CURRENT_TIMESTAMP"

        | "CORE_ORACLE_SYSDATE" ->
            requireCount 0
            if call.IsDistinct then fail "Oracle SYSDATE cannot be DISTINCT."
            if ctx.Provider <> Oracle then
                fail ("SQL capability 'function.oracle_sysdate' is native-only and cannot render for provider " + string (providerTool ctx.Provider) + ".")
            doc ctx "SYSDATE"

        | "CORE_STRING_AGG" ->
            requireCount 2
            if call.IsDistinct then fail "Canonical CORE_STRING_AGG DISTINCT semantics are not enabled."
            let value = renderExpr ctx call.Arguments[0]
            let separator =
                if ctx.Provider = PostgreSql || ctx.Provider = SqlServer then
                    doc ctx (ctx.Bind(box (literalText "string aggregate separator" call.Arguments[1])))
                else
                    sqlStringLiteral call.Arguments[1] "string aggregate separator"
            let ordering =
                if call.AggregateOrderBy.IsEmpty then None
                else
                    call.AggregateOrderBy
                    |> List.collect (renderOrderBy ctx false)
                    |> joinDocuments ctx ", "
                    |> fun sql -> Some("ORDER BY " + sql)
            match ordering, ctx.Provider with
            | Some order, PostgreSql -> "STRING_AGG(" + value + ", " + separator + " " + order + ")"
            | Some order, SQLite -> "GROUP_CONCAT(" + value + ", " + separator + " " + order + ")"
            | Some order, SqlServer -> "STRING_AGG(" + value + ", " + separator + ") WITHIN GROUP (" + order + ")"
            | Some order, Oracle -> "LISTAGG(" + value + ", " + separator + ") WITHIN GROUP (" + order + ")"
            | Some order, MySql -> "GROUP_CONCAT(" + value + " " + order + " SEPARATOR " + separator + ")"
            | Some _, Firebird -> fail "Aggregate-local ORDER BY lowering is not supported by Firebird."
            | None, SqlServer
            | None, PostgreSql -> "STRING_AGG(" + value + ", " + separator + ")"
            | None, MySql -> "GROUP_CONCAT(" + value + " SEPARATOR " + separator + ")"
            | None, SQLite -> "GROUP_CONCAT(" + value + ", " + separator + ")"
            | None, Oracle -> "LISTAGG(" + value + ", " + separator + ")"
            | None, Firebird -> "LIST(" + value + ", " + separator + ")"

        | _ -> renderOrdinary ()

    and private renderWindow (ctx: RenderContext) window =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let parts = ResizeArray<SqlDocument>()
        if not window.PartitionBy.IsEmpty then parts.Add("PARTITION BY " + (window.PartitionBy |> List.map (renderExpr ctx) |> joinDocuments ctx ", "))
        if not window.OrderBy.IsEmpty then parts.Add("ORDER BY " + (window.OrderBy |> List.map (renderOrderBy ctx false) |> List.collect id |> joinDocuments ctx ", "))
        window.Frame |> Option.iter (fun frame ->
            let unitText = match frame.Unit with WindowFrameUnit.Rows -> "ROWS" | WindowFrameUnit.Range -> "RANGE"
            let frameSql = match frame.Extent with SingleBound start -> unitText + " " + frameBoundText start | BetweenBounds(start, finish) -> unitText + " BETWEEN " + frameBoundText start + " AND " + frameBoundText finish
            parts.Add(frameSql))
        joinDocuments ctx " " parts

    and private renderOrderBy (ctx: RenderContext) setTail (order: OrderBy) : SqlDocument list =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let expression = renderExpr ctx order.Expression
        let direction = if order.Descending then " DESC" else " ASC"
        match order.NullOrdering, ctx.Provider with
        | NullOrdering.Default, _ -> [ expression + direction ]
        | NullOrdering.NullsFirst, (PostgreSql | SQLite | Oracle | Firebird) -> [ expression + direction + " NULLS FIRST" ]
        | NullOrdering.NullsLast, (PostgreSql | SQLite | Oracle | Firebird) -> [ expression + direction + " NULLS LAST" ]
        | explicitNulls, (MySql | SqlServer) ->
            if setTail then invalidOp (capabilityError ctx.Provider "ordering.nulls")
            let targetDefault = (not order.Descending && explicitNulls = NullOrdering.NullsFirst) || (order.Descending && explicitNulls = NullOrdering.NullsLast)
            if targetDefault then [ expression + direction ]
            else
                match Expr.unspan order.Expression with
                | BoundColumn(_, LocalRowSource)
                | BoundColumn(_, OuterRowSource) ->
                    let nullRank, nonNullRank = if explicitNulls = NullOrdering.NullsLast then 1, 0 else 0, 1
                    let nullRankSql = ctx.Bind(box nullRank)
                    let nonNullRankSql = ctx.Bind(box nonNullRank)
                    [ "CASE WHEN (" + expression + " IS NULL) THEN " + nullRankSql + " ELSE " + nonNullRankSql + " END ASC"; expression + direction ]
                | _ -> invalidOp (capabilityError ctx.Provider "ordering.nulls")

    and private renderCtes (ctx: RenderContext) (ctes: Cte list) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        if List.isEmpty ctes then doc ctx ""
        else
            let recursiveScope = ctes |> List.exists (fun cte -> cte.RecursiveScope)
            if recursiveScope then
                let provider = providerTool ctx.Provider
                if not (SqlRecursiveCteCapabilityRules.SupportsWithRecursiveSyntax(provider)) then
                    raise (SqlCompilationException(String.Concat(
                        "SQL capability 'select.recursive_cte' is not supported by target provider ",
                        string provider, "; this provider does not use the modeled WITH RECURSIVE syntax contract.")))
            let result = RenderBuffer(ctx, "WITH ")
            if recursiveScope then result.Append("RECURSIVE ") |> ignore

            // CTE output names are part of its schema, not optional cosmetic
            // aliases. Dropping them silently changes column identity for
            // SELECT, INSERT, UPDATE and DELETE consumers across providers.
            let appendCteHeader (cte: Cte) =
                result.Append(renderAlias ctx cte.Name) |> ignore
                if not cte.ColumnAliases.IsEmpty then
                    result.Append('(') |> ignore
                    cte.ColumnAliases
                    |> List.iteri (fun index name ->
                        if index > 0 then result.Append(", ") |> ignore
                        result.Append(renderAlias ctx name) |> ignore)
                    result.Append(')') |> ignore
                result.Append(" AS (") |> ignore

            ctes
            |> List.iteri (fun index cte ->
                if index > 0 then result.Append(", ") |> ignore
                appendCteHeader cte

                // Streaming one-CTE nesting is crucial: each nested CTE body
                // used to render to a complete string before its parent,
                // allocating O(depth^2) SQL text. Keep Query tails, SET,
                // recursive scopes and multi-CTE bodies on the full path.
                let nested = ResizeArray<Query>()
                let mutable current = cte.Query
                let mutable walking = true
                while walking do
                    RewriteResourceBudget.checkpoint ()
                    match current.Head.Ctes with
                    | [ innerCte ] when not innerCte.RecursiveScope
                                        && current.SetOperations.IsEmpty
                                        && not (hasTail current) ->
                        result.Append("WITH ") |> ignore
                        appendCteHeader innerCte
                        nested.Add(current)
                        current <- innerCte.Query
                    | _ -> walking <- false

                result.Append((renderCteQuery ctx current) : SqlDocument) |> ignore
                for nesting = nested.Count - 1 downto 0 do
                    result.Append(") ") |> ignore
                    let query = nested.[nesting]
                    result.Append(
                        (renderSelectBody ctx { query.Head with Ctes = [] }) : SqlDocument)
                    |> ignore
                result.Append(')') |> ignore)
            result.Append(' ').ToDocument()

    and private renderSource (ctx: RenderContext) source : SqlDocument =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        match source with
        | TableSource.NamedTable(identifier, alias) | TableSource.CteTable(identifier, alias) ->
            renderIdentifier ctx identifier + (alias |> Option.map (fun value -> tableAliasPrefix ctx.Provider + renderAlias ctx value) |> Option.defaultValue (doc ctx ""))
        | TableSource.DerivedTable(query, alias) ->
            renderDerivedSourceChain ctx query alias false
        | TableSource.LateralDerivedTable(query, alias) ->
            match SqlLateralDerivedTableCapabilityRules.SyntaxEmissionValidationError(providerTool ctx.Provider) with
            | null -> renderDerivedSourceChain ctx query alias true
            | message -> raise (SqlCompilationException(message))

    // Outside-in emission preserves projection binding and provider alias
    // syntax without recursively allocating a string at every nested Query.
    and private renderDerivedSourceChain (ctx: RenderContext) query alias lateral =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let tryWrapper (current: Query) =
            let head = current.Head
            // TOP/FIRST/SKIP need prefix rewriting; PostgreSQL, MySQL,
            // SQLite and Oracle tails are suffixes and can stream safely.
            let tailIsSuffix =
                not (hasTail current)
                || (match ctx.Provider with
                    | PostgreSql | MySql | SQLite | Oracle -> true
                    | SqlServer | Firebird -> false)
            if not head.Ctes.IsEmpty
               || head.DistinctMode <> SelectDistinct.AllRows
               || not head.Joins.IsEmpty
               || not head.GroupBy.IsEmpty || head.Having.IsSome
               || not current.SetOperations.IsEmpty || not tailIsSuffix then
                None
            else
                match head.From with
                | Some (TableSource.DerivedTable(inner, innerAlias)) ->
                    Some(head.Projection, inner, innerAlias)
                | _ -> None
        match tryWrapper query with
        | None ->
            (if lateral then "LATERAL (" else "(")
            + renderQuery ctx query + ")"
            + tableAliasPrefix ctx.Provider + renderAlias ctx alias
        | Some _ ->
            let result = RenderBuffer(ctx, if lateral then "LATERAL (" else "(")
            let wrappers = ResizeArray<struct (IdentifierPart * Query)>()
            let mutable current = query
            let mutable scanning = true
            while scanning do
                RewriteResourceBudget.checkpoint ()
                match tryWrapper current with
                | None -> scanning <- false
                | Some(projection, inner, childAlias) ->
                    result.Append("SELECT ") |> ignore
                    projection
                    |> List.iteri (fun index item ->
                        if index > 0 then result.Append(", ") |> ignore
                        result.Append(renderExpr ctx item.Expression) |> ignore
                        item.Alias
                        |> Option.iter (fun itemAlias ->
                            result.Append(" AS ").Append(renderAlias ctx itemAlias) |> ignore))
                    result.Append(" FROM (") |> ignore
                    wrappers.Add(struct (childAlias, current))
                    current <- inner
            result.Append(renderQuery ctx current) |> ignore
            for index = wrappers.Count - 1 downto 0 do
                let struct (childAlias, wrapper) = wrappers.[index]
                result.Append(')')
                      .Append(tableAliasPrefix ctx.Provider)
                      .Append(renderAlias ctx childAlias) |> ignore
                wrapper.Head.Where
                |> Option.iter (fun predicate ->
                    result.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
                result.Append((renderOrderClause ctx false wrapper.OrderBy) : SqlDocument) |> ignore
                if hasTail wrapper then
                    // Only providers with a suffix-only paging contract
                    // can reach here; passing "" avoids copying a prefix.
                    result.Append((renderPaging ctx wrapper (doc ctx "")) : SqlDocument) |> ignore
            result.Append(')')
                  .Append(tableAliasPrefix ctx.Provider)
                  .Append(renderAlias ctx alias)
                  .ToDocument()

    and private renderSelectBody (ctx: RenderContext) (select: Select) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let groupScope expression =
            if ctx.Provider <> PostgreSql then None
            else
                select.GroupBy
                |> List.tryFindIndex (fun grouped -> Expr.equivalent expression grouped)
                |> Option.map (fun index -> String.Concat("postgres-group:", string index))

        let renderProjectionExpression expression =
            match groupScope expression with
            | Some scope -> ctx.WithSharedBindings(scope, fun () -> renderExpr ctx expression)
            | None -> renderExpr ctx expression

        let projection =
            select.Projection
            |> List.map (fun item ->
                renderProjectionExpression item.Expression
                + (item.Alias
                   |> Option.map (fun alias -> " AS " + renderAlias ctx alias)
                   |> Option.defaultValue (doc ctx "")))
            |> joinDocuments ctx ", "

        let distinctSql =
            match select.DistinctMode with
            | SelectDistinct.AllRows -> doc ctx ""
            | SelectDistinct.DistinctRows -> doc ctx "DISTINCT "
            | SelectDistinct.DistinctOn expressions ->
                if ctx.Provider <> PostgreSql then
                    raise (SqlCompilationException(
                        "SQL capability 'select.distinct_on' is not supported by this target provider."))
                "DISTINCT ON ("
                + (expressions
                   |> NonEmpty.toList
                   |> List.map (renderExpr ctx)
                   |> joinDocuments ctx ", ")
                + ") "
        // Projection and DISTINCT ON expressions bind before FROM/JOIN
        // exactly as before. Build the text once instead of copying the
        // complete SELECT prefix for every JOIN, WHERE, GROUP and HAVING.
        let sql = RenderBuffer(ctx, "SELECT ")
        sql.Append(distinctSql).Append(projection) |> ignore
        select.From
        |> Option.iter (fun source ->
            sql.Append(" FROM ").Append(renderSource ctx source) |> ignore)
        if select.From.IsNone then
            match ctx.Provider with
            | Oracle -> sql.Append(" FROM DUAL") |> ignore
            | Firebird -> sql.Append(" FROM RDB$DATABASE") |> ignore
            | _ -> ()
        for join in select.Joins do
            let joinSql =
                match join with
                | ApplyJoin(ApplyKind.CrossApply, _) -> "CROSS APPLY"
                | ApplyJoin(ApplyKind.OuterApply, _) -> "OUTER APPLY"
                | NaturalJoin(kind, _) ->
                    match SqlNaturalJoinCapabilityRules.TargetValidationError(providerTool ctx.Provider) with
                    | null -> naturalJoinText kind
                    | message -> raise (SqlCompilationException(message))
                | _ -> joinText join.Kind
            sql.Append(" ").Append(joinSql).Append(" ")
               .Append(renderSource ctx join.Source) |> ignore
            match join with
            | CrossJoin _
            | ApplyJoin _
            | NaturalJoin _ -> ()
            | OnJoin(_, _, predicate) ->
                sql.Append(" ON ").Append(renderPredicate ctx predicate) |> ignore
            | UsingJoin(_, _, columns) ->
                match SqlUsingJoinCapabilityRules.TargetValidationError(providerTool ctx.Provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
                let names =
                    columns
                    |> NonEmpty.toList
                    |> List.map (renderAlias ctx)
                    |> joinDocuments ctx ", "
                sql.Append(" USING (").Append(names).Append(")") |> ignore
        select.Where
        |> Option.iter (fun predicate ->
            sql.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
        if not select.GroupBy.IsEmpty then
            let grouped =
                select.GroupBy
                |> List.mapi (fun index expression ->
                    if ctx.Provider = PostgreSql then
                        ctx.WithSharedBindings(
                            String.Concat("postgres-group:", string index),
                            fun () -> renderExpr ctx expression)
                    else
                        renderExpr ctx expression)
                |> joinDocuments ctx ", "
            sql.Append(" GROUP BY ").Append(grouped) |> ignore
        select.Having
        |> Option.iter (fun predicate ->
            sql.Append(" HAVING ").Append(renderPredicate ctx predicate) |> ignore)
        sql.ToDocument()

    // Stream SET arms into an existing bounded buffer. Consumers with
    // outer paging wrappers need not materialize and then concatenate an
    // entire intermediate UNION body before enforcing the output budget.
    and private appendSetBody (ctx: RenderContext) (sql: RenderBuffer) (query: Query) =
        let pending = ResizeArray<SetEmitFrame>()
        pending.Add(EnterSetBody query)
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            match frame with
            | EnterSetBody current ->
                sql.Append(renderSelectBody ctx { current.Head with Ctes = [] }) |> ignore
                for branch in List.rev current.SetOperations do
                    pending.Add(EmitSetBranch branch)
            | EmitSetBranch branch ->
                let branchNoTail =
                    { branch.Query with
                        OrderBy = []
                        Limit = None
                        Offset = None
                        FetchPercent = None
                        FetchWithTies = false }
                sql.Append(" ").Append(setText ctx.Provider branch.Operator).Append(" ") |> ignore
                if (ctx.Provider = PostgreSql || ctx.Provider = Oracle)
                   && not branchNoTail.SetOperations.IsEmpty
                   && branchNoTail.Head.Ctes.IsEmpty then
                    sql.Append('(') |> ignore
                    pending.Add(CloseNestedSet)
                    pending.Add(EnterSetBody branchNoTail)
                elif (ctx.Provider = PostgreSql || ctx.Provider = Oracle)
                     && not branchNoTail.SetOperations.IsEmpty then
                    // Preserve the full WITH/SET contract for the uncommon
                    // CTE-bearing branch; do not lower away capability proof.
                    sql.Append("(").Append((renderQueryCore ctx branchNoTail) : SqlDocument)
                       .Append(')') |> ignore
                elif branchNoTail.Head.Ctes.IsEmpty then
                    sql.Append((renderQueryCore ctx branchNoTail) : SqlDocument) |> ignore
                else
                    sql.Append("SELECT * FROM (")
                       .Append((renderQueryCore ctx branchNoTail) : SqlDocument)
                       .Append(") AS ")
                       .Append(renderAlias ctx
                           { Value = "_set_branch"
                             WasQuoted = false
                             PreserveSpelling = false
                             Span = { Start = 0; Length = 0 } }) |> ignore
            | CloseNestedSet -> sql.Append(')') |> ignore

    and private renderSetBody (ctx: RenderContext) (query: Query) =
        let sql = RenderBuffer(ctx, "")
        appendSetBody ctx sql query
        sql.ToDocument()

    and private renderOrderClause (ctx: RenderContext) setTail orderBy =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        if List.isEmpty orderBy then doc ctx ""
        else " ORDER BY " + (orderBy |> List.collect (renderOrderBy ctx setTail) |> joinDocuments ctx ", ")

    and private renderPaging (ctx: RenderContext) (query: Query) (sql: SqlDocument) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let intValue value = NonNegativeRowCount.value value
        let percentValue value = NonNegativePercentage.value value
        if query.FetchPercent.IsSome && ctx.Provider <> Oracle then
            invalidOp ("FETCH PERCENT reached rendering for unsupported provider " + string (providerTool ctx.Provider) + ".")
        match ctx.Provider with
        | PostgreSql when query.FetchWithTies ->
            let withOffset =
                query.Offset
                |> Option.map (fun value -> sql + " OFFSET " + ctx.Bind(box (intValue value)) + " ROWS")
                |> Option.defaultValue sql
            match query.Limit with
            | Some limit ->
                withOffset + " FETCH FIRST " + ctx.Bind(box (intValue limit)) + " ROWS WITH TIES"
            | None ->
                invalidOp "FETCH WITH TIES reached PostgreSQL rendering without a row-count limit."
        | PostgreSql ->
            let withLimit = query.Limit |> Option.map (fun value -> sql + " LIMIT " + ctx.Bind(box (intValue value))) |> Option.defaultValue sql
            query.Offset |> Option.map (fun value -> withLimit + " OFFSET " + ctx.Bind(box (intValue value))) |> Option.defaultValue withLimit
        | MySql ->
            match query.Limit, query.Offset with
            | None, None -> sql
            | None, Some offset -> sql + " LIMIT 18446744073709551615 OFFSET " + ctx.Bind(box (intValue offset))
            | Some limit, None -> sql + " LIMIT " + ctx.Bind(box (intValue limit))
            | Some limit, Some offset -> sql + " LIMIT " + ctx.Bind(box (intValue limit)) + " OFFSET " + ctx.Bind(box (intValue offset))
        | SQLite ->
            match query.Limit, query.Offset with
            | None, None -> sql
            | None, Some offset -> sql + " LIMIT -1 OFFSET " + ctx.Bind(box (intValue offset))
            | Some limit, None -> sql + " LIMIT " + ctx.Bind(box (intValue limit))
            | Some limit, Some offset -> sql + " LIMIT " + ctx.Bind(box (intValue limit)) + " OFFSET " + ctx.Bind(box (intValue offset))
        | Oracle ->
            match query.Limit, query.FetchPercent, query.Offset, query.FetchWithTies with
            | None, None, None, false -> sql
            | None, None, Some offset, false ->
                sql + " OFFSET " + ctx.Bind(box (int64 (intValue offset))) + " ROWS"
            | Some limit, None, offset, true ->
                sql
                + " OFFSET "
                + ctx.Bind(box (int64 (offset |> Option.map intValue |> Option.defaultValue 0)))
                + " ROWS FETCH NEXT "
                + ctx.Bind(box (intValue limit))
                + " ROWS WITH TIES"
            | Some limit, None, offset, false ->
                sql
                + " OFFSET "
                + ctx.Bind(box (int64 (offset |> Option.map intValue |> Option.defaultValue 0)))
                + " ROWS FETCH NEXT "
                + ctx.Bind(box (intValue limit))
                + " ROWS ONLY"
            | None, Some percent, offset, ties ->
                sql
                + " OFFSET "
                + ctx.Bind(box (int64 (offset |> Option.map intValue |> Option.defaultValue 0)))
                + " ROWS FETCH NEXT "
                + ctx.Bind(box (percentValue percent))
                + " PERCENT ROWS "
                + (if ties then "WITH TIES" else "ONLY")
            | Some _, Some _, _, _ ->
                invalidOp "FETCH row count and percentage reached Oracle rendering simultaneously."
            | None, None, _, true ->
                invalidOp "FETCH WITH TIES reached Oracle rendering without a row count or percentage."
        | Firebird ->
            match query.Limit, query.Offset with
            | Some limit, Some offset when intValue limit > 0 && intValue offset > 0 ->
                let first = Operators.(+) (int64 (intValue offset)) 1L
                let last = Operators.(+) (int64 (intValue offset)) (int64 (intValue limit))
                sql + " ROWS " + ctx.Bind(box first) + " TO " + ctx.Bind(box last)
            | _ -> sql
        | SqlServer -> sql

    and private renderSqlServerOffset (ctx: RenderContext) (query: Query) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let projection = query.Head.Projection
        let outputNames = projection |> List.map projectionOutputName
        let internalNames =
            outputNames
            |> List.mapi (fun index _ ->
                { Value = String.Concat("_core_page_", string index)
                  WasQuoted = false
                  PreserveSpelling = false
                  Span = { Start = 0; Length = 0 } })
        let baseAlias = "[_core_page_base]"
        let wrapperAlias = "[results_wrapper]"
        let rowAlias = "[_core_page_row]"
        let baseProjection = ResizeArray<SqlDocument>()
        (projection, internalNames)
        ||> List.iter2 (fun item alias ->
            baseProjection.Add(renderExpr ctx item.Expression + " AS " + renderAlias ctx alias))

        let projectionIndex (order: OrderBy) =
            match Expr.unspan order.Expression with
            | OrderOrdinal ordinal ->
                let index = PositiveRowCount.value ordinal - 1
                if index >= 0 && index < projection.Length then Some index else None
            | Column identifier
            | BoundColumn(identifier, _) when Identifier.parts identifier |> List.length = 1 ->
                let name = Identifier.parts identifier |> List.head |> fun part -> part.Value
                let aliasMatches =
                    projection
                    |> List.indexed
                    |> List.choose (fun (index, item) ->
                        item.Alias
                        |> Option.bind (fun alias ->
                            if StringComparer.OrdinalIgnoreCase.Equals(alias.Value, name) then Some index else None))
                match aliasMatches with
                | [ index ] -> Some index
                | _ :: _ :: _ -> invalidOp ("SQL Server OFFSET pagination ORDER BY alias '" + name + "' is ambiguous.")
                | [] -> projection |> List.tryFindIndex (fun item -> Expr.equivalent item.Expression order.Expression)
            | _ -> projection |> List.tryFindIndex (fun item -> Expr.equivalent item.Expression order.Expression)

        let windowOrders =
            query.OrderBy
            |> List.mapi (fun orderIndex order ->
                let orderAlias =
                    match projectionIndex order with
                    | Some index -> internalNames[index]
                    | None when not query.Head.Distinct ->
                        let hidden =
                            { Value = String.Concat("_core_page_order_", string orderIndex)
                              WasQuoted = false
                              PreserveSpelling = false
                              Span = { Start = 0; Length = 0 } }
                        baseProjection.Add(renderExpr ctx order.Expression + " AS " + renderAlias ctx hidden)
                        hidden
                    | None ->
                        invalidOp "SQL Server DISTINCT OFFSET pagination requires every ORDER BY expression to resolve to a projected output."
                renderAlias ctx orderAlias + (if order.Descending then " DESC" else " ASC"))

        // OFFSET wrapping used to copy the entire growing SELECT prefix for
        // every JOIN/WHERE/GROUP/HAVING. Append once, with a pre-allocation cap.
        let baseSql = RenderBuffer(ctx, "SELECT ")
        if query.Head.Distinct then baseSql.Append("DISTINCT ") |> ignore
        baseSql.Append(baseProjection |> Seq.toList |> joinDocuments ctx ", ") |> ignore
        query.Head.From
        |> Option.iter (fun source ->
            baseSql.Append(" FROM ").Append(renderSource ctx source) |> ignore)
        for join in query.Head.Joins do
            let joinSql =
                match join with
                | ApplyJoin(ApplyKind.CrossApply, _) -> "CROSS APPLY"
                | ApplyJoin(ApplyKind.OuterApply, _) -> "OUTER APPLY"
                | _ -> joinText join.Kind
            baseSql.Append(" ").Append(joinSql).Append(" ")
                   .Append(renderSource ctx join.Source) |> ignore
            match join with
            | CrossJoin _ | ApplyJoin _ -> ()
            | NaturalJoin _ ->
                invalidOp "NATURAL JOIN cannot enter SQL Server pagination lowering without an explicit target capability."
            | OnJoin(_, _, predicate) ->
                baseSql.Append(" ON ").Append(renderPredicate ctx predicate) |> ignore
            | UsingJoin(_, _, _) ->
                invalidOp "JOIN USING cannot enter SQL Server pagination lowering without an explicit target capability."
        query.Head.Where
        |> Option.iter (fun predicate ->
            baseSql.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
        if not query.Head.GroupBy.IsEmpty then
            baseSql.Append(" GROUP BY ")
                   .Append(query.Head.GroupBy |> List.map (renderExpr ctx) |> joinDocuments ctx ", ") |> ignore
        query.Head.Having
        |> Option.iter (fun predicate ->
            baseSql.Append(" HAVING ").Append(renderPredicate ctx predicate) |> ignore)

        let windowOrder =
            if windowOrders.IsEmpty then doc ctx "ORDER BY (SELECT 0)"
            else "ORDER BY " + joinDocuments ctx ", " windowOrders
        let middleOutputs =
            internalNames
            |> List.map (fun alias -> baseAlias + "." + renderAlias ctx alias)
            |> joinDocuments ctx ", "
        let middleSql = RenderBuffer(ctx, "SELECT ")
        middleSql.Append(middleOutputs)
                 .Append(", ROW_NUMBER() OVER (")
                 .Append(windowOrder)
                 .Append(") AS ")
                 .Append(rowAlias)
                 .Append(" FROM (")
                 .Append(baseSql.ToDocument())
                 .Append(") AS ")
                 .Append(baseAlias) |> ignore
        let outerOutputs =
            (internalNames, outputNames)
            ||> List.map2 (fun internalName externalName ->
                wrapperAlias
                + "."
                + renderAlias ctx internalName
                + " AS "
                + renderAlias ctx externalName)
            |> joinDocuments ctx ", "
        let offset = query.Offset |> Option.map NonNegativeRowCount.value |> Option.defaultValue 0
        let predicate =
            match query.Limit with
            | None -> wrapperAlias + "." + rowAlias + " >= " + ctx.Bind(box (Operators.(+) (int64 offset) 1L))
            | Some limit ->
                wrapperAlias
                + "."
                + rowAlias
                + " BETWEEN "
                + ctx.Bind(box (Operators.(+) (int64 offset) 1L))
                + " AND "
                + ctx.Bind(box (Operators.(+) (int64 offset) (int64 (NonNegativeRowCount.value limit))))
        // Budget every wrapper segment before its next allocation,
        // instead of creating an unbounded second copy of the page SQL.
        let output = RenderBuffer(ctx, renderCtes ctx query.Head.Ctes)
        output.Append("SELECT ")
              .Append(outerOutputs)
              .Append(" FROM (")
              .Append(middleSql.ToDocument())
              .Append(") AS ")
              .Append(wrapperAlias)
              .Append(" WHERE ")
              .Append(predicate)
              .Append(" ORDER BY ")
              .Append(wrapperAlias)
              .Append(".")
              .Append(rowAlias)
              .Append(" ASC")
              .ToDocument()

    and private renderSqlServerSetOffset (ctx: RenderContext) (query: Query) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let outputNames = query.Head.Projection |> List.map projectionOutputName
        let internalNames =
            outputNames
            |> List.mapi (fun index _ ->
                { Value = String.Concat("_core_page_", string index)
                  WasQuoted = false
                  PreserveSpelling = false
                  Span = { Start = 0; Length = 0 } })
        let setAlias = "[_set]"
        let baseAlias = "[_core_page_base]"
        let wrapperAlias = "[results_wrapper]"
        let rowAlias = "[_core_page_row]"
        let body = renderSetBody ctx query
        let pageSourceOutputs =
            (outputNames, internalNames)
            ||> List.map2 (fun outputName internalName ->
                setAlias
                + "."
                + renderAlias ctx outputName
                + " AS "
                + renderAlias ctx internalName)
            |> joinDocuments ctx ", "
        let pageSource = RenderBuffer(ctx, "SELECT ")
        pageSource.Append(pageSourceOutputs)
                  .Append(" FROM (")
                  .Append(body)
                  .Append(") AS ")
                  .Append(setAlias) |> ignore

        let resolveOrder (order: OrderBy) =
            let index =
                match Expr.unspan order.Expression with
                | OrderOrdinal ordinal -> PositiveRowCount.value ordinal - 1
                | Column identifier
                | BoundColumn(identifier, _) when Identifier.parts identifier |> List.length = 1 ->
                    let reference = Identifier.parts identifier |> List.head |> fun part -> part.Value
                    let matches =
                        outputNames
                        |> List.indexed
                        |> List.filter (fun (_, alias) -> StringComparer.OrdinalIgnoreCase.Equals(alias.Value, reference))
                    match matches with
                    | [ (index, _) ] -> index
                    | _ -> invalidOp ("SQL Server set-operation OFFSET pagination ORDER BY reference '" + reference + "' is not a unique combined output name.")
                | _ -> invalidOp "SQL Server set-operation OFFSET pagination supports ORDER BY output names or ordinals only."
            if index < 0 || index >= internalNames.Length then
                invalidOp "SQL Server set-operation OFFSET pagination ORDER BY position is outside the projected output width."
            renderAlias ctx internalNames[index] + (if order.Descending then " DESC" else " ASC")

        let windowOrder =
            if query.OrderBy.IsEmpty then doc ctx "ORDER BY (SELECT 0)"
            else "ORDER BY " + (query.OrderBy |> List.map resolveOrder |> joinDocuments ctx ", ")
        let middleOutputs =
            internalNames
            |> List.map (fun alias -> baseAlias + "." + renderAlias ctx alias)
            |> joinDocuments ctx ", "
        let middleSql = RenderBuffer(ctx, "SELECT ")
        middleSql.Append(middleOutputs)
                 .Append(", ROW_NUMBER() OVER (")
                 .Append(windowOrder)
                 .Append(") AS ")
                 .Append(rowAlias)
                 .Append(" FROM (")
                 .Append(pageSource.ToDocument())
                 .Append(") AS ")
                 .Append(baseAlias) |> ignore
        let outerOutputs =
            (internalNames, outputNames)
            ||> List.map2 (fun internalName externalName ->
                wrapperAlias
                + "."
                + renderAlias ctx internalName
                + " AS "
                + renderAlias ctx externalName)
            |> joinDocuments ctx ", "
        let offset = query.Offset |> Option.map NonNegativeRowCount.value |> Option.defaultValue 0
        let predicate =
            match query.Limit with
            | None -> wrapperAlias + "." + rowAlias + " >= " + ctx.Bind(box (Operators.(+) (int64 offset) 1L))
            | Some limit ->
                wrapperAlias
                + "."
                + rowAlias
                + " BETWEEN "
                + ctx.Bind(box (Operators.(+) (int64 offset) 1L))
                + " AND "
                + ctx.Bind(box (Operators.(+) (int64 offset) (int64 (NonNegativeRowCount.value limit))))
        // Budget every wrapper segment before its next allocation,
        // instead of creating an unbounded second copy of the page SQL.
        let output = RenderBuffer(ctx, renderCtes ctx query.Head.Ctes)
        output.Append("SELECT ")
              .Append(outerOutputs)
              .Append(" FROM (")
              .Append(middleSql.ToDocument())
              .Append(") AS ")
              .Append(wrapperAlias)
              .Append(" WHERE ")
              .Append(predicate)
              .Append(" ORDER BY ")
              .Append(wrapperAlias)
              .Append(".")
              .Append(rowAlias)
              .Append(" ASC")
              .ToDocument()

    and private renderSetTailWrapper (ctx: RenderContext) (query: Query) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        match ctx.Provider, query.Offset, query.Limit with
        | SqlServer, Some offset, limit
            when NonNegativeRowCount.value offset > 0
                 && (limit |> Option.map NonNegativeRowCount.value <> Some 0) ->
            renderSqlServerSetOffset ctx query
        | SqlServer, _, Some limit ->
            let prefix = renderCtes ctx query.Head.Ctes
            let body = renderSetBody ctx query
            let top = ctx.Bind(box (NonNegativeRowCount.value limit))
            let alias = renderAlias ctx { Value = "_set"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            // Retain body-before-TOP binding order, while bounding every
            // wrapper append instead of concatenating another full copy.
            let sql = RenderBuffer(ctx, prefix)
            sql.Append("SELECT TOP (").Append(top).Append(") * FROM (")
               .Append(body).Append(")").Append(tableAliasPrefix ctx.Provider)
               .Append(alias).Append(renderOrderClause ctx true query.OrderBy).ToDocument()
        | Firebird, offset, Some limit when NonNegativeRowCount.value limit = 0 ->
            let first = ctx.Bind(box 0)
            let skip =
                match offset with
                | Some value when NonNegativeRowCount.value value > 0 ->
                    " SKIP " + ctx.Bind(box (NonNegativeRowCount.value value))
                | _ -> doc ctx ""
            let prefix = renderCtes ctx query.Head.Ctes
            let alias = renderAlias ctx { Value = "_set"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            let sql = RenderBuffer(ctx, prefix)
            sql.Append("SELECT FIRST ").Append(first).Append(skip).Append(" * FROM (") |> ignore
            appendSetBody ctx sql query
            sql.Append(")").Append(tableAliasPrefix ctx.Provider).Append(alias)
               .Append(renderOrderClause ctx true query.OrderBy).ToDocument()
        | Firebird, None, Some limit ->
            let first = ctx.Bind(box (NonNegativeRowCount.value limit))
            let prefix = renderCtes ctx query.Head.Ctes
            let alias = renderAlias ctx { Value = "_set"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            let sql = RenderBuffer(ctx, prefix)
            sql.Append("SELECT FIRST ").Append(first).Append(" * FROM (") |> ignore
            appendSetBody ctx sql query
            sql.Append(")").Append(tableAliasPrefix ctx.Provider).Append(alias)
               .Append(renderOrderClause ctx true query.OrderBy).ToDocument()
        | Firebird, Some offset, None when NonNegativeRowCount.value offset > 0 ->
            let skip = ctx.Bind(box (NonNegativeRowCount.value offset))
            let prefix = renderCtes ctx query.Head.Ctes
            let alias = renderAlias ctx { Value = "_set"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            let sql = RenderBuffer(ctx, prefix)
            sql.Append("SELECT SKIP ").Append(skip).Append(" * FROM (") |> ignore
            appendSetBody ctx sql query
            sql.Append(")").Append(tableAliasPrefix ctx.Provider).Append(alias)
               .Append(renderOrderClause ctx true query.OrderBy).ToDocument()
        | _ ->
            let prefix = renderCtes ctx query.Head.Ctes
            let sql = RenderBuffer(ctx, prefix)
            sql.Append("SELECT * FROM (") |> ignore
            appendSetBody ctx sql query
            let alias =
                renderAlias ctx
                    { Value = "_set"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            sql.Append(")")
               .Append(tableAliasPrefix ctx.Provider)
               .Append(alias)
               .Append(renderOrderClause ctx true query.OrderBy)
               // The terminal paging grammar is suffix-only on this path
               // for every provider; rendering it against "" avoids a
               // second copy of the entire SELECT/SET SQL text.
               .Append(renderPaging ctx query (doc ctx ""))
               .ToDocument()

    and private renderQueryCore (ctx: RenderContext) (query: Query) : SqlDocument =
        ctx.ResolveQuery(query, fun () -> renderQueryBody ctx query)

    and private renderQueryBody (ctx: RenderContext) (query: Query) : SqlDocument =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        RewriteStackSafety.compile SqlDiagnosticStage.RenderingInvariant
        if isSetQuery query && hasTail query then renderSetTailWrapper ctx query
        elif ctx.Provider = SqlServer
             && query.Offset |> Option.exists (fun value -> NonNegativeRowCount.value value > 0)
             && (query.Limit |> Option.map NonNegativeRowCount.value <> Some 0)
             && not (isSetQuery query) then
            renderSqlServerOffset ctx query
        else
            match ctx.Provider, query.Offset, query.Limit, isSetQuery query with
            | SqlServer, _, Some limit, false when NonNegativeRowCount.value limit = 0 ->
                let top = ctx.Bind(box 0)
                let ctes = renderCtes ctx query.Head.Ctes
                let body = renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx false query.OrderBy
                let head = if query.Head.Distinct then "SELECT DISTINCT " else "SELECT "
                ctes + head + "TOP (" + top + ") " + withOrder.Substring(head.Length)
            | SqlServer, None, Some limit, false ->
                let top = ctx.Bind(box (NonNegativeRowCount.value limit))
                let ctes = renderCtes ctx query.Head.Ctes
                let body = renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx false query.OrderBy
                let head = if query.Head.Distinct then "SELECT DISTINCT " else "SELECT "
                ctes + head + "TOP (" + top + ") " + withOrder.Substring(head.Length)
            | Firebird, None, Some limit, false ->
                let first = ctx.Bind(box (NonNegativeRowCount.value limit))
                let ctes = renderCtes ctx query.Head.Ctes
                let body = renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx false query.OrderBy
                let head = if query.Head.Distinct then "SELECT DISTINCT " else "SELECT "
                let replacement = if query.Head.Distinct then "SELECT FIRST " + first + " DISTINCT " else "SELECT FIRST " + first + " "
                ctes + replacement + withOrder.Substring(head.Length)
            | Firebird, Some offset, Some limit, false when NonNegativeRowCount.value limit = 0 ->
                let first = ctx.Bind(box 0)
                let skip =
                    if NonNegativeRowCount.value offset > 0 then
                        " SKIP " + ctx.Bind(box (NonNegativeRowCount.value offset))
                    else doc ctx ""
                let ctes = renderCtes ctx query.Head.Ctes
                let body = renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx false query.OrderBy
                let head = if query.Head.Distinct then "SELECT DISTINCT " else "SELECT "
                let replacement =
                    if query.Head.Distinct then "SELECT FIRST " + first + skip + " DISTINCT "
                    else "SELECT FIRST " + first + skip + " "
                ctes + replacement + withOrder.Substring(head.Length)
            | Firebird, Some offset, None, false when NonNegativeRowCount.value offset > 0 ->
                let skip = ctx.Bind(box (NonNegativeRowCount.value offset))
                let ctes = renderCtes ctx query.Head.Ctes
                let body = renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx false query.OrderBy
                let head = if query.Head.Distinct then "SELECT DISTINCT " else "SELECT "
                let replacement = if query.Head.Distinct then "SELECT SKIP " + skip + " DISTINCT " else "SELECT SKIP " + skip + " "
                ctes + replacement + withOrder.Substring(head.Length)
            | _ ->
                let ctes = renderCtes ctx query.Head.Ctes
                let body = if isSetQuery query then renderSetBody ctx query else renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx (isSetQuery query) query.OrderBy
                ctes + renderPaging ctx query withOrder

    and private tryRenderMixedLinearQuery (ctx: RenderContext) (query: Query) =
        // Restrict this optimization to a proven, non-recursive linear
        // grammar: single CTE definition or ordinary derived FROM,
        // SELECT without SET/paging or GROUP/JOIN/DISTINCT. All richer
        // cases go through the existing capability-checked renderer.
        let frames = ResizeArray<MixedQueryEmitFrame>()
        let mutable current = query
        let mutable sawCte = false
        let mutable sawDerived = false
        let mutable sawLateral = false
        let mutable scanning = true
        while scanning do
            RewriteResourceBudget.checkpoint ()
            if current.SetOperations.IsEmpty && not (hasTail current) then
                let head = current.Head
                match head.Ctes, head.From with
                | [ cte ], Some (TableSource.CteTable _)
                    when not cte.RecursiveScope ->
                    frames.Add(FinishMixedCte(current, cte))
                    sawCte <- true
                    current <- cte.Query
                | [], Some (TableSource.DerivedTable(inner, alias))
                    when head.DistinctMode = SelectDistinct.AllRows
                         && head.Joins.IsEmpty && head.GroupBy.IsEmpty
                         && head.Having.IsNone ->
                    frames.Add(FinishMixedDerived(current, alias))
                    sawDerived <- true
                    current <- inner
                | [], Some (TableSource.LateralDerivedTable(inner, alias))
                    when head.DistinctMode = SelectDistinct.AllRows
                         && head.Joins.IsEmpty && head.GroupBy.IsEmpty
                         && head.Having.IsNone ->
                    // Skipping renderSource must not skip its fail-closed
                    // provider proof. Only a primary FROM LATERAL is safe
                    // to stream without constructing left-join scope.
                    match SqlLateralDerivedTableCapabilityRules.SyntaxEmissionValidationError(providerTool ctx.Provider) with
                    | null ->
                        frames.Add(FinishMixedLateral(current, alias))
                        sawLateral <- true
                        current <- inner
                    | message -> raise (SqlCompilationException(message))
                | [], Some ((TableSource.NamedTable _ | TableSource.CteTable _) as preceding)
                    when head.DistinctMode = SelectDistinct.AllRows
                         && head.GroupBy.IsEmpty && head.Having.IsNone ->
                    match head.Joins with
                    | [ OnJoin(OnJoinKind.Inner, TableSource.LateralDerivedTable(inner, alias), predicate) ] ->
                        match SqlLateralDerivedTableCapabilityRules.SyntaxEmissionValidationError(providerTool ctx.Provider) with
                        | null ->
                            frames.Add(FinishMixedJoinedLateral(current, preceding, alias, predicate))
                            sawLateral <- true
                            current <- inner
                        | message -> raise (SqlCompilationException(message))
                    | joins ->
                        match List.rev joins with
                        | OnJoin(OnJoinKind.Inner, TableSource.LateralDerivedTable(inner, alias), predicate) :: precedingReversed
                            when not precedingReversed.IsEmpty &&
                                 (precedingReversed |> List.forall (function
                                     | OnJoin(OnJoinKind.Inner,
                                              (TableSource.NamedTable _ | TableSource.CteTable _ | TableSource.LateralDerivedTable _), _) -> true
                                     | _ -> false)) ->
                            // All preceding sources/predicates keep their
                            // original parameter order. Only the last
                            // Query crosses the heap continuation.
                            match SqlLateralDerivedTableCapabilityRules.SyntaxEmissionValidationError(providerTool ctx.Provider) with
                            | null ->
                                frames.Add(FinishMixedPrefixedJoinedLateral(
                                    current, preceding, List.rev precedingReversed, alias, predicate))
                                sawLateral <- true
                                current <- inner
                            | message -> raise (SqlCompilationException(message))
                        | _ -> scanning <- false
                | _ -> scanning <- false
            else
                scanning <- false

        if not (sawCte && sawDerived) && not sawLateral then None
        else
            let sql = RenderBuffer(ctx, "")
            // Emit the lexical SQL prefixes outside-in. Projection
            // expressions bind before each derived body, whereas CTE
            // definitions bind before their referencing SELECT.
            for frame in frames do
                RewriteResourceBudget.checkpoint ()
                match frame with
                | FinishMixedCte(_, cte) ->
                    sql.Append("WITH ")
                       .Append(renderAlias ctx cte.Name)
                       .Append(" AS (") |> ignore
                | FinishMixedDerived(outer, _)
                | FinishMixedLateral(outer, _)
                | FinishMixedJoinedLateral(outer, _, _, _)
                | FinishMixedPrefixedJoinedLateral(outer, _, _, _, _) as frame ->
                    sql.Append("SELECT ") |> ignore
                    outer.Head.Projection
                    |> List.iteri (fun index item ->
                        if index > 0 then sql.Append(", ") |> ignore
                        sql.Append(renderExpr ctx item.Expression) |> ignore
                        item.Alias |> Option.iter (fun alias ->
                            sql.Append(" AS ").Append(renderAlias ctx alias) |> ignore))
                    match frame with
                    | FinishMixedLateral _ -> sql.Append(" FROM LATERAL (") |> ignore
                    | FinishMixedJoinedLateral(_, preceding, _, _) ->
                        sql.Append(" FROM ").Append(renderSource ctx preceding)
                           .Append(" INNER JOIN LATERAL (") |> ignore
                    | FinishMixedPrefixedJoinedLateral(_, preceding, earlier, _, _) ->
                        sql.Append(" FROM ").Append(renderSource ctx preceding) |> ignore
                        for join in earlier do
                            RewriteResourceBudget.checkpoint ()
                            sql.Append(" INNER JOIN ").Append(renderSource ctx join.Source) |> ignore
                            match join with
                            | OnJoin(_, _, condition) ->
                                sql.Append(" ON ").Append(renderPredicate ctx condition) |> ignore
                            | _ -> invalidOp "Streaming JOIN proof requires an ON predicate."
                        sql.Append(" INNER JOIN LATERAL (") |> ignore
                    | _ -> sql.Append(" FROM (") |> ignore

            sql.Append(renderQueryCore ctx current) |> ignore
            for index = frames.Count - 1 downto 0 do
                RewriteResourceBudget.checkpoint ()
                match frames.[index] with
                | FinishMixedCte(outer, _) ->
                    sql.Append(") ")
                       .Append(renderSelectBody ctx { outer.Head with Ctes = [] })
                    |> ignore
                | FinishMixedDerived(outer, alias)
                | FinishMixedLateral(outer, alias) ->
                    sql.Append(')')
                       .Append(tableAliasPrefix ctx.Provider)
                       .Append(renderAlias ctx alias) |> ignore
                    outer.Head.Where |> Option.iter (fun predicate ->
                        sql.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
                | FinishMixedJoinedLateral(outer, _, alias, onPredicate)
                | FinishMixedPrefixedJoinedLateral(outer, _, _, alias, onPredicate) ->
                    sql.Append(')')
                       .Append(tableAliasPrefix ctx.Provider)
                       .Append(renderAlias ctx alias)
                       .Append(" ON ")
                       .Append(renderPredicate ctx onPredicate) |> ignore
                    outer.Head.Where |> Option.iter (fun predicate ->
                        sql.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
            Some(sql.ToDocument())

    // A linear correlated WHERE column IN (Query) chain needs a single
    // append-only SQL buffer: Query -> predicate -> Query must not build
    // recursive SELECT strings. Projection values still bind before IN.
    and private tryRenderLinearInQuery (ctx: RenderContext) (query: Query) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let tryWrapper (current: Query) =
            let head = current.Head
            if not head.Ctes.IsEmpty || not head.Joins.IsEmpty
               || head.DistinctMode <> SelectDistinct.AllRows
               || not head.GroupBy.IsEmpty || head.Having.IsSome
               || not current.SetOperations.IsEmpty || hasTail current then None
            else
                match head.Projection, head.From, head.Where |> Option.map Expr.unspan with
                | [ item ], Some ((TableSource.NamedTable _ | TableSource.CteTable _) as source),
                  Some(InSubquery(value, inner, false)) ->
                    match Expr.unspan item.Expression, Expr.unspan value with
                    | (Literal _ | Column _ | BoundColumn _),
                      (Column _ | BoundColumn _) -> Some(item, source, value, inner)
                    | _ -> None
                | _ -> None
        let output = RenderBuffer(ctx, "")
        let mutable current = query
        let mutable count = 0
        let mutable scanning = true
        while scanning do
            RewriteResourceBudget.checkpoint ()
            match tryWrapper current with
            | Some(item, source, value, inner) ->
                output.Append("SELECT ").Append((renderExpr ctx item.Expression) : SqlDocument) |> ignore
                item.Alias |> Option.iter (fun alias ->
                    output.Append(" AS ").Append(renderAlias ctx alias) |> ignore)
                output.Append(" FROM ").Append((renderSource ctx source) : SqlDocument)
                      .Append(" WHERE (").Append((renderExpr ctx value) : SqlDocument)
                      .Append(" IN (") |> ignore
                count <- Operators.(+) count 1
                current <- inner
            | None -> scanning <- false
        if count = 0 then None
        else
            output.Append((renderQueryCore ctx current) : SqlDocument) |> ignore
            for _ in 1 .. count do output.Append("))") |> ignore
            Some(output.ToDocument())


    // Append a linear searched CASE -> scalar Query chain outside-in.
    // Condition parameters precede child parameters; ELSE binds on unwind.
    and private tryRenderLinearCaseQuery (ctx: RenderContext) (query: Query) =
        let tryWrapper (current: Query) =
            let head = current.Head
            if not head.Ctes.IsEmpty || head.From.IsSome
               || not head.Joins.IsEmpty
               || head.DistinctMode <> SelectDistinct.AllRows
               || head.Where.IsSome || not head.GroupBy.IsEmpty
               || head.Having.IsSome
               || not current.SetOperations.IsEmpty || hasTail current then None
            else
                match head.Projection with
                | [ { Alias = None; Expression = projection } ] ->
                    match Expr.unspan projection with
                    | SearchedCase(branches, Some fallback) ->
                        match NonEmpty.toList branches, Expr.unspan fallback with
                        | [ branch ], Literal _ ->
                            match Expr.unspan branch.Result, Expr.unspan branch.Condition with
                            | ScalarSubquery child, Binary _ ->
                                Some(branch.Condition, fallback, child)
                            | _ -> None
                        | _ -> None
                    | _ -> None
                | _ -> None
        let dummyFrom =
            match ctx.Provider with
            | Oracle -> " FROM DUAL"
            | Firebird -> " FROM RDB$DATABASE"
            | _ -> ""
        let mutable current = query
        let fallbacks = ResizeArray<Expr>()
        let output = RenderBuffer(ctx, "")
        let mutable scanning = true
        while scanning do
            RewriteResourceBudget.checkpoint ()
            match tryWrapper current with
            | Some(condition, fallback, child) ->
                output.Append("SELECT CASE WHEN ")
                      .Append((renderPredicate ctx condition) : SqlDocument)
                      .Append(" THEN (") |> ignore
                fallbacks.Add(fallback)
                current <- child
            | None -> scanning <- false
        if fallbacks.Count = 0 then None
        else
            output.Append((renderQueryCore ctx current) : SqlDocument) |> ignore
            for index = fallbacks.Count - 1 downto 0 do
                output.Append(") ELSE ")
                      .Append((renderExpr ctx fallbacks.[index]) : SqlDocument)
                      .Append(" END")
                      .Append(dummyFrom) |> ignore
            Some(output.ToDocument())

    and private renderQuery (ctx: RenderContext) query : SqlDocument =
        ctx.ResolveQuery(query, fun () -> renderQueryEntry ctx query)

    and private renderQueryEntry (ctx: RenderContext) query : SqlDocument =
        match tryRenderLinearCaseQuery ctx query with
        | Some sql -> sql
        | None ->
            match tryRenderLinearInQuery ctx query with
            | Some sql -> sql
            | None ->
                match tryRenderMixedLinearQuery ctx query with
                | Some sql -> sql
                | None -> renderQueryCore ctx query

    and private renderSubquery (ctx: RenderContext) query : SqlDocument =
        ctx.ResolveQuery(query, fun () -> renderSubqueryBody ctx query)

    and private renderSubqueryBody (ctx: RenderContext) query : SqlDocument =
        if isSetQuery query
           && hasTail query
           && (ctx.Provider = PostgreSql || ctx.Provider = MySql || ctx.Provider = SQLite) then
            let prefix = renderCtes ctx query.Head.Ctes
            let sql = RenderBuffer(ctx, prefix)
            appendSetBody ctx sql query
            sql.Append(renderOrderClause ctx true query.OrderBy)
               .Append(renderPaging ctx query (doc ctx "")).ToDocument()
        else
            renderQueryCore ctx query

    and private renderCteQuery (ctx: RenderContext) query : SqlDocument =
        ctx.ResolveQuery(query, fun () -> renderCteQueryBody ctx query)

    and private renderCteQueryBody (ctx: RenderContext) query : SqlDocument =
        if isSetQuery query
           && hasTail query
           && not query.Head.Ctes.IsEmpty
           && (ctx.Provider = PostgreSql || ctx.Provider = MySql || ctx.Provider = SQLite) then
            let sql = RenderBuffer(ctx, "SELECT * FROM (")
            sql.Append(renderCtes ctx query.Head.Ctes) |> ignore
            appendSetBody ctx sql query
            let alias =
                renderAlias ctx
                    { Value = "_set"
                      WasQuoted = false
                      PreserveSpelling = false
                      Span = { Start = 0; Length = 0 } }
            sql.Append(")").Append(tableAliasPrefix ctx.Provider).Append(alias)
               .Append(renderOrderClause ctx true query.OrderBy)
               .Append(renderPaging ctx query (doc ctx "")).ToDocument()
        else
            renderQueryCore ctx query

    let private renderReturning (ctx: RenderContext) (items: ReturningItem list) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        if List.isEmpty items then doc ctx ""
        else
            match ctx.Provider with
            | PostgreSql | SQLite | Firebird ->
                " RETURNING " + (items |> List.map (fun item -> renderExpr ctx item.Expression + (item.Alias |> Option.map (fun alias -> " AS " + renderAlias ctx alias) |> Option.defaultValue (doc ctx ""))) |> joinDocuments ctx ", ")
            | _ -> invalidOp "RETURNING is not supported by the target provider."

    let private renderSqlServerOutput image (ctx: RenderContext) (items: ReturningItem list) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        if List.isEmpty items then doc ctx ""
        else
            let renderItem = function
                | ReturningColumn(identifier, alias) ->
                    if Identifier.parts identifier |> List.length <> 1 then
                        invalidOp "Validated SQL Server OUTPUT requires target-only single-part columns."
                    image
                    + "."
                    + renderIdentifier ctx identifier
                    + (alias
                       |> Option.map (fun value -> " AS " + renderAlias ctx value)
                       |> Option.defaultValue (doc ctx ""))
                | ReturningWildcard None -> image + ".*"
                | ReturningWildcard(Some _) ->
                    invalidOp "Validated SQL Server OUTPUT wildcard cannot be aliased."
                | ReturningExpression _ ->
                    invalidOp "Validated SQL Server OUTPUT does not admit rich expressions."
            " OUTPUT " + (items |> List.map renderItem |> joinDocuments ctx ", ")

    let private renderTrailingReturning (ctx: RenderContext) items =
        if ctx.Provider = SqlServer then doc ctx "" else renderReturning ctx items

    let private conflictArithmeticText = function
        | ConflictAdd -> "+"
        | ConflictSubtract -> "-"
        | ConflictMultiply -> "*"
        | ConflictDivide -> "/"
        | ConflictModulo -> "%"

    [<Struct>]
    type private ConflictRenderFrame =
        | ConflictNode of conflictValue: ConflictValue
        | ConflictText of emittedText: string

    let private renderConflictValue (ctx: RenderContext) (proposedQualifier: string) (value: ConflictValue) =
        // Explicit preorder emission preserves left-to-right parameter
        // binding for nested conflict arithmetic without consuming a CLR
        // call frame for every ConflictBinary/ConflictNegate.
        let pending = ResizeArray<ConflictRenderFrame>()
        let output = RenderBuffer(ctx, "")
        pending.Add(ConflictNode value)
        while pending.Count > 0 do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            match frame with
            | ConflictText text -> output.Append(text) |> ignore
            | ConflictNode node ->
                match node with
                | ConflictProposedColumn identifier ->
                    output.Append(proposedQualifier).Append(".").Append(renderIdentifier ctx identifier)
                    |> ignore
                | ConflictTargetColumn identifier ->
                    output.Append(renderIdentifier ctx identifier) |> ignore
                | ConflictLiteral scalar ->
                    output.Append(renderExpr ctx (Expr.Literal scalar)) |> ignore
                | ConflictNegate operand ->
                    output.Append("(-") |> ignore
                    pending.Add(ConflictText ")")
                    pending.Add(ConflictNode operand)
                | ConflictBinary(operator, left, right) ->
                    output.Append("(") |> ignore
                    pending.Add(ConflictText ")")
                    pending.Add(ConflictNode right)
                    pending.Add(ConflictText(" " + conflictArithmeticText operator + " "))
                    pending.Add(ConflictNode left)
        output.ToDocument()

    let private renderConflict (ctx: RenderContext) conflict =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        match conflict with
        | None -> doc ctx ""
        | Some conflict ->
            let targetClause =
                conflict.TargetColumns
                |> Option.map (fun columns ->
                    " (" + (columns |> NonEmpty.toList |> List.map (renderIdentifier ctx) |> joinDocuments ctx ", ") + ")")
                |> Option.defaultValue (doc ctx "")
            match ctx.Provider with
            | PostgreSql | SQLite ->
                match conflict.Action with
                | DoNothing -> " ON CONFLICT" + targetClause + " DO NOTHING"
                | UpdateProposedValues assignments ->
                    if Option.isNone conflict.TargetColumns then
                        invalidOp "ON CONFLICT DO UPDATE reached rendering without an explicit conflict target."
                    let values =
                        assignments
                        |> NonEmpty.toList
                        |> List.map (fun assignment ->
                            renderIdentifier ctx assignment.Target
                            + " = "
                            + renderConflictValue ctx "EXCLUDED" assignment.Value)
                        |> joinDocuments ctx ", "
                    " ON CONFLICT" + targetClause + " DO UPDATE SET " + values
            | _ -> invalidOp "Portable INSERT conflict lowering is not supported by the target provider."

    // Row-major VALUES rendering must not allocate one intermediate SQL
    // string per row and a second aggregate concatenation. All writes check
    // the bound before StringBuilder growth, including conflict/RETURNING.
    let private appendValues (ctx: RenderContext) (output: RenderBuffer) rows =
        rows
        |> NonEmpty.toList
        |> List.iteri (fun rowIndex row ->
            if rowIndex > 0 then output.Append(", ") |> ignore
            output.Append('(') |> ignore
            row
            |> NonEmpty.toList
            |> List.iteri (fun columnIndex value ->
                if columnIndex > 0 then output.Append(", ") |> ignore
                output.Append(renderExpr ctx value) |> ignore)
            output.Append(')') |> ignore)

    let private renderInsertValues (ctx: RenderContext) (prefix: SqlDocument) rows conflict returning =
        let result = RenderBuffer(ctx, prefix)
        result.Append(" VALUES ") |> ignore
        appendValues ctx result rows
        result.Append(renderConflict ctx conflict)
              .Append(renderTrailingReturning ctx returning).ToDocument()

    let private renderInsert (ctx: RenderContext) insert =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let columns = if insert.Columns.IsEmpty then doc ctx "" else " (" + (insert.Columns |> List.map (renderAlias ctx) |> joinDocuments ctx ", ") + ")"
        match ctx.Provider, insert.Conflict with
        | Firebird, Some conflict ->
            let values =
                match insert.Input with
                | Values rows when NonEmpty.length rows = 1 -> rows |> NonEmpty.toList |> List.head |> NonEmpty.toList |> List.map (renderExpr ctx) |> joinDocuments ctx ", "
                | _ -> invalidOp "Firebird UPDATE OR INSERT requires exactly one VALUES row."
            let targets =
                conflict.TargetColumns
                |> Option.defaultWith (fun () -> invalidOp "Firebird UPDATE OR INSERT requires an explicit MATCHING conflict target.")
                |> NonEmpty.toList
                |> List.map (renderIdentifier ctx)
                |> joinDocuments ctx ", "
            "UPDATE OR INSERT INTO " + renderIdentifier ctx insert.Target + columns + " VALUES (" + values + ") MATCHING (" + targets + ")" + renderTrailingReturning ctx insert.Returning
        | MySql, Some conflict ->
            let values =
                match insert.Input with
                | Values rows when NonEmpty.length rows = 1 ->
                    rows
                    |> NonEmpty.toList
                    |> List.head
                    |> NonEmpty.toList
                    |> List.map (renderExpr ctx)
                    |> joinDocuments ctx ", "
                | _ ->
                    invalidOp "Validated MySQL conflict lowering requires exactly one VALUES row."
            let preferredAlias = "__core_proposed"
            let targetName =
                insert.Target
                |> Identifier.parts
                |> List.last
                |> fun part -> part.Value
            let aliasName =
                if StringComparer.OrdinalIgnoreCase.Equals(targetName, preferredAlias) then
                    String.Concat(preferredAlias, "_row")
                else
                    preferredAlias
            let alias =
                renderAlias
                    ctx
                    { Value = aliasName
                      WasQuoted = false
                      PreserveSpelling = false
                      Span = { Start = 0; Length = 0 } }
            let assignments =
                match conflict.Action with
                | DoNothing ->
                    invalidOp "Validated MySQL conflict lowering cannot contain DO NOTHING."
                | UpdateProposedValues assignments ->
                    assignments
                    |> NonEmpty.toList
                    |> List.map (fun assignment ->
                        let proposed =
                            match ConflictValue.trySimpleProposed assignment.Value with
                            | Some identifier -> identifier
                            | None ->
                                invalidOp "Validated MySQL conflict lowering admits only direct proposed-row assignments."
                        renderIdentifier ctx assignment.Target
                        + " = "
                        + alias
                        + "."
                        + renderIdentifier ctx proposed)
                    |> joinDocuments ctx ", "
            "INSERT INTO "
            + renderIdentifier ctx insert.Target
            + columns
            + " VALUES ("
            + values
            + ") AS "
            + alias
            + " ON DUPLICATE KEY UPDATE "
            + assignments
        | Oracle, None ->
            let prefix = "INSERT INTO " + renderIdentifier ctx insert.Target + columns
            match insert.Input with
            | Values rows when NonEmpty.length rows > 1 ->
                let table = renderIdentifier ctx insert.Target
                let result = RenderBuffer(ctx, "INSERT ALL")
                rows |> NonEmpty.toList |> List.iter (fun row ->
                    result.Append(" INTO ").Append(table).Append(columns)
                          .Append(" VALUES (") |> ignore
                    row |> NonEmpty.toList |> List.iteri (fun index value ->
                        if index > 0 then result.Append(", ") |> ignore
                        result.Append(renderExpr ctx value) |> ignore)
                    result.Append(')') |> ignore)
                result.Append(" SELECT 1 FROM DUAL")
                      .Append(renderTrailingReturning ctx insert.Returning).ToDocument()
            | QuerySource query when not query.Head.Ctes.IsEmpty ->
                let withClause = renderCtes ctx query.Head.Ctes
                let source = renderQuery ctx { query with Head = { query.Head with Ctes = [] } }
                prefix + " " + withClause + source + renderTrailingReturning ctx insert.Returning
            | QuerySource query -> prefix + " " + renderQuery ctx query + renderTrailingReturning ctx insert.Returning
            | Values rows ->
                renderInsertValues ctx prefix rows None insert.Returning
            | DefaultValues -> prefix + " DEFAULT VALUES" + renderTrailingReturning ctx insert.Returning
        | Firebird, None ->
            let prefix = "INSERT INTO " + renderIdentifier ctx insert.Target + columns
            match insert.Input with
            | Values rows when NonEmpty.length rows > 1 ->
                let result = RenderBuffer(ctx, prefix + " ")
                rows |> NonEmpty.toList |> List.iteri (fun rowIndex row ->
                    if rowIndex > 0 then result.Append(" UNION ALL ") |> ignore
                    result.Append("SELECT ") |> ignore
                    row |> NonEmpty.toList |> List.iteri (fun index value ->
                        if index > 0 then result.Append(", ") |> ignore
                        result.Append(renderExpr ctx value) |> ignore)
                    result.Append(" FROM RDB$DATABASE") |> ignore)
                result.Append(renderTrailingReturning ctx insert.Returning).ToDocument()
            | QuerySource query when not query.Head.Ctes.IsEmpty ->
                let withClause = renderCtes ctx query.Head.Ctes
                let source = renderQuery ctx { query with Head = { query.Head with Ctes = [] } }
                prefix + " " + withClause + source + renderTrailingReturning ctx insert.Returning
            | QuerySource query -> prefix + " " + renderQuery ctx query + renderTrailingReturning ctx insert.Returning
            | Values rows ->
                renderInsertValues ctx prefix rows None insert.Returning
            | DefaultValues -> prefix + " DEFAULT VALUES" + renderTrailingReturning ctx insert.Returning
        | _ ->
            let prefix =
                "INSERT INTO "
                + renderIdentifier ctx insert.Target
                + columns
                + (if ctx.Provider = SqlServer then
                       renderSqlServerOutput "INSERTED" ctx insert.Returning
                   else
                       doc ctx "")
            match insert.Input with
            | QuerySource query when not query.Head.Ctes.IsEmpty ->
                let withClause = renderCtes ctx query.Head.Ctes
                let source = renderQuery ctx { query with Head = { query.Head with Ctes = [] } }
                match ctx.Provider with
                | PostgreSql | SqlServer | SQLite ->
                    withClause + prefix + " " + source
                    + renderConflict ctx insert.Conflict + renderTrailingReturning ctx insert.Returning
                | MySql ->
                    prefix + " " + withClause + source
                    + renderConflict ctx insert.Conflict + renderTrailingReturning ctx insert.Returning
                | Oracle | Firebird ->
                    invalidOp "Provider-specific INSERT ... SELECT path was not selected."
            | QuerySource query ->
                prefix + " " + renderQuery ctx query
                + renderConflict ctx insert.Conflict + renderTrailingReturning ctx insert.Returning
            | Values rows ->
                renderInsertValues ctx prefix rows insert.Conflict insert.Returning
            | DefaultValues ->
                prefix + " DEFAULT VALUES"
                + renderConflict ctx insert.Conflict + renderTrailingReturning ctx insert.Returning

    let private renderUpdate (ctx: RenderContext) (update: Update) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let targetAlias =
            match update.TargetAlias with
            | None -> doc ctx ""
            | Some alias when ctx.Provider = PostgreSql || ctx.Provider = Firebird ->
                " AS " + renderAlias ctx alias
            | Some _ -> invalidOp "DML target aliases are not supported by the target provider."

        // Emit each assignment after checking the output work budget,
        // instead of constructing both a list of rendered assignments and
        // a second joined SET clause. Render parameter order is unchanged.
        let sql = RenderBuffer(ctx, renderCtes ctx update.Ctes + "UPDATE ")
        sql.Append(renderIdentifier ctx update.Target)
           .Append(targetAlias)
           .Append(" SET ") |> ignore
        update.Assignments
        |> List.iteri (fun index assignment ->
            if index > 0 then sql.Append(", ") |> ignore
            sql.Append(renderIdentifier ctx assignment.Target)
               .Append(" = ")
               .Append(renderExpr ctx assignment.Value) |> ignore)
        if ctx.Provider = SqlServer then
            sql.Append(renderSqlServerOutput "INSERTED" ctx update.Returning) |> ignore
        if not update.From.IsEmpty then
            if ctx.Provider <> PostgreSql
               && ctx.Provider <> SqlServer
               && ctx.Provider <> SQLite
               && ctx.Provider <> Oracle then
                invalidOp "UPDATE ... FROM is not supported by the target provider."
            sql.Append(" FROM ") |> ignore
            update.From
            |> List.iteri (fun index source ->
                if index > 0 then sql.Append(", ") |> ignore
                sql.Append(renderSource ctx source) |> ignore)
        update.Where
        |> Option.iter (fun predicate ->
            sql.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
        sql.Append(renderTrailingReturning ctx update.Returning).ToDocument()

    let private renderDelete (ctx: RenderContext) (delete: Delete) =
        let (+) (left: obj) (right: obj) = addDocument ctx left right
        let invalidOp message = invalidDocument ctx message
        let targetAlias =
            match delete.TargetAlias with
            | None -> doc ctx ""
            | Some alias when ctx.Provider = PostgreSql || ctx.Provider = Firebird ->
                " AS " + renderAlias ctx alias
            | Some _ -> invalidOp "DML target aliases are not supported by the target provider."
        let renderedTarget = renderIdentifier ctx delete.Target
        let sql = RenderBuffer(ctx,
            renderCtes ctx delete.Ctes
            + "DELETE FROM "
            + renderedTarget
            + targetAlias
            + (if ctx.Provider = SqlServer then
                   renderSqlServerOutput "DELETED" ctx delete.Returning
               else
                   doc ctx ""))
        if not delete.Using.IsEmpty then
            match ctx.Provider with
            | PostgreSql ->
                sql.Append(" USING ") |> ignore
            | SqlServer ->
                if delete.TargetAlias.IsSome then
                    invalidOp "SQL Server joined DELETE target aliases require FROM-scope alias declaration and are not represented by the current Core DML target-alias shape."
                sql.Append(" FROM ").Append(renderedTarget).Append(", ") |> ignore
            | Oracle ->
                if delete.TargetAlias.IsSome then
                    invalidOp "Oracle direct-join DELETE target aliases are not represented by the current Core target-alias lowering."
                sql.Append(" FROM ") |> ignore
            | _ ->
                invalidOp "Joined DELETE is not supported by the target provider."
            delete.Using
            |> List.iteri (fun index source ->
                if index > 0 then sql.Append(", ") |> ignore
                sql.Append(renderSource ctx source) |> ignore)
        delete.Where
        |> Option.iter (fun predicate ->
            sql.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
        sql.Append(renderTrailingReturning ctx delete.Returning).ToDocument()

    let private renderMerge (ctx: RenderContext) (merge: Merge) =
        if ctx.Provider <> SqlServer then
            invalidOp "Validated canonical MERGE reached a non-SQL Server renderer."
        // Source values must bind before ON, matched UPDATE and unmatched
        // INSERT expressions. One bounded buffer preserves that ordering
        // without large joined intermediate assignment/value strings.
        let sql = RenderBuffer(ctx, "MERGE INTO ")
        sql.Append(renderIdentifier ctx merge.Target)
           .Append(" AS ")
           .Append(renderAlias ctx merge.TargetAlias)
           .Append(" USING (VALUES (") |> ignore
        merge.Source.SourceValues
        |> NonEmpty.toList
        |> List.iteri (fun index value ->
            if index > 0 then sql.Append(", ") |> ignore
            sql.Append(renderExpr ctx value) |> ignore)
        sql.Append(")) AS ")
           .Append(renderAlias ctx merge.Source.Alias)
           .Append(" (") |> ignore
        merge.Source.SourceColumns
        |> NonEmpty.toList
        |> List.iteri (fun index column ->
            if index > 0 then sql.Append(", ") |> ignore
            sql.Append(renderAlias ctx column) |> ignore)
        sql.Append(") ON ").Append(renderPredicate ctx merge.MatchPredicate) |> ignore

        merge.Matched
        |> Option.iter (function
            | MergeDelete ->
                sql.Append(" WHEN MATCHED THEN DELETE") |> ignore
            | MergeUpdate assignments ->
                sql.Append(" WHEN MATCHED THEN UPDATE SET ") |> ignore
                assignments
                |> NonEmpty.toList
                |> List.iteri (fun index assignment ->
                    if index > 0 then sql.Append(", ") |> ignore
                    sql.Append(renderIdentifier ctx assignment.Target)
                       .Append(" = ")
                       .Append(renderExpr ctx assignment.Value) |> ignore))

        merge.NotMatched
        |> Option.iter (fun mergeInsert ->
            sql.Append(" WHEN NOT MATCHED THEN INSERT (") |> ignore
            mergeInsert.InsertColumns
            |> NonEmpty.toList
            |> List.iteri (fun index column ->
                if index > 0 then sql.Append(", ") |> ignore
                sql.Append(renderIdentifier ctx column) |> ignore)
            sql.Append(") VALUES (") |> ignore
            mergeInsert.InsertValues
            |> NonEmpty.toList
            |> List.iteri (fun index value ->
                if index > 0 then sql.Append(", ") |> ignore
                sql.Append(renderExpr ctx value) |> ignore)
            sql.Append(')') |> ignore)

        sql.Append(';').ToDocument()

    let private providerForRuntime = function
        | TargetRuntime.PostgreSqlRuntime -> Provider.PostgreSql
        | TargetRuntime.MySqlRuntime -> Provider.MySql
        | TargetRuntime.SqlServerRuntime _ -> Provider.SqlServer
        | TargetRuntime.SQLiteRuntime -> Provider.SQLite
        | TargetRuntime.OracleRuntime -> Provider.Oracle
        | TargetRuntime.FirebirdRuntime -> Provider.Firebird

    let render (executable: RewritePolicy.ExecutableSql) : RenderedCommand =
        let targetRuntime = RewritePolicy.Executable.targetRuntime executable
        let document = RewritePolicy.Executable.value executable
        let ctx = RenderContext(providerForRuntime targetRuntime, targetRuntime, document.Span)
        let sql, returnsRows =
            match document.Statement with
            | Statement.QueryStatement query -> renderQuery ctx query, false
            | Statement.InsertStatement insert -> renderInsert ctx insert, not insert.Returning.IsEmpty
            | Statement.UpdateStatement update -> renderUpdate ctx update, not update.Returning.IsEmpty
            | Statement.DeleteStatement delete -> renderDelete ctx delete, not delete.Returning.IsEmpty
            | Statement.MergeStatement merge -> renderMerge ctx merge, false
        // Fail closed before exposing a generated command to the executor.
        // This guards the delivered artifact size; individual specialized
        // emitters still need streaming checks to cap transient allocations.
        if sql.Length > MaxRenderedSqlLength then
            let detail =
                "Rendered SQL exceeds the maximum "
                + string MaxRenderedSqlLength
                + " UTF-16 code units."
            let location =
                if document.Span.Start >= 0 && document.Span.Length >= 0 then
                    SqlDiagnosticSpan(document.Span.Start, document.Span.Length)
                else
                    Unchecked.defaultof<SqlDiagnosticSpan>
            raise (
                SqlCompilationException(
                    detail,
                    SqlDiagnostic(
                        "SQL_COMPILER_RENDERED_OUTPUT_LIMIT",
                        SqlDiagnosticStage.RenderingInvariant,
                        SqlDiagnosticCategory.Resource,
                        detail,
                        location)))
        { Sql = emitDocument ctx sql; Parameters = ctx.Parameters; ReturnsRows = returnsRows }
