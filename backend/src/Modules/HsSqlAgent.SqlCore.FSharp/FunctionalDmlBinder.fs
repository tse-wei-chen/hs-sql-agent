namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums

/// DML binding implemented on top of the F# query scope engine.
///
/// Mutation-specific shape handling stays explicit here, while expression,
/// alias, qualifier, CTE, and correlated-subquery resolution is delegated to
/// FunctionalQueryBinder. This mirrors the legacy carrier strategy without
/// depending on SqlAstBinder.
module internal FunctionalDmlBinder =

    let private toImmutableArray<'T> (items: seq<'T>) =
        ImmutableArray.CreateRange<'T>(items)

    let private identifierName (identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    let private requireExpr context (value: SqlExpr | null) : SqlExpr =
        match value with
        | null ->
            raise (InvalidOperationException(
                $"{context} cannot be null at the F# DML binder boundary."))
        | expression ->
            expression

    let private parsedCarrier
        (sourceDialect: SqlAgentToolType)
        (statement: SqlStatement) =
        ParsedStatement(
            statement,
            sourceDialect,
            false,
            null)

    let private boundSelect (bound: BoundStatement) =
        match bound.Statement with
        | :? SelectStatement as select ->
            select
        | other ->
            raise (SqlCompilationException(
                $"DML binding carrier returned '{other.GetType().Name}' instead of SelectStatement."))

    let private mutationFacts
        targetName
        (facts: QueryFacts)
        forceContainsSubquery =

        QueryFacts(
            facts.ReferencedTables.Add(targetName),
            facts.Aliases,
            facts.ContainsSubquery || forceContainsSubquery,
            facts.ContainsCte)

    let private validateSimpleUpdateAssignments
        (update: UpdateStatement) =

        if update.Assignments.IsDefaultOrEmpty then
            raise (InvalidOperationException(
                "UPDATE requires at least one assignment."))

        for assignment in update.Assignments do
            if assignment.Column.Parts.Length <> 1 then
                raise (InvalidOperationException(
                    $"UPDATE assignment column '{identifierName assignment.Column}' must be unqualified."))

    let private createUpdateCarrier (update: UpdateStatement) =
        let projection =
            seq {
                for assignment in update.Assignments do
                    yield SelectItem(
                        requireExpr
                            "UPDATE assignment source expression"
                            assignment.Value,
                        null,
                        assignment.Span)

                match Option.ofObj update.Predicate with
                | Some predicate ->
                    yield SelectItem(
                        predicate,
                        null,
                        predicate.Span)
                | None ->
                    ()
            }
            |> toImmutableArray

        let joins =
            update.From
            |> Seq.map (fun source ->
                JoinSource(
                    "CROSS",
                    source,
                    null,
                    source.Span))
            |> toImmutableArray

        SelectStatement(
            ImmutableArray<CteDefinition>.Empty,
            false,
            projection,
            update.Target,
            joins,
            null,
            ImmutableArray<SqlExpr>.Empty,
            null,
            ImmutableArray<OrderByItem>.Empty,
            Nullable<int>(),
            Nullable<int>(),
            update.Span)

    let private restoreUpdate
        (update: UpdateStatement)
        (boundCarrier: BoundStatement) =

        let select = boundSelect boundCarrier

        if select.Select.Length < update.Assignments.Length then
            raise (SqlCompilationException(
                "UPDATE binding carrier returned fewer expressions than assignments."))

        let assignments =
            update.Assignments
            |> Seq.mapi (fun index assignment ->
                CoreBindingAstClone.Assignment(
                    assignment,
                    requireExpr
                        "UPDATE assignment expression"
                        select.Select[index].Expression))
            |> toImmutableArray

        let predicate =
            match Option.ofObj update.Predicate with
            | None ->
                None
            | Some _ ->
                if select.Select.Length <= update.Assignments.Length then
                    raise (SqlCompilationException(
                        "UPDATE binding carrier did not return the mutation predicate."))

                Some(
                    requireExpr
                        "UPDATE predicate"
                        select.Select[update.Assignments.Length].Expression)

        CoreBindingAstClone.Update(
            update,
            assignments,
            Option.toObj predicate)

    let private bindUpdate
        (statement: ParsedStatement)
        (update: UpdateStatement) =

        if update.From.IsDefaultOrEmpty then
            validateSimpleUpdateAssignments update

        let carrier = createUpdateCarrier update
        let boundCarrier =
            FunctionalQueryBinder.bind(
                parsedCarrier
                    statement.SourceDialect
                    carrier)

        BoundStatement(
            restoreUpdate update boundCarrier,
            boundCarrier.Facts,
            statement.SourceDialect)

    let private createDeleteCarrier (delete: DeleteStatement) =
        let projection =
            match Option.ofObj delete.Predicate with
            | Some predicate ->
                ImmutableArray.Create(
                    SelectItem(
                        predicate,
                        null,
                        predicate.Span))
            | None ->
                ImmutableArray<SelectItem>.Empty

        let joins =
            delete.Using
            |> Seq.map (fun source ->
                JoinSource(
                    "CROSS",
                    source,
                    null,
                    source.Span))
            |> toImmutableArray

        SelectStatement(
            ImmutableArray<CteDefinition>.Empty,
            false,
            projection,
            delete.Target,
            joins,
            null,
            ImmutableArray<SqlExpr>.Empty,
            null,
            ImmutableArray<OrderByItem>.Empty,
            Nullable<int>(),
            Nullable<int>(),
            delete.Span)

    let private restoreDelete
        (delete: DeleteStatement)
        (boundCarrier: BoundStatement) =

        let select = boundSelect boundCarrier

        let predicate =
            match Option.ofObj delete.Predicate with
            | None ->
                None
            | Some _ ->
                if select.Select.IsDefaultOrEmpty then
                    raise (SqlCompilationException(
                        "DELETE binding carrier did not return the mutation predicate."))

                Some(
                    requireExpr
                        "DELETE predicate"
                        select.Select[0].Expression)

        CoreBindingAstClone.Delete(
            delete,
            Option.toObj predicate)

    let private bindDelete
        (statement: ParsedStatement)
        (delete: DeleteStatement) =

        if not delete.Using.IsDefaultOrEmpty
           && Option.isNone (Option.ofObj delete.Predicate) then
            raise (InvalidOperationException(
                "DELETE ... USING requires a predicate before binding."))

        let carrier = createDeleteCarrier delete
        let boundCarrier =
            FunctionalQueryBinder.bind(
                parsedCarrier
                    statement.SourceDialect
                    carrier)

        BoundStatement(
            restoreDelete delete boundCarrier,
            boundCarrier.Facts,
            statement.SourceDialect)

    let private validateInsert (insert: InsertStatement) =
        if insert.Columns.IsDefaultOrEmpty then
            raise (InvalidOperationException(
                "INSERT requires at least one target column."))

        if insert.Columns
           |> Seq.exists (fun column -> column.Parts.Length <> 1) then
            raise (InvalidOperationException(
                "INSERT target columns must be unqualified."))

        match insert.Source with
        | :? InsertValuesSource as values
            when values.Rows.IsDefaultOrEmpty ->
            raise (InvalidOperationException(
                "INSERT VALUES requires at least one row."))
        | _ ->
            ()

    let private bindInsertValues
        (statement: ParsedStatement)
        (insert: InsertStatement)
        (values: InsertValuesSource) =

        let carrier =
            CoreInsertValuesCarrier.CreateExpressionCarrier(values)

        let boundCarrier =
            FunctionalQueryBinder.bind(
                parsedCarrier
                    statement.SourceDialect
                    carrier)

        let boundValues =
            CoreInsertValuesCarrier.RestoreFromExpressionCarrier(
                values,
                boundCarrier.Statement)

        let targetName = identifierName insert.Target.Name
        let facts =
            mutationFacts
                targetName
                boundCarrier.Facts
                false

        BoundStatement(
            CoreBindingAstClone.Insert(
                insert,
                boundValues),
            facts,
            statement.SourceDialect)

    let private bindInsertQuery
        (statement: ParsedStatement)
        (insert: InsertStatement)
        (querySource: InsertQuerySource) =

        let boundQuery =
            FunctionalQueryBinder.bind(
                parsedCarrier
                    statement.SourceDialect
                    querySource.Query)

        let targetName = identifierName insert.Target.Name
        let facts =
            mutationFacts
                targetName
                boundQuery.Facts
                true

        let source =
            CoreBindingAstClone.InsertQuery(
                querySource,
                boundQuery.Statement)

        BoundStatement(
            CoreBindingAstClone.Insert(
                insert,
                source),
            facts,
            statement.SourceDialect)

    let private bindInsert
        (statement: ParsedStatement)
        (insert: InsertStatement) =

        validateInsert insert

        match insert.Source with
        | :? InsertValuesSource as values ->
            bindInsertValues statement insert values

        | :? InsertQuerySource as querySource ->
            bindInsertQuery statement insert querySource

        | other ->
            raise (InvalidOperationException(
                $"Unsupported INSERT source while binding: {other.GetType().Name}"))

    /// Bind INSERT/UPDATE/DELETE without invoking the legacy C# binder.
    let bind (statement: ParsedStatement) : BoundStatement =
        match statement.Statement with
        | :? InsertStatement as insert ->
            bindInsert statement insert

        | :? UpdateStatement as update ->
            bindUpdate statement update

        | :? DeleteStatement as delete ->
            bindDelete statement delete

        | other ->
            raise (InvalidOperationException(
                $"Unsupported SQL statement while DML binding: {other.GetType().Name}"))
