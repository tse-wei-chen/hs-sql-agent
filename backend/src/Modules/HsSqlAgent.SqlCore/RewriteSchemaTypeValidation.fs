namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Catalog
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

    let private qualifiedKey parts =
        parts |> List.map (fun (part: string) ->
            part.Replace("\\", "\\\\").Replace(".", "\\.")) |> String.concat "."

    let private keyIdentifier dialect identifier =
        Identifier.parts identifier
        |> List.map (keyPart dialect)
        |> qualifiedKey

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
        | Some part -> [ qualifiedKey [keyPart catalog.Dialect part] ]
        | None when full = qualifiedKey [tail] -> [ full ]
        | None -> [ full; qualifiedKey [tail] ]

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
        columnsSource [ qualifiedKey [keyPart catalog.Dialect alias] ] columns

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

    // Local lexical provenance for type *family* evidence. The name binder
    // already rejects absent/ambiguous SQL columns before this validation
    // stage. Keeping the cases separate here prevents a shadowing untyped
    // column from inheriting the type of an outer typed column.
    //
    // These cases never become compiler typestate, authorization or an
    // exact SQL primitive type; their sole consumer projects ProvenKind to
    // the existing optional family result.
    type private ScopedFamilyEvidence =
        | MissingName
        | KnownNameUnknownFamily of scopeDepth: int
        | AmbiguousName of scopeDepth: int
        | ProvenKind of scopeDepth: int * kind: SqlSchemaValueKind

    let private kindInScope scope qualifier column =
        let mutable current = Some scope
        let mutable depth = 0
        let mutable result = MissingName
        let mutable doneWalking = false
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
            // All hits must be counted, even when the source only proves a
            // column name and its declared type family is Unknown. Unknown
            // never means the name was absent and never authorizes walking
            // outward to a coincidentally typed column.
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
            | [ Some kind ] ->
                result <- ProvenKind(depth,kind)
                doneWalking <- true
            | [ None ] ->
                result <- KnownNameUnknownFamily depth
                doneWalking <- true
            | _ :: _ ->
                result <- AmbiguousName depth
                doneWalking <- true
            | [] ->
                match active.Parent with
                | Some parent when qualifier.IsNone || sources.IsEmpty ->
                    current <- Some parent
                    depth <- depth + 1
                | _ ->
                    // A qualifier resolved in this scope cannot borrow a
                    // column from a different scope when its field is absent.
                    doneWalking <- true
        result

    let private columnEvidence catalog scope identifier =
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

    let private columnKind catalog scope identifier =
        match columnEvidence catalog scope identifier with
        | ProvenKind(_,kind) -> Some kind
        | KnownNameUnknownFamily _
        | AmbiguousName _
        | MissingName -> None

    // The type validator consumes ONLY the family projection. The separate
    // exact Function type proof is never manufactured from a family-only fact.
    let private literalKind scalar =
        RewriteScalarTypeEvidence.literalFamily scalar

    let private requireKind span expected actual operation =
        match actual with
        | Some value when value <> expected ->
            fail span (
                operation + " requires " + string expected +
                ", but the schema proves " + string value + ".")
        | _ -> ()

    /// Diagnostic provenance must stay on the failing value rather than
    /// the enclosing DML statement. Unknown AST spans use the statement as
    /// a conservative fallback, never fabricate character offsets.
    let private expressionSpanOr fallback value =
        let precise = Expr.span value
        if Span.isKnown precise && precise.Length > 0 then precise
        else fallback

    let private compatible span left right operation =
        match left, right with
        | Some a, Some b when a <> b ->
            fail span (
                operation + " has incompatible proven SQL type families: " +
                string a + " and " + string b + ". Use an explicit CAST if intended.")
        | _ -> ()

    // Diagnostic annotations describe only validated, *proven* type-family
    // origins; a missing, ambiguous or unknown type remains silent here and
    // is never converted into a fabricated mismatch. Do not include SQL
    // literal contents, catalog values or runtime query results in messages.
    let private columnOriginAt offset catalog scope identifier =
        match columnEvidence catalog scope identifier with
        | ProvenKind(depth,kind) ->
            Some(sprintf "proven column '%s' (%A, scope depth %d)"
                (Identifier.text identifier) kind (depth + offset))
        | _ -> None

    let private columnOrigin catalog scope identifier =
        columnOriginAt 0 catalog scope identifier

    let private expressionOrigin catalog scope expression =
        match Expr.unspan expression with
        | Column identifier | BoundColumn(identifier, ColumnBinding.LocalRowSource) ->
            columnOrigin catalog scope identifier
        | BoundColumn(identifier, ColumnBinding.OuterRowSource) ->
            match scope.Parent with
            | Some parent ->
                // The binder has already established this correlation.
                // Count the transition from the expression's local scope
                // before traversing any further outer lexical scopes.
                columnOriginAt 1 catalog parent identifier
            | None -> None
        | Literal scalar ->
            literalKind scalar
            |> Option.map (fun family -> sprintf "proven literal (%A family)" family)
        | _ -> None

    let private evidenceSuffix items =
        let proven = items |> List.choose id
        if List.isEmpty proven then ""
        else " Evidence: " + String.concat "; " proven + "."

    let private requireKindWith span expected actual operation origins =
        match actual with
        | Some value when value <> expected ->
            fail span (
                operation + " requires " + string expected +
                ", but the schema proves " + string value + "." +
                evidenceSuffix origins)
        | _ -> ()

    let private compatibleWith span left right operation origins =
        match left,right with
        | Some a,Some b when a <> b ->
            fail span (
                operation + " has incompatible proven SQL type families: " +
                string a + " and " + string b +
                ". Use an explicit CAST if intended." + evidenceSuffix origins)
        | _ -> ()

    let private knownKindMismatch expected actual =
        match actual with
        | Some family when family <> expected -> true
        | _ -> false

    let private knownFamilyMismatch left right =
        match left,right with
        | Some a,Some b when a <> b -> true
        | _ -> false

    let private targetOrigin (name: string) kind =
        kind
        |> Option.map (fun family ->
            sprintf "proven assignment target '%s' (%A family)"
                name family)

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
        | FinishTypedBinary of Span * BinaryOperator * Expr * Expr

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
                    pending.Add(FinishTypedBinary(currentSpan, op, left, right))
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
                | FinishTypedBinary(currentSpan, op, left, right) ->
                    let rightKind = pop ()
                    let leftKind = pop ()
                    let result =
                        match op with
                        | BinaryOperator.Add | BinaryOperator.Subtract
                        | BinaryOperator.Multiply | BinaryOperator.Divide | BinaryOperator.Modulo ->
                            if knownKindMismatch SqlSchemaValueKind.Numeric leftKind then
                                requireKindWith currentSpan SqlSchemaValueKind.Numeric leftKind
                                    "Arithmetic" [ expressionOrigin catalog scope left ]
                            if knownKindMismatch SqlSchemaValueKind.Numeric rightKind then
                                requireKindWith currentSpan SqlSchemaValueKind.Numeric rightKind
                                    "Arithmetic" [ expressionOrigin catalog scope right ]
                            bothNumeric leftKind rightKind
                        | BinaryOperator.Equal | BinaryOperator.NotEqual
                        | BinaryOperator.GreaterThan | BinaryOperator.LessThan
                        | BinaryOperator.GreaterThanOrEqual | BinaryOperator.LessThanOrEqual
                        | BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom ->
                            if knownFamilyMismatch leftKind rightKind then
                                compatibleWith currentSpan leftKind rightKind "Comparison"
                                    [ expressionOrigin catalog scope left
                                      expressionOrigin catalog scope right ]
                            Some SqlSchemaValueKind.Boolean
                        | BinaryOperator.And | BinaryOperator.Or ->
                            if knownKindMismatch SqlSchemaValueKind.Boolean leftKind then
                                requireKindWith currentSpan SqlSchemaValueKind.Boolean leftKind
                                    "Boolean operator" [ expressionOrigin catalog scope left ]
                            if knownKindMismatch SqlSchemaValueKind.Boolean rightKind then
                                requireKindWith currentSpan SqlSchemaValueKind.Boolean rightKind
                                    "Boolean operator" [ expressionOrigin catalog scope right ]
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
            let rec scheduleBranches visible parentScope
                (parentOutput: (string option * SqlSchemaValueKind option) list ref) branches =
                for branch in List.rev branches do
                    pending.Add(fun () ->
                        let child = branch.Query
                        let childCtes, childProjection =
                            queryTypesHead catalog visible parentScope child
                        let childOutput = ref childProjection
                        pending.Add(fun () ->
                            let left = parentOutput.Value
                            let right = childOutput.Value
                            if List.length left = List.length right then
                                parentOutput.Value <-
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
            result.Value

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
                            List.zip insert.Columns (List.zip columnTypes values)
                            |> List.iter (fun (target, (targetKind, value)) ->
                                let valueKind = expression catalog ctes root Span.unknown value
                                if knownFamilyMismatch targetKind valueKind then
                                    compatibleWith (expressionSpanOr document.Span value)
                                        targetKind valueKind "INSERT assignment"
                                        [ targetOrigin target.Value targetKind
                                          expressionOrigin catalog root value ]))
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
                    let targetKind = columnKind catalog targetScope item.Target
                    let valueKind = expression catalog ctes scope Span.unknown item.Value
                    if knownFamilyMismatch targetKind valueKind then
                        compatibleWith (expressionSpanOr document.Span item.Value)
                            targetKind valueKind "UPDATE assignment"
                            [ targetOrigin (Identifier.text item.Target) targetKind
                              expressionOrigin catalog scope item.Value ])
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

    /// Never promote partial/unknown Virtual Catalog facts to typed binding.
    /// This is a static copy of the already proven DDL column types, consumed
    /// by the existing AST expression compatibility checker.
    let verifyVirtual dialect (state: SqlCatalogState) (bound: BoundSql) =
        if not state.IsClosedWorld
           || (state.Tables |> Seq.exists (fun t -> t.Knowledge <> SqlCatalogKnowledge.Known)) then
            invalidArg "state" "Typed catalog binding requires a complete known closed-world catalog."
        let typeKind (name: string) =
            match name.ToUpperInvariant() with
            | "BOOLEAN" | "BIT" -> SqlSchemaValueKind.Boolean
            | "SMALLINT" | "INTEGER" | "INT" | "BIGINT"
            | "NUMBER" | "FLOAT" | "REAL" | "NUMERIC" -> SqlSchemaValueKind.Numeric
            | "TEXT" | "CHAR" | "VARCHAR" | "VARCHAR2"
            | "NVARCHAR" | "NVARCHAR2" | "NCHAR" -> SqlSchemaValueKind.Text
            | "DATE" | "TIMESTAMP" | "DATETIME" | "DATETIME2" ->
                SqlSchemaValueKind.Temporal
            | value when [ "CHAR("; "VARCHAR("; "NCHAR("; "NVARCHAR("; "VARCHAR2("; "NVARCHAR2(" ]
                         |> List.exists (fun prefix -> value.StartsWith(prefix, StringComparison.Ordinal)) ->
                SqlSchemaValueKind.Text
            | _ -> SqlSchemaValueKind.Unknown
        let tables =
            state.Tables
            |> Seq.map (fun table ->
                RewriteResourceBudget.checkpoint ()
                table.QualifiedName.BinderKey,
                (table.Columns
                 |> Seq.map (fun col -> catalogPart dialect col.Name, typeKind col.TypeName)
                 |> Seq.toList))
            |> Map.ofSeq
        let virtualCatalog : Catalog = { Dialect = dialect; Tables = tables }
        (createValidation ()) virtualCatalog (Bound.value bound)
        bound
