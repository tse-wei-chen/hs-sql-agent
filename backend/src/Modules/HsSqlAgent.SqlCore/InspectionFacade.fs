#nowarn "3261" "3262"

namespace HsSqlAgent.SqlCore

open System
open System.Collections.Generic
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module private Inspection =

    type State =
        { Tables: HashSet<string>
          Aliases: ResizeArray<QueryAliasFact>
          mutable ContainsSubquery: bool
          mutable ContainsCte: bool
          mutable NextScopeId: int }

    let identifierText = CoreModel.Identifier.text

    let newState () =
        { Tables = HashSet<string>(StringComparer.OrdinalIgnoreCase)
          Aliases = ResizeArray<QueryAliasFact>()
          ContainsSubquery = false
          ContainsCte = false
          NextScopeId = 0 }

    let scopeId (state: State) =
        let value = state.NextScopeId
        state.NextScopeId <- value + 1
        value

    let registerAlias (state: State) scope target (alias: CoreModel.IdentifierPart option) =
        match alias with
        | Some value when not (String.IsNullOrWhiteSpace(value.Value)) ->
            state.Aliases.Add(QueryAliasFact(value.Value, target, scope))
        | _ -> ()

    let rec inspectExpr (state: State) (expression: Expr) =
        match expression with
        | Spanned(_, inner) -> inspectExpr state inner
        | Column _
        | BoundColumn _
        | Wildcard _
        | OrderOrdinal _
        | Literal _
        | Interval _ -> ()
        | DateAdd(_, amount, value)
        | DateDiff(_, amount, value) ->
            inspectExpr state amount
            inspectExpr state value
        | Unary(_, value) -> inspectExpr state value
        | Binary(_, left, right) ->
            inspectExpr state left
            inspectExpr state right
        | Like(value, pattern, _, _, _) ->
            inspectExpr state value
            inspectExpr state pattern
        | RawRegexCall(arguments, _)
        | FunctionCall { Arguments = arguments } ->
            arguments |> List.iter (inspectExpr state)
            match expression with
            | FunctionCall call ->
                call.AggregateOrderBy
                |> List.iter (fun item -> inspectExpr state item.Expression)
            | _ -> ()
        | RegexMatch(value, pattern) ->
            inspectExpr state value
            inspectExpr state pattern
        | PostgresJsonAccess(value, _, _) ->
            inspectExpr state value
        | FilteredAggregate(value, predicate) ->
            inspectExpr state value
            inspectExpr state predicate
        | Windowed(value, window) ->
            inspectExpr state value
            window.PartitionBy |> List.iter (inspectExpr state)
            window.OrderBy |> List.iter (fun item -> inspectExpr state item.Expression)
        | Cast(value, _)
        | Extract(_, value)
        | IsNull(value, _) ->
            inspectExpr state value
        | SimpleCase(input, branches, fallback) ->
            inspectExpr state input
            branches
            |> NonEmpty.toList
            |> List.iter (fun branch ->
                inspectExpr state branch.Match
                inspectExpr state branch.Result)
            fallback |> Option.iter (inspectExpr state)
        | SearchedCase(branches, fallback) ->
            branches
            |> NonEmpty.toList
            |> List.iter (fun branch ->
                inspectExpr state branch.Condition
                inspectExpr state branch.Result)
            fallback |> Option.iter (inspectExpr state)
        | InList(value, items, _) ->
            inspectExpr state value
            items |> NonEmpty.toList |> List.iter (inspectExpr state)
        | InSubquery(value, query, _) ->
            inspectExpr state value
            state.ContainsSubquery <- true
            inspectQuery state query
        | Between(value, lower, upper, _) ->
            inspectExpr state value
            inspectExpr state lower
            inspectExpr state upper
        | ScalarSubquery query
        | Exists(query, _) ->
            state.ContainsSubquery <- true
            inspectQuery state query

    and inspectSource (state: State) scope (source: TableSource) =
        match source with
        | NamedTable(name, alias) ->
            let target = identifierText name
            state.Tables.Add(target) |> ignore
            registerAlias state scope target alias
        | CteTable(name, alias) ->
            registerAlias state scope (identifierText name) alias
        | DerivedTable(query, alias)
        | LateralDerivedTable(query, alias) ->
            state.ContainsSubquery <- true
            registerAlias state scope "<subquery>" (Some alias)
            inspectQuery state query

    and inspectJoin state scope (join: Join) =
        inspectSource state scope join.Source
        join.Predicate |> Option.iter (inspectExpr state)

    and inspectSelect (state: State) (select: Select) =
        let scope = scopeId state
        if not select.Ctes.IsEmpty then state.ContainsCte <- true
        select.Ctes |> List.iter (fun cte -> inspectQuery state cte.Query)
        select.From |> Option.iter (inspectSource state scope)
        select.Joins |> List.iter (inspectJoin state scope)
        match select.DistinctMode with
        | SelectDistinct.DistinctOn expressions ->
            expressions |> NonEmpty.iter (inspectExpr state)
        | SelectDistinct.AllRows
        | SelectDistinct.DistinctRows -> ()
        select.Projection |> List.iter (fun item -> inspectExpr state item.Expression)
        select.Where |> Option.iter (inspectExpr state)
        select.GroupBy |> List.iter (inspectExpr state)
        select.Having |> Option.iter (inspectExpr state)

    and inspectQuery (state: State) (query: Query) =
        inspectSelect state query.Head
        query.SetOperations |> List.iter (fun branch -> inspectQuery state branch.Query)
        query.OrderBy |> List.iter (fun item -> inspectExpr state item.Expression)

    let inspectDocument (document: Document) =
        let state = newState ()
        match document.Statement with
        | Statement.QueryStatement query ->
            inspectQuery state query
        | Statement.InsertStatement insert ->
            let target = identifierText insert.Target
            state.Tables.Add(target) |> ignore
            match insert.Input with
            | InsertInput.QuerySource query ->
                state.ContainsSubquery <- true
                inspectQuery state query
            | InsertInput.Values rows ->
                rows
                |> NonEmpty.toList
                |> List.collect NonEmpty.toList
                |> List.iter (inspectExpr state)
            | InsertInput.DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> inspectExpr state item.Expression)
        | Statement.UpdateStatement update ->
            state.Tables.Add(identifierText update.Target) |> ignore
            let scope = scopeId state
            update.From |> List.iter (inspectSource state scope)
            update.Assignments |> List.iter (fun assignment -> inspectExpr state assignment.Value)
            update.Where |> Option.iter (inspectExpr state)
            update.Returning |> List.iter (fun item -> inspectExpr state item.Expression)
        | Statement.DeleteStatement delete ->
            state.Tables.Add(identifierText delete.Target) |> ignore
            let scope = scopeId state
            delete.Using |> List.iter (inspectSource state scope)
            delete.Where |> Option.iter (inspectExpr state)
            delete.Returning |> List.iter (fun item -> inspectExpr state item.Expression)
        | Statement.MergeStatement merge ->
            state.Tables.Add(identifierText merge.Target) |> ignore
            merge.Source.SourceValues |> NonEmpty.iter (inspectExpr state)
            inspectExpr state merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> inspectExpr state item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues |> NonEmpty.iter (inspectExpr state))

        QueryFacts(
            state.Tables.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            state.Aliases.ToImmutableArray(),
            state.ContainsSubquery,
            state.ContainsCte)

