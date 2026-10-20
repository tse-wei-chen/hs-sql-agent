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
          Kinds: Map<string, SqlSchemaValueKind> }

    type private Scope =
        { Parent: Scope option
          Sources: Source list }

    type private Catalog =
        { Dialect: SqlAgentToolType
          Tables: Map<string, Map<string, SqlSchemaValueKind>> }

    let private keyPart dialect (part: IdentifierPart) =
        match dialect with
        | SqlAgentToolType.Postgres when not part.WasQuoted -> part.Value.ToLowerInvariant()
        | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird when not part.WasQuoted ->
            part.Value.ToUpperInvariant()
        | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Sqlite ->
            part.Value.ToUpperInvariant()
        | _ -> part.Value

    let private catalogPart dialect (value: string) =
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
                table.Name.Split('.')
                |> Array.map (catalogPart dialect)
                |> String.concat ".",
                (table.TypedColumns
                 |> Seq.map (fun column ->
                     catalogPart dialect column.Name, column.Kind)
                 |> Map.ofSeq))
            |> Map.ofSeq
        { Dialect = dialect; Tables = tables }

    let private sourceKey catalog name alias =
        let full = keyIdentifier catalog.Dialect name
        let tail = Identifier.parts name |> List.last |> keyPart catalog.Dialect
        match alias with
        | Some part -> [ keyPart catalog.Dialect part ]
        | None when full = tail -> [ full ]
        | None -> [ full; tail ]

    let private emptyKinds = Map.empty<string, SqlSchemaValueKind>

    let private namedSource catalog name alias =
        { Qualifiers = sourceKey catalog name alias
          Kinds = Map.tryFind (keyIdentifier catalog.Dialect name) catalog.Tables
                  |> Option.defaultValue emptyKinds }

    let private sourceAlias catalog alias =
        { Qualifiers = [ keyPart catalog.Dialect alias ]
          Kinds = emptyKinds }

    let private typeKnown kind =
        match kind with
        | SqlSchemaValueKind.Unknown -> None
        | value -> Some value

    let rec private kindInScope scope qualifier column =
        let sources =
            match qualifier with
            | None -> scope.Sources
            | Some value ->
                scope.Sources
                |> List.filter (fun source -> List.contains value source.Qualifiers)
        let hits =
            sources
            |> List.choose (fun source -> Map.tryFind column source.Kinds)
        match hits with
        | [ found ] -> typeKnown found
        | [] ->
            match scope.Parent with
            | Some parent when qualifier.IsNone || sources.IsEmpty ->
                kindInScope parent qualifier column
            | _ -> None
        | _ -> None // The name binder already rejects ambiguous references.

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

    let rec private expression catalog scope span expr =
        let evaluate = expression catalog scope Span.unknown
        match expr with
        | Spanned(current, value) -> expression catalog scope current value
        | BoundColumn(_, ColumnBinding.ProjectionAlias) -> None
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
            queryTypes catalog (Some scope) query |> ignore
            Some SqlSchemaValueKind.Boolean
        | InSubquery(value, query, _) ->
            evaluate value |> ignore
            queryTypes catalog (Some scope) query |> ignore
            Some SqlSchemaValueKind.Boolean
        | ScalarSubquery query ->
            queryTypes catalog (Some scope) query |> ignore
            None
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

    and private tableSource catalog parent table =
        match table with
        | NamedTable(name, alias) -> namedSource catalog name alias
        | CteTable(name, alias) -> { Qualifiers = sourceKey catalog name alias; Kinds = emptyKinds }
        | DerivedTable(query, alias) ->
            queryTypes catalog None query |> ignore
            sourceAlias catalog alias
        | LateralDerivedTable(query, alias) ->
            queryTypes catalog parent query |> ignore
            sourceAlias catalog alias

    and private queryTypes catalog parent (query: Query) =
        // CTE shape/name constraints are already proved by RewriteSchemaValidation.
        query.Head.Ctes |> List.iter (fun cte -> queryTypes catalog None cte.Query |> ignore)
        let initial =
            { Parent = parent
              Sources =
                match query.Head.From with
                | None -> []
                | Some table -> [ tableSource catalog parent table ] }
        let mutable scope = initial
        for join in query.Head.Joins do
            let joinParent =
                match join.Source with
                | LateralDerivedTable _ -> Some scope
                | _ -> scope.Parent
            let added = tableSource catalog joinParent join.Source
            let combined = { scope with Sources = scope.Sources @ [ added ] }
            match join with
            | OnJoin(_, _, predicate) ->
                expression catalog combined Span.unknown predicate |> ignore
            | _ -> ()
            scope <- combined
        let inspect value = expression catalog scope Span.unknown value |> ignore
        match query.Head.DistinctMode with
        | SelectDistinct.DistinctOn items -> items |> NonEmpty.iter inspect
        | _ -> ()
        query.Head.Projection |> List.iter (fun item -> inspect item.Expression)
        query.Head.Where |> Option.iter inspect
        query.Head.GroupBy |> List.iter inspect
        query.Head.Having |> Option.iter inspect
        query.OrderBy |> List.iter (fun item -> inspect item.Expression)
        query.SetOperations |> List.iter (fun branch ->
            queryTypes catalog parent branch.Query |> ignore)

    let private document catalog (document: Document) =
        let root = { Parent = None; Sources = [] }
        let inspect scope value = expression catalog scope Span.unknown value |> ignore
        match document.Statement with
        | QueryStatement query -> queryTypes catalog None query
        | InsertStatement insert ->
            let target = namedSource catalog insert.Target None
            match insert.Input with
            | Values rows ->
                let columnTypes =
                    insert.Columns
                    |> List.map (fun item ->
                        Map.tryFind (keyPart catalog.Dialect item) target.Kinds
                        |> Option.bind typeKnown)
                rows |> NonEmpty.iter (fun row ->
                    let values = NonEmpty.toList row
                    values |> List.iter (inspect root)
                    if values.Length = columnTypes.Length then
                        List.zip columnTypes values
                        |> List.iter (fun (targetKind, value) ->
                            compatible document.Span targetKind
                                (expression catalog root Span.unknown value) "INSERT assignment"))
            | QuerySource query -> queryTypes catalog None query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item ->
                inspect { root with Sources = [ target ] } item.Expression)
        | UpdateStatement update ->
            let target = namedSource catalog update.Target update.TargetAlias
            let others = update.From |> List.map (tableSource catalog None)
            let scope = { root with Sources = target :: others }
            let targetScope = { root with Sources = [ target ] }
            update.Assignments |> List.iter (fun item ->
                compatible document.Span
                    (columnKind catalog targetScope item.Target)
                    (expression catalog scope Span.unknown item.Value)
                    "UPDATE assignment")
            update.Where |> Option.iter (inspect scope)
            update.Returning |> List.iter (fun item -> inspect targetScope item.Expression)
        | DeleteStatement delete ->
            let target = namedSource catalog delete.Target delete.TargetAlias
            let scope =
                { root with
                    Sources = target :: (delete.Using |> List.map (tableSource catalog None)) }
            delete.Where |> Option.iter (inspect scope)
            delete.Returning |> List.iter (fun item -> inspect { root with Sources = [ target ] } item.Expression)
        | MergeStatement _ -> () // Name binding already rejects MERGE in strict snapshot mode.

    let verify dialect snapshot (bound: BoundSql) =
        let catalog = catalog dialect snapshot
        document catalog (Bound.value bound)
        bound
