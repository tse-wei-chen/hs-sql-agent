namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Read-only source evidence for statically analyzed DML. Never provides a
/// physical target table, executable mutation permission, or runtime effect
/// proof. Only proven PostgreSQL View output columns are admitted.
module internal RewriteMutationAnalyzer =
    let readableViews (dialect: SqlAgentToolType)
                      (state: SqlCatalogState)
                      (bound: BoundSql) : seq<SqlCatalogView> =
        match (Bound.value bound).Statement with
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _
            when dialect = SqlAgentToolType.Postgres ->
            state.Views :> seq<SqlCatalogView>
        | _ -> Seq.empty

/// A strict AST INSERT can identify positional write targets only when
/// explicit names were given, or the ordered physical DDL projection itself
/// was proved by the in-memory Virtual Catalog. Generic snapshots expose
/// column sets, NOT sufficient physical ordinal provenance.
type internal InsertTargetLayout<'column> =
    | ProvenTargets of 'column list
    | UnprovenImplicitOrder

module internal RewriteInsertTargetLayout =
    let select (explicitColumns: 'column list) (physicalColumns: 'column list)
               implicitOrderProven =
        match explicitColumns with
        | _ :: _ -> ProvenTargets explicitColumns
        | [] when implicitOrderProven -> ProvenTargets physicalColumns
        | [] -> UnprovenImplicitOrder