[<AbstractClass; Sealed>]
type SqlCoreInspection private () =
    static member GetDeterminismFacts(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType) =

        RewriteFacadeAdapter.determinismFacts
            sql
            sourceDialect
            targetProvider
            null
            null

    static member GetDeterminismFacts(
        sql: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType,
        sourceProfile: SqlProviderCapabilityProfile,
        targetProfile: SqlProviderCapabilityProfile) =

        RewriteFacadeAdapter.determinismFacts
            sql
            sourceDialect
            targetProvider
            sourceProfile
            targetProfile

    static member GetQueryFacts(sql: string, sourceDialect: SqlAgentToolType) =
        ArgumentNullException.ThrowIfNull(sql)

        RewriteFacadeAdapter.parseSourceValidated sql sourceDialect null
        |> RewriteBinder.bind sourceDialect
        |> Bound.value
        |> Inspection.inspectDocument

    static member GetQueryFacts(parsed: ParsedStatement) =
        ArgumentNullException.ThrowIfNull(parsed)

        if parsed.EnforceSourceDialectSyntax && not (String.IsNullOrWhiteSpace(parsed.RawSql)) then
            RewriteFacadeAdapter.parseSourceValidated
                parsed.RawSql
                parsed.SourceDialect
                parsed.SourceProfile
            |> ignore

        RewriteLegacyAstAdapter.toParsed parsed.Statement
        |> RewriteBinder.bind parsed.SourceDialect
        |> Bound.value
        |> Inspection.inspectDocument
