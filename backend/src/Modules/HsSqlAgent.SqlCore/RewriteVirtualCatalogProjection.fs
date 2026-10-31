namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Per-statement, non-executable Virtual Catalog projection shared by the
/// independent name and type validators. Never cache across SQL statements:
/// DDL, transaction rollback and session scope change the visible catalog.
module internal RewriteVirtualCatalogProjection =

    /// Canonical binder column name paired with the original DDL type tag.
    /// Type-family interpretation belongs exclusively to TypeValidation.
    type RelationColumns = (string * string) list
    type Projection =
        { Physical: (string * RelationColumns) list
          Readable: (string * RelationColumns) list }

    let private catalogPart dialect (value: string) =
        RewriteResourceBudget.charge (max 1L (int64 value.Length))
        match dialect with
        | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Sqlite ->
            value.ToUpperInvariant()
        | _ -> value

    let private project dialect (name: SqlCatalogQualifiedName)
                        (columns: seq<SqlCatalogColumn>) =
        RewriteResourceBudget.checkpoint ()
        name.BinderKey,
        (columns
         |> Seq.map (fun column -> catalogPart dialect column.Name, column.TypeName)
         |> Seq.toList)

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
        let readable =
            seq {
                yield! physical
                for view in RewriteQueryAnalyzer.readableViews dialect state bound do
                    yield project dialect view.QualifiedName view.Columns
                for view in RewriteMutationAnalyzer.readableViews dialect state bound do
                    yield project dialect view.QualifiedName view.Columns
            }
            |> Seq.toList
        { Physical = physical; Readable = readable }
