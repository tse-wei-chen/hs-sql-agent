namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Per-statement, non-executable Virtual Catalog projection shared by the
/// independent name and type validators. Never cache across SQL statements:
/// DDL, transaction rollback and session scope change the visible catalog.
module internal RewriteVirtualCatalogProjection =

    /// Canonical names are materialized exactly once. Column objects are
    /// immutable, so TypeValidation can read their original types only after
    /// NameValidation succeeds. No per-column type tuple is allocated for
    /// SQLite name-only checks.
    type Relation =
        { Key: string
          Names: string list
          Columns: IReadOnlyList<SqlCatalogColumn> }
    type Projection =
        { Physical: Relation list
          ReadableViews: Relation list }

    let private catalogPart dialect (value: string) =
        RewriteResourceBudget.charge (max 1L (int64 value.Length))
        match dialect with
        | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Sqlite ->
            value.ToUpperInvariant()
        | _ -> value

    let private project dialect (name: SqlCatalogQualifiedName)
                        (columns: IReadOnlyList<SqlCatalogColumn>) =
        RewriteResourceBudget.checkpoint ()
        { Key = name.BinderKey
          Names = columns |> Seq.map (fun column -> catalogPart dialect column.Name) |> Seq.toList
          Columns = columns }

    let prepare dialect (state: SqlCatalogState) (bound: BoundSql) =
        if not state.IsClosedWorld
           || (state.Tables |> Seq.exists (fun t -> t.Knowledge <> SqlCatalogKnowledge.Known)) then
            invalidArg "state" "Strict catalog binding requires a complete known closed-world catalog."

        let physical =
            state.Tables
            |> Seq.map (fun table ->
                project dialect table.QualifiedName table.Columns)
            |> Seq.toList

        // Physical DML targets must NEVER inherit read-only View identities.
        // Query/Mutation readable View selection retains its independent,
        // provider-specific proof from the bound closed AST.
        let readableViews =
            seq {
                for view in RewriteQueryAnalyzer.readableViews dialect state bound do
                    yield project dialect view.QualifiedName view.Columns
                for view in RewriteMutationAnalyzer.readableViews dialect state bound do
                    yield project dialect view.QualifiedName view.Columns
            }
            |> Seq.toList
        { Physical = physical; ReadableViews = readableViews }
