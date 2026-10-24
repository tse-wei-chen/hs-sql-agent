namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Type-family validation is opt-in and deliberately conservative.
/// Unknown / unmodeled expression types do not produce speculative type proofs.
/// Name resolution is always performed by RewriteSchemaValidation first.
module internal RewriteSchemaTypeValidation =

    type private Source =
        { Qualifiers: string list
          Kinds: Map<string, SqlSchemaValueKind>
          Columns: string list }

    type private Scope =
        { Parent: Scope option
          Sources: Source list
          Unqualified: (string * SqlSchemaValueKind option) list }

    type private Catalog =
        { Dialect: SqlAgentToolType
          Tables: Map<string, (string * SqlSchemaValueKind) list> }

    let private keyPart dialect (part: IdentifierPart) =
        match dialect with
        | SqlAgentToolType.Postgres when not part.WasQuoted -> part.Value.ToLowerInvariant()
        | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird when not part.WasQuoted ->
            part.Value.ToUpperInvariant()
        | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Sqlite ->
            part.Value.ToUpperInvariant()
        | _ -> part.Value

    let private catalogPart dialect (value: string) =
        RewriteResourceBudget.charge (max 1L (int64 value.Length))
        match dialect with
        | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Sqlite ->
            value.ToUpperInvariant()
        | _ -> value

    let private keyIdentifier dialect identifier =
        Identifier.parts identifier
        |> List.map (keyPart dialect)
        |> String.concat "."

    let private fail span message =
        let diagnosticSpan =
            if Span.isKnown span then SqlDiagnosticSpan(span.Start, span.Length)
            else Unchecked.defaultof<SqlDiagnosticSpan>
        let diagnostic =
            SqlDiagnostic(
                "SQL_SCHEMA_TYPE_MISMATCH",
                SqlDiagnosticStage.SemanticValidation,
                SqlDiagnosticCategory.Semantic,
                message,
                diagnosticSpan)
        raise (SqlCompilationException(message, diagnostic))

    let private catalog dialect (snapshot: SqlSchemaSnapshot) =
        let tables =
            snapshot.Tables
            |> Seq.map (fun table ->
                RewriteResourceBudget.charge (max 1L (int64 table.Name.Length))
                table.Name.Split('.')
                |> Array.map (catalogPart dialect)
                |> String.concat ".",
                (table.TypedColumns
                 |> Seq.map (fun column ->
                     catalogPart dialect column.Name, column.Kind)
                 |> Seq.toList))
            |> Map.ofSeq
        { Dialect = dialect; Tables = tables }

    let private sourceKey catalog name alias =
        let full = keyIdentifier catalog.Dialect name
        let tail = Identifier.parts name |> List.last |> keyPart catalog.Dialect
        match alias with
        | Some part -> [ keyPart catalog.Dialect part ]
        | None when full = tail -> [ full ]
        | None -> [ full; tail ]

    let private columnsSource qualifiers columns =
        let names = columns |> List.map fst
        let kinds =
            if (List.distinct names).Length <> names.Length then Map.empty
            else
                columns
                |> List.map (fun (name, kind) -> name, Option.defaultValue SqlSchemaValueKind.Unknown kind)
                |> Map.ofList
        { Qualifiers = qualifiers; Kinds = kinds; Columns = names }

    let private namedSource catalog name alias =
        let columns =
            Map.tryFind (keyIdentifier catalog.Dialect name) catalog.Tables
            |> Option.defaultValue []
            |> List.map (fun (name, kind) -> name, Some kind)
        columnsSource (sourceKey catalog name alias) columns

    let private sourceAlias catalog alias columns =
        columnsSource [ keyPart catalog.Dialect alias ] columns

    let private cteSource catalog name alias ctes =
        columnsSource (sourceKey catalog name alias)
            (Map.tryFind (keyIdentifier catalog.Dialect name) ctes |> Option.defaultValue [])

    let private typeKnown kind =
        match kind with
        | SqlSchemaValueKind.Unknown -> None
        | value -> Some value

    let private sourceOutput (source: Source) =
        source.Columns
        |> List.map (fun name ->
            RewriteResourceBudget.checkpoint ()
            name, (Map.tryFind name source.Kinds |> Option.bind typeKnown))

    let private makeScope parent sources =
        { Parent = parent
          Sources = sources
          Unqualified = sources |> List.collect sourceOutput }

    let private kindInScope scope qualifier column =
        let mutable current = Some scope
        let mutable doneWalking = false
        let mutable result = None
        while current.IsSome && not doneWalking do
            RewriteResourceBudget.checkpoint ()
            let active = Option.get current
            let sources =
                match qualifier with
                | None -> active.Sources
                | Some value ->
                    active.Sources
                    |> List.filter (fun source ->
                        RewriteResourceBudget.checkpoint ()
                        source.Qualifiers |> List.exists (fun name ->
                            RewriteResourceBudget.checkpoint ()
                            name = value))
            let hits =
                match qualifier with
                | None ->
                    active.Unqualified
                    |> List.choose (fun (name, kind) ->
                        RewriteResourceBudget.checkpoint ()
                        if name = column then Some kind else None)
                | Some _ ->
                    sources
                    |> List.choose (fun source ->
                        RewriteResourceBudget.checkpoint ()
                        Map.tryFind column source.Kinds |> Option.map typeKnown)
            match hits with
            | [ found ] ->
                result <- found
                doneWalking <- true
            | [] ->
                match active.Parent with
                | Some parent when qualifier.IsNone || sources.IsEmpty ->
                    current <- Some parent
                | _ -> doneWalking <- true
            | _ ->
                // Name binding rejects ambiguity; no speculative type proof.
                doneWalking <- true
        result

    let private columnKind catalog scope identifier =
        let parts = Identifier.parts identifier
        let column = parts |> List.last |> keyPart catalog.Dialect
        let qualifier =
            if parts.Length = 1 then None
            else
                parts
                |> List.take (parts.Length - 1)
                |> List.map (keyPart catalog.Dialect)
                |> String.concat "."
                |> Some
        kindInScope scope qualifier column

    let private literalKind scalar =
        match scalar with
        | ScalarValue.Null -> None
        | ScalarValue.Boolean _ -> Some SqlSchemaValueKind.Boolean
        | ScalarValue.Integer _ | ScalarValue.Decimal _ | ScalarValue.Floating _ ->
            Some SqlSchemaValueKind.Numeric
        | ScalarValue.Text _ -> Some SqlSchemaValueKind.Text
        | ScalarValue.Date _ | ScalarValue.Time _ | ScalarValue.LocalDateTime _
        | ScalarValue.OffsetDateTime _ | ScalarValue.Duration _ ->
            Some SqlSchemaValueKind.Temporal
        | ScalarValue.Bytes _ -> Some SqlSchemaValueKind.Binary

    let private requireKind span expected actual operation =
        match actual with
        | Some value when value <> expected ->
            fail span (
                operation + " requires " + string expected +
                ", but the schema proves " + string value + ".")
        | _ -> ()

    let private compatible span left right operation =
        match left, right with
        | Some a, Some b when a <> b ->
            fail span (
                operation + " has incompatible proven SQL type families: " +
                string a + " and " + string b + ". Use an explicit CAST if intended.")
        | _ -> ()

    let private bothNumeric left right =
        match left, right with
        | Some SqlSchemaValueKind.Numeric, Some SqlSchemaValueKind.Numeric ->
            Some SqlSchemaValueKind.Numeric
        | _ -> None


    // A linear correlated WHERE EXISTS graph does not require a CLR
    // Query→Expr→Query callback. Only select shapes whose other expressions
    // are terminal participate; everything else retains full validation.
    let private tryLinearExists (query: Query) =
        let terminalProjection =
            query.Head.Projection |> List.forall (fun item ->
                match Expr.unspan item.Expression with
                | Literal _ | Column _ | BoundColumn _ | Wildcard _
                | OrderOrdinal _ | Interval _ -> true
                | _ -> false)
        if query.Head.Ctes.IsEmpty && query.SetOperations.IsEmpty &&
           query.Head.Joins.IsEmpty && query.Head.GroupBy.IsEmpty &&
           query.Head.Having.IsNone && query.OrderBy.IsEmpty &&
           query.Head.DistinctMode = SelectDistinct.AllRows && terminalProjection then
            match query.Head.From, query.Head.Where |> Option.map Expr.unspan with
            | (None | Some(NamedTable _ | CteTable _)), Some(Exists(inner, _)) -> Some inner
            | _ -> None
        else None


    // Match only the terminal correlated IN shape whose left operand can
    // be checked before suspending into the child query.
    let private tryLinearIn (query: Query) =
        let terminalProjection =
            query.Head.Projection |> List.forall (fun item ->
                match Expr.unspan item.Expression with
                | Literal _ | Column _ | BoundColumn _ | Wildcard _
                | OrderOrdinal _ | Interval _ -> true
                | _ -> false)
        if query.Head.Ctes.IsEmpty && query.SetOperations.IsEmpty &&
           query.Head.Joins.IsEmpty && query.Head.GroupBy.IsEmpty &&
           query.Head.Having.IsNone && query.OrderBy.IsEmpty &&
           query.Head.DistinctMode = SelectDistinct.AllRows && terminalProjection then
            match query.Head.From, query.Head.Where |> Option.map Expr.unspan with
            | Some (NamedTable _ | CteTable _), Some(InSubquery(value, inner, false)) ->
                match Expr.unspan value with
                | Column _ | BoundColumn _ -> Some(value, inner)
                | _ -> None
            | _ -> None
        else None


    // A projection consisting solely of a scalar Query can be resumed
    // after its inner Query proof. No row source or other expression is
    // skipped; other projection shapes retain the full proof traversal.
    let private tryLinearScalarProjection (query: Query) =
        let head = query.Head
        if head.Ctes.IsEmpty && query.SetOperations.IsEmpty &&
           head.DistinctMode = SelectDistinct.AllRows &&
           head.From.IsNone && head.Joins.IsEmpty &&
           head.Where.IsNone && head.GroupBy.IsEmpty && head.Having.IsNone &&
           query.OrderBy.IsEmpty && query.Limit.IsNone && query.Offset.IsNone &&
           query.FetchPercent.IsNone && not query.FetchWithTies then
            match head.Projection with
            | [ { Alias = None; Expression = projection } ] ->
                match Expr.unspan projection with
                | ScalarSubquery child -> Some child
                | _ -> None
            | _ -> None
        else None


    // Only one CASE branch with one suspended scalar Query participates.
    // The condition and fallback remain fully validated and type-checked.
    let private tryLinearCaseProjection (query: Query) =
        let head = query.Head
        if head.Ctes.IsEmpty && query.SetOperations.IsEmpty &&
           head.DistinctMode = SelectDistinct.AllRows &&
           head.From.IsNone && head.Joins.IsEmpty &&
           head.Where.IsNone && head.GroupBy.IsEmpty && head.Having.IsNone &&
           query.OrderBy.IsEmpty && query.Limit.IsNone && query.Offset.IsNone &&
           query.FetchPercent.IsNone && not query.FetchWithTies then
            match head.Projection with
            | [ { Alias = None; Expression = projection } ] ->
                match Expr.unspan projection with
                | SearchedCase(branches, Some fallback) ->
                    match NonEmpty.toList branches, Expr.unspan fallback with
                    | [ branch ], Literal _ ->
                        match Expr.unspan branch.Result, Expr.unspan branch.Condition with
                        | ScalarSubquery inner, Binary _ ->
                            Some(branch.Condition, fallback, inner)
                        | _ -> None
                    | _ -> None
                | _ -> None
            | _ -> None
        else None

    // The common unary/Boolean/arithmetic/type-comparison graph uses heap
    // continuations. Complex CASE/aggregate/subquery clauses retain their
    // existing provider-aware evaluator until the full Query graph migrates.
    type private TypedScalarFrame =
        | EnterTyped of Span * Expr
        | FinishTypedUnary of Span * UnaryOperator
        | FinishTypedBinary of Span * BinaryOperator

    type private TypedQueryFrame =
        | ResumeTypedCte of Map<string, (string * SqlSchemaValueKind option) list> * Scope option * Query * Cte
        | ResumeTypedDerived of Map<string, (string * SqlSchemaValueKind option) list> * Scope option * Query * IdentifierPart
        | ResumeTypedJoinedLateral of Map<string, (string * SqlSchemaValueKind option) list> * Scope option * Query * IdentifierPart * int
        | ResumeTypedExists of Map<string, (string * SqlSchemaValueKind option) list> * Scope option * Query
        | ResumeTypedIn of Map<string, (string * SqlSchemaValueKind option) list> * Scope option * Query * SqlSchemaValueKind option * Span
        | ResumeTypedScalarProjection
        | ResumeTypedCaseProjection of Map<string, (string * SqlSchemaValueKind option) list> * Scope option * Expr

    type private ProofResult =
        | ExpressionKind of SqlSchemaValueKind option
        | ProjectionKinds of (string option * SqlSchemaValueKind option) list

    let private createValidation () =
        let evaluator = RewriteScopedContinuations.Evaluator<ProofResult>()
        let rec expression catalog ctes scope span expr =
            match evaluator.Evaluate((fun () ->
                ExpressionKind(expressionCore catalog ctes scope span expr)), true) with
            | ExpressionKind kind -> kind
            | ProjectionKinds _ -> invalidOp "Scalar type continuation returned a projection."

        and expressionCore catalog ctes scope span expr =
            RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation
            let evaluate = expression catalog ctes scope Span.unknown
            match expr with
            | Spanned(current, value) -> expression catalog ctes scope current value
            | Unary _ | Binary _ -> evaluateTypedScalar catalog ctes scope span expr
            | BoundColumn(_, ColumnBinding.ProjectionAlias) -> None
            | BoundColumn(identifier, ColumnBinding.OuterRowSource) ->
                match scope.Parent with
                | Some parent -> columnKind catalog parent identifier
                | None -> None
            | Column identifier | BoundColumn(identifier, _) ->
                columnKind catalog scope identifier
            | Literal literal -> literalKind literal
            | Wildcard _ | OrderOrdinal _ | Interval _ -> None
            | Unary(UnaryOperator.Not, value) ->
                requireKind span SqlSchemaValueKind.Boolean (evaluate value) "NOT"
                Some SqlSchemaValueKind.Boolean
            | Unary((UnaryOperator.Negate | UnaryOperator.Positive), value) ->
                let result = evaluate value
                requireKind span SqlSchemaValueKind.Numeric result "Unary arithmetic"
                result
            | Binary(op, left, right) ->
                let leftKind = evaluate left
                let rightKind = evaluate right
                match op with
                | BinaryOperator.Add | BinaryOperator.Subtract | BinaryOperator.Multiply
                | BinaryOperator.Divide | BinaryOperator.Modulo ->
                    requireKind span SqlSchemaValueKind.Numeric leftKind "Arithmetic"
                    requireKind span SqlSchemaValueKind.Numeric rightKind "Arithmetic"
                    bothNumeric leftKind rightKind
                | BinaryOperator.Equal | BinaryOperator.NotEqual
                | BinaryOperator.GreaterThan | BinaryOperator.LessThan
                | BinaryOperator.GreaterThanOrEqual | BinaryOperator.LessThanOrEqual
                | BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom ->
                    compatible span leftKind rightKind "Comparison"
                    Some SqlSchemaValueKind.Boolean
                | BinaryOperator.And | BinaryOperator.Or ->
                    requireKind span SqlSchemaValueKind.Boolean leftKind "Boolean operator"
                    requireKind span SqlSchemaValueKind.Boolean rightKind "Boolean operator"
                    Some SqlSchemaValueKind.Boolean
                | BinaryOperator.Concat -> None // Target-side coercion is provider-specific.
            | Like(value, pattern, _, _, _) ->
                requireKind span SqlSchemaValueKind.Text (evaluate value) "LIKE"
                requireKind span SqlSchemaValueKind.Text (evaluate pattern) "LIKE"
                Some SqlSchemaValueKind.Boolean
            | InList(value, values, _) ->
                let left = evaluate value
                values |> NonEmpty.iter (fun item ->
                    compatible span left (evaluate item) "IN")
                Some SqlSchemaValueKind.Boolean
            | Between(value, lower, upper, _) ->
                let left = evaluate value
                compatible span left (evaluate lower) "BETWEEN"
                compatible span left (evaluate upper) "BETWEEN"
                Some SqlSchemaValueKind.Boolean
            | IsNull(value, _) ->
                evaluate value |> ignore
                Some SqlSchemaValueKind.Boolean
            | Exists(query, _) ->
                queryTypes catalog ctes (Some scope) query |> ignore
                Some SqlSchemaValueKind.Boolean
            | InSubquery(value, query, _) ->
                let left = evaluate value
                match queryTypes catalog ctes (Some scope) query with
                | [ (_, right) ] -> compatible span left right "IN subquery"
                | _ -> ()
                Some SqlSchemaValueKind.Boolean
            | ScalarSubquery query ->
                match queryTypes catalog ctes (Some scope) query with
                | [ (_, projected) ] -> projected
                | _ -> None
            | RegexMatch(a, b) ->
                evaluate a |> ignore
                evaluate b |> ignore
                Some SqlSchemaValueKind.Boolean
            | RawRegexCall(args, _) ->
                args |> List.iter (fun item -> evaluate item |> ignore)
                None
            | FilteredAggregate(value, predicate) ->
                evaluate value |> ignore
                requireKind span SqlSchemaValueKind.Boolean (evaluate predicate) "Aggregate FILTER"
                None
            | DateAdd(_, amount, value) | DateDiff(_, amount, value) ->
                evaluate amount |> ignore
                evaluate value |> ignore
                None
            | Cast(value, _) | Extract(_, value)
            | PostgresJsonAccess(value, _, _) ->
                evaluate value |> ignore
                None
            | FunctionCall call ->
                call.Arguments |> List.iter (fun item -> evaluate item |> ignore)
                call.AggregateOrderBy |> List.iter (fun item -> evaluate item.Expression |> ignore)
                None
            | Windowed(value, window) ->
                evaluate value |> ignore
                window.PartitionBy |> List.iter (fun item -> evaluate item |> ignore)
                window.OrderBy |> List.iter (fun item -> evaluate item.Expression |> ignore)
                None
            | SimpleCase(value, branches, fallback) ->
                evaluate value |> ignore
                branches |> NonEmpty.iter (fun item ->
                    evaluate item.Match |> ignore
                    evaluate item.Result |> ignore)
                fallback |> Option.iter (fun item -> evaluate item |> ignore)
                None
            | SearchedCase(branches, fallback) ->
                branches |> NonEmpty.iter (fun item ->
                    requireKind span SqlSchemaValueKind.Boolean (evaluate item.Condition) "CASE WHEN"
                    evaluate item.Result |> ignore)
                fallback |> Option.iter (fun item -> evaluate item |> ignore)
                None

        // Terminal expressions use the original conservative type evaluator.
        // Spanned children inherit their precise source location, and all
        // arithmetic/Boolean reductions happen after left then right evaluation.
        and evaluateTypedScalar catalog ctes scope span root =
            let pending = ResizeArray<TypedScalarFrame>()
            let results = ResizeArray<SqlSchemaValueKind option>()
            let inline enter currentSpan expr = pending.Add(EnterTyped(currentSpan, expr))
            let pop () =
                let index = results.Count - 1
                let value = results.[index]
                results.RemoveAt index
                value
            enter span root
            while pending.Count > 0 do
                RewriteResourceBudget.checkpoint ()
                let index = pending.Count - 1
                let frame = pending.[index]
                pending.RemoveAt index
                match frame with
                | EnterTyped(currentSpan, Spanned(nextSpan, inner)) ->
                    enter nextSpan inner
                | EnterTyped(currentSpan, Unary(op, operand)) ->
                    pending.Add(FinishTypedUnary(currentSpan, op))
                    enter Span.unknown operand
                | EnterTyped(currentSpan, Binary(op, left, right)) ->
                    pending.Add(FinishTypedBinary(currentSpan, op))
                    enter Span.unknown right
                    enter Span.unknown left
                | EnterTyped(currentSpan, terminal) ->
                    results.Add(expression catalog ctes scope currentSpan terminal)
                | FinishTypedUnary(currentSpan, op) ->
                    let result = pop ()
                    match op with
                    | UnaryOperator.Not ->
                        requireKind currentSpan SqlSchemaValueKind.Boolean result "NOT"
                        results.Add(Some SqlSchemaValueKind.Boolean)
                    | UnaryOperator.Negate | UnaryOperator.Positive ->
                        requireKind currentSpan SqlSchemaValueKind.Numeric result "Unary arithmetic"
                        results.Add(result)
                | FinishTypedBinary(currentSpan, op) ->
                    let rightKind = pop ()
                    let leftKind = pop ()
                    let result =
                        match op with
                        | BinaryOperator.Add | BinaryOperator.Subtract
                        | BinaryOperator.Multiply | BinaryOperator.Divide | BinaryOperator.Modulo ->
                            requireKind currentSpan SqlSchemaValueKind.Numeric leftKind "Arithmetic"
                            requireKind currentSpan SqlSchemaValueKind.Numeric rightKind "Arithmetic"
                            bothNumeric leftKind rightKind
                        | BinaryOperator.Equal | BinaryOperator.NotEqual
                        | BinaryOperator.GreaterThan | BinaryOperator.LessThan
                        | BinaryOperator.GreaterThanOrEqual | BinaryOperator.LessThanOrEqual
                        | BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom ->
                            compatible currentSpan leftKind rightKind "Comparison"
                            Some SqlSchemaValueKind.Boolean
                        | BinaryOperator.And | BinaryOperator.Or ->
                            requireKind currentSpan SqlSchemaValueKind.Boolean leftKind "Boolean operator"
                            requireKind currentSpan SqlSchemaValueKind.Boolean rightKind "Boolean operator"
                            Some SqlSchemaValueKind.Boolean
                        | BinaryOperator.Concat -> None
                    results.Add(result)
            if results.Count <> 1 then
                invalidOp "Typed scalar evaluation did not return exactly one SQL type proof."
            results.[0]

        and tableSource catalog ctes parent table =
            match table with
            | NamedTable(name, alias) -> namedSource catalog name alias
            | CteTable(name, alias) -> cteSource catalog name alias ctes
            | DerivedTable(query, alias) ->
                queryTypes catalog ctes None query
                |> List.choose (fun (name, kind) ->
                    name |> Option.map (fun value -> value, kind))
                |> sourceAlias catalog alias
            | LateralDerivedTable(query, alias) ->
                queryTypes catalog ctes parent query
                |> List.choose (fun (name, kind) ->
                    name |> Option.map (fun value -> value, kind))
                |> sourceAlias catalog alias

        // Preserve exact inner-to-outer type proof order for linear CTE chains.
        // Every continuation restores its own inherited CTE visibility and
        // correlation parent. Other mixed Query/Expr/Source occurrences
        // suspend through the enclosing scope-aware evaluator.
        and queryTypes catalog inheritedCtes parent (query: Query) =
            let action () = ProjectionKinds(queryTypesCore catalog inheritedCtes parent query)
            let result =
                if RewriteAstTraversal.isLocalQuery query then
                    evaluator.EvaluateLocal action
                else evaluator.Evaluate(action, false)
            match result with
            | ProjectionKinds projection -> projection
            | ExpressionKind _ -> invalidOp "Query type continuation returned a scalar."

        and queryTypesCore catalog inheritedCtes parent (query: Query) =
            RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation
            let pending = ResizeArray<TypedQueryFrame>()
            let mutable current = query
            let mutable currentParent = parent
            let mutable scanning = true
            while scanning do
                RewriteResourceBudget.checkpoint ()
                match current.Head.Ctes, current.SetOperations, tryLinearExists current, tryLinearIn current, tryLinearScalarProjection current, tryLinearCaseProjection current with
                | [], [], Some inner, _, _, _ ->
                    // Type validation inspects WHERE before projection. Defer
                    // the outer projection until the inner EXISTS proof returns.
                    let fromSources =
                        match current.Head.From with
                        | Some table -> [ tableSource catalog inheritedCtes currentParent table ]
                        | None -> []
                    let scope = makeScope currentParent fromSources
                    pending.Add(ResumeTypedExists(inheritedCtes, currentParent, current))
                    currentParent <- Some scope
                    current <- inner
                | [], [], _, Some(value, inner), _, _ ->
                    let fromSources =
                        match current.Head.From with
                        | Some table -> [ tableSource catalog inheritedCtes currentParent table ]
                        | None -> []
                    let scope = makeScope currentParent fromSources
                    let leftKind = expression catalog inheritedCtes scope Span.unknown value
                    let whereSpan = current.Head.Where |> Option.map Expr.span |> Option.defaultValue Span.unknown
                    pending.Add(ResumeTypedIn(inheritedCtes, currentParent, current, leftKind, whereSpan))
                    currentParent <- Some scope
                    current <- inner
                | [], [], _, _, Some inner, _ ->
                    pending.Add ResumeTypedScalarProjection
                    currentParent <- Some(makeScope currentParent [])
                    current <- inner
                | [], [], _, _, _, Some(condition, fallback, inner) ->
                    let scope = makeScope currentParent []
                    requireKind Span.unknown SqlSchemaValueKind.Boolean
                        (expression catalog inheritedCtes scope Span.unknown condition) "CASE WHEN"
                    pending.Add(ResumeTypedCaseProjection(inheritedCtes, currentParent, fallback))
                    currentParent <- Some scope
                    current <- inner
                | [ cte ], [], _, _, _, _ when not cte.RecursiveScope ->
                    pending.Add(ResumeTypedCte(inheritedCtes, currentParent, current, cte))
                    current <- cte.Query
                    currentParent <- None
                | [], [], _, _, _, _ when not current.Head.Joins.IsEmpty ->
                    match current.Head.From, List.rev current.Head.Joins with
                    | Some ((NamedTable _ | CteTable _) as first),
                      OnJoin(OnJoinKind.Inner, LateralDerivedTable(inner, alias), _) :: precedingReversed
                        when precedingReversed |> List.forall (function
                            | OnJoin(OnJoinKind.Inner, (NamedTable _ | CteTable _ | LateralDerivedTable _), _) -> true
                            | _ -> false) ->
                        let mutable visible =
                            makeScope currentParent [ tableSource catalog inheritedCtes currentParent first ]
                        for preceding in List.rev precedingReversed do
                            RewriteResourceBudget.checkpoint ()
                            match preceding with
                            | OnJoin(_, table, predicate) ->
                                let sourceParent =
                                    match table with
                                    | LateralDerivedTable _ -> Some visible
                                    | _ -> currentParent
                                let added = tableSource catalog inheritedCtes sourceParent table
                                let combined =
                                    { visible with
                                        Sources = visible.Sources @ [ added ]
                                        Unqualified = visible.Unqualified @ sourceOutput added }
                                expression catalog inheritedCtes combined Span.unknown predicate |> ignore
                                visible <- combined
                            | _ -> invalidOp "Proven typed JOIN prefix requires an INNER JOIN ON predicate."
                        let lastIndex = current.Head.Joins.Length - 1
                        pending.Add(ResumeTypedJoinedLateral(inheritedCtes, currentParent, current, alias, lastIndex))
                        currentParent <- Some visible
                        current <- inner
                    | _ -> scanning <- false
                | [], [], _, _, _, _ when current.Head.Joins.IsEmpty ->
                    match current.Head.From with
                    | Some (DerivedTable(inner, alias)) ->
                        pending.Add(ResumeTypedDerived(inheritedCtes, currentParent, current, alias))
                        current <- inner
                        currentParent <- None
                    | Some (LateralDerivedTable(inner, alias)) ->
                        // FROM-first LATERAL inherits the existing outer
                        // correlation scope, never a fabricated local row.
                        // Joined LATERALs stay in the scoped full proof path.
                        pending.Add(ResumeTypedDerived(inheritedCtes, currentParent, current, alias))
                        current <- inner
                    | _ -> scanning <- false
                | _ -> scanning <- false

            let mutable projected = queryTypesOne catalog inheritedCtes currentParent current
            for index = pending.Count - 1 downto 0 do
                RewriteResourceBudget.checkpoint ()
                match pending.[index] with
                | ResumeTypedCte(inherited, originalParent, outer, cte) ->
                    let names =
                        if cte.ColumnAliases.IsEmpty then projected |> List.map fst
                        else cte.ColumnAliases
                             |> List.map (fun alias -> Some(keyPart catalog.Dialect alias))
                    let typed =
                        List.zip names (projected |> List.map snd)
                        |> List.choose (fun (name, kind) ->
                            name |> Option.map (fun value -> value, kind))
                    let visible = Map.add (keyPart catalog.Dialect cte.Name) typed inherited
                    let stripped = { outer with Head = { outer.Head with Ctes = [] } }
                    projected <- queryTypesOne catalog visible originalParent stripped
                | ResumeTypedDerived(inherited, originalParent, outer, alias) ->
                    let columns =
                        projected |> List.choose (fun (name, kind) ->
                            name |> Option.map (fun value -> value, kind))
                    let source = sourceAlias catalog alias columns
                    let _, result =
                        queryTypesHeadWithFirst catalog inherited originalParent
                            outer (Some source)
                    projected <- result
                | ResumeTypedJoinedLateral(inherited, originalParent, outer, alias, joinIndex) ->
                    let columns =
                        projected |> List.choose (fun (name, kind) ->
                            name |> Option.map (fun value -> value, kind))
                    let source = sourceAlias catalog alias columns
                    let _, result =
                        queryTypesHeadWithJoinOverride catalog inherited originalParent
                            outer None (Some(joinIndex, source))
                    projected <- result
                | ResumeTypedExists(inherited, originalParent, outer) ->
                    let withoutWhere =
                        { outer with Head = { outer.Head with Where = None } }
                    projected <- queryTypesOne catalog inherited originalParent withoutWhere
                | ResumeTypedIn(inherited, originalParent, outer, leftKind, whereSpan) ->
                    match projected with
                    | [ (_, rightKind) ] -> compatible whereSpan leftKind rightKind "IN subquery"
                    | _ -> ()
                    let withoutWhere =
                        { outer with Head = { outer.Head with Where = None } }
                    projected <- queryTypesOne catalog inherited originalParent withoutWhere
                | ResumeTypedScalarProjection ->
                    projected <-
                        match projected with
                        | [ (_, kind) ] -> [ None, kind ]
                        | _ -> [ None, None ]
                | ResumeTypedCaseProjection(inherited, originalParent, fallback) ->
                    expression catalog inherited (makeScope originalParent []) Span.unknown fallback |> ignore
                    projected <- [ None, None ]
            projected

        and queryTypesOne catalog inheritedCtes parent (query: Query) =
            RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation
            let visibleCtes, projection = queryTypesHead catalog inheritedCtes parent query
            let pending = ResizeArray<unit -> unit>()
            let result = ref projection

            // Every child query needs its own type-intersection accumulator:
            // evaluate its entire nested SET subtree before comparing the
            // result with its parent's current output family, exactly as before.
            let rec scheduleBranches visible parentScope parentOutput branches =
                for branch in List.rev branches do
                    pending.Add(fun () ->
                        let child = branch.Query
                        let childCtes, childProjection =
                            queryTypesHead catalog visible parentScope child
                        let childOutput = ref childProjection
                        pending.Add(fun () ->
                            let left = !parentOutput
                            let right = !childOutput
                            if List.length left = List.length right then
                                parentOutput :=
                                    List.map2 (fun (name, leftKind) (_, rightKind) ->
                                        name, (if leftKind = rightKind then leftKind else None))
                                        left right)
                        scheduleBranches childCtes parentScope childOutput child.SetOperations)

            scheduleBranches visibleCtes parent result query.SetOperations
            while pending.Count > 0 do
                RewriteResourceBudget.checkpoint ()
                let last = pending.Count - 1
                let next = pending.[last]
                pending.RemoveAt(last)
                next()
            !result

        and queryTypesHead catalog inheritedCtes parent (query: Query) =
            queryTypesHeadWithFirst catalog inheritedCtes parent query None

        and queryTypesHeadWithFirst catalog inheritedCtes parent (query: Query) firstOverride =
            queryTypesHeadWithJoinOverride catalog inheritedCtes parent query firstOverride None

        and queryTypesHeadWithJoinOverride catalog inheritedCtes parent (query: Query) firstOverride joinOverride =
            RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation
            let mutable ctes = inheritedCtes
            // Sequential visibility follows the already validated CTE lexical scope.
            for cte in query.Head.Ctes do
                let output = queryTypes catalog ctes None cte.Query
                let names =
                    if cte.ColumnAliases.IsEmpty then output |> List.map fst
                    else cte.ColumnAliases |> List.map (fun alias -> Some(keyPart catalog.Dialect alias))
                let typed =
                    List.zip names (output |> List.map snd)
                    |> List.choose (fun (name, kind) -> name |> Option.map (fun value -> value, kind))
                ctes <- Map.add (keyPart catalog.Dialect cte.Name) typed ctes

            let initial =
                match firstOverride with
                | Some resolved -> makeScope parent [ resolved ]
                | None ->
                    match query.Head.From with
                    | None -> makeScope parent []
                    | Some table -> makeScope parent [ tableSource catalog ctes parent table ]
            let mutable scope = initial
            let mutable joinIndex = 0
            for join in query.Head.Joins do
                let added =
                    match joinOverride with
                    | Some (targetIndex, resolved) when targetIndex = joinIndex -> resolved
                    | _ ->
                        let sourceParent =
                            match join.Source with
                            | LateralDerivedTable _ -> Some scope
                            | _ -> scope.Parent
                        tableSource catalog ctes sourceParent join.Source
                joinIndex <- joinIndex + 1
                let left = scope.Unqualified
                let right = sourceOutput added
                let mergeKeys =
                    match join with
                    | UsingJoin(_, _, columns) ->
                        Some(columns |> NonEmpty.toList |> List.map (keyPart catalog.Dialect))
                    | NaturalJoin _ ->
                        let rightNames = right |> List.map fst |> Set.ofList
                        Some(left |> List.map fst |> List.filter rightNames.Contains)
                    | _ -> None
                let unqualified =
                    match mergeKeys with
                    | None -> left @ right
                    | Some keys ->
                        let isMerged name = List.contains name keys
                        let merged =
                            keys |> List.map (fun key ->
                                let leftKind =
                                    left |> List.filter (fun (name, _) -> name = key)
                                let rightKind =
                                    right |> List.filter (fun (name, _) -> name = key)
                                let proof =
                                    match leftKind, rightKind with
                                    | [ (_, a) ], [ (_, b) ] when a = b -> a
                                    | _ -> None
                                key, proof)
                        merged
                        @ (left |> List.filter (fun (name, _) -> not (isMerged name)))
                        @ (right |> List.filter (fun (name, _) -> not (isMerged name)))
                let combined =
                    { scope with
                        Sources = scope.Sources @ [ added ]
                        Unqualified = unqualified }
                match join with
                | OnJoin(_, _, predicate) ->
                    expression catalog ctes combined Span.unknown predicate |> ignore
                | _ -> ()
                scope <- combined

            let evaluate = expression catalog ctes scope Span.unknown
            let inspect value = evaluate value |> ignore
            match query.Head.DistinctMode with
            | SelectDistinct.DistinctOn items -> items |> NonEmpty.iter inspect
            | _ -> ()
            query.Head.Where |> Option.iter inspect
            query.Head.GroupBy |> List.iter inspect
            query.Head.Having |> Option.iter inspect
            query.OrderBy |> List.iter (fun item -> inspect item.Expression)

            let projection =
                query.Head.Projection
                |> List.collect (fun item ->
                    match item.Alias, Expr.unspan item.Expression with
                    | Some alias, _ ->
                        [ Some(keyPart catalog.Dialect alias), evaluate item.Expression ]
                    | None, Column identifier
                    | None, BoundColumn(identifier, _) ->
                        [ Some(Identifier.parts identifier |> List.last |> keyPart catalog.Dialect),
                          evaluate item.Expression ]
                    | None, Wildcard None ->
                        scope.Unqualified |> List.map (fun (name, kind) -> Some name, kind)
                    | None, Wildcard(Some identifier) ->
                        let qualifier = keyIdentifier catalog.Dialect identifier
                        scope.Sources
                        |> List.filter (fun source -> List.contains qualifier source.Qualifiers)
                        |> List.collect (fun source ->
                            source.Columns |> List.map (fun name ->
                                Some name, (Map.tryFind name source.Kinds |> Option.bind typeKnown)))
                    | _ -> [ None, evaluate item.Expression ])

            ctes, projection

        let document catalog (document: Document) =
            let root = makeScope None []
            let ctes = Map.empty<string, (string * SqlSchemaValueKind option) list>
            let inspect scope value = expression catalog ctes scope Span.unknown value |> ignore
            match document.Statement with
            | QueryStatement query -> queryTypes catalog ctes None query |> ignore
            | InsertStatement insert ->
                let target = namedSource catalog insert.Target None
                let columnTypes =
                    insert.Columns
                    |> List.map (fun item ->
                        Map.tryFind (keyPart catalog.Dialect item) target.Kinds
                        |> Option.bind typeKnown)
                match insert.Input with
                | Values rows ->
                    rows |> NonEmpty.iter (fun row ->
                        let values = NonEmpty.toList row
                        values |> List.iter (inspect root)
                        if values.Length = columnTypes.Length then
                            List.zip columnTypes values
                            |> List.iter (fun (targetKind, value) ->
                                compatible document.Span targetKind
                                    (expression catalog ctes root Span.unknown value) "INSERT assignment"))
                | QuerySource query ->
                    let projections = queryTypes catalog ctes None query |> List.map snd
                    // Implicit target-column order is not proven by the portable snapshot.
                    if not insert.Columns.IsEmpty && projections.Length = columnTypes.Length then
                        List.zip columnTypes projections
                        |> List.iter (fun (targetKind, projectedKind) ->
                            compatible document.Span targetKind projectedKind "INSERT SELECT assignment")
                | DefaultValues -> ()
                insert.Returning |> List.iter (fun item ->
                    inspect (makeScope None [ target ]) item.Expression)
            | UpdateStatement update ->
                let target = namedSource catalog update.Target update.TargetAlias
                let others = update.From |> List.map (tableSource catalog ctes None)
                let scope = makeScope None (target :: others)
                let targetScope = makeScope None [ target ]
                update.Assignments |> List.iter (fun item ->
                    compatible document.Span
                        (columnKind catalog targetScope item.Target)
                        (expression catalog ctes scope Span.unknown item.Value)
                        "UPDATE assignment")
                update.Where |> Option.iter (inspect scope)
                update.Returning |> List.iter (fun item -> inspect targetScope item.Expression)
            | DeleteStatement delete ->
                let target = namedSource catalog delete.Target delete.TargetAlias
                let scope =
                    makeScope None (target :: (delete.Using |> List.map (tableSource catalog ctes None)))
                delete.Where |> Option.iter (inspect scope)
                delete.Returning |> List.iter (fun item -> inspect (makeScope None [ target ]) item.Expression)
            | MergeStatement _ -> () // Name binding already rejects MERGE in strict snapshot mode.


        document

    let verify dialect snapshot (bound: BoundSql) =
        let catalog = catalog dialect snapshot
        (createValidation ()) catalog (Bound.value bound)
        bound
