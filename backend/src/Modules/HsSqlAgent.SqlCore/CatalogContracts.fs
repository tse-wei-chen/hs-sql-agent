namespace HsSqlAgent.SqlCore.Catalog

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation

/// Closed-world absence is provable only when the caller explicitly supplies
/// a known-empty initial Catalog; unknown is the safe public default.
type SqlCatalogKnowledge =
    | Known = 0
    | Partial = 1
    | Unknown = 2

type SqlCatalogOperation =
    | CreateTable = 0
    | DropTable = 1
    | AddColumn = 2
    | DropColumn = 3
    | Indeterminate = 4

[<Sealed>]
type SqlCatalogColumn internal (name: string, typeName: string, nullable: bool) =
    member _.Name = name
    member _.TypeName = typeName
    member _.Nullable = nullable

[<Sealed>]
type SqlCatalogTable internal (
    name: string, knowledge: SqlCatalogKnowledge,
    columns: IReadOnlyList<SqlCatalogColumn>) =
    member _.Name = name
    member _.Knowledge = knowledge
    member _.Columns = columns

/// Immutable snapshot detached from the legacy SqlSchemaSnapshot. The latter
/// requires known schema and must never receive an unknown/partial Catalog.
[<Sealed>]
type SqlCatalogState internal (
    isClosedWorld: bool, tables: IReadOnlyList<SqlCatalogTable>) =
    member _.IsClosedWorld = isClosedWorld
    member _.Tables = tables
    member _.Knowledge =
        if isClosedWorld && (tables |> Seq.forall (fun t -> t.Knowledge = SqlCatalogKnowledge.Known)) then
            SqlCatalogKnowledge.Known
        elif tables.Count = 0 then SqlCatalogKnowledge.Unknown
        else SqlCatalogKnowledge.Partial

    static member KnownEmpty() =
        SqlCatalogState(true, Array.AsReadOnly(Array.empty<SqlCatalogTable>))

    static member Unknown() =
        SqlCatalogState(false, Array.AsReadOnly(Array.empty<SqlCatalogTable>))

[<Sealed>]
type SqlCatalogChange internal (
    tableName: string,
    operation: SqlCatalogOperation,
    certainty: SqlCatalogKnowledge,
    span: SqlDiagnosticSpan,
    columnName: string) =
    new(tableName, operation, certainty, span) =
        SqlCatalogChange(tableName, operation, certainty, span, null)
    member _.TableName = tableName
    member _.Operation = operation
    member _.Certainty = certainty
    member _.Span = span
    /// Populated for proven column-level changes; null for table-wide or
    /// indeterminate effects. This is not an executable schema-change plan.
    member _.ColumnName = columnName
