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
    | CreateIndex = 5
    | DropIndex = 6
    | AddConstraint = 7
    | DropConstraint = 8

/// Exact, normalized SQL identifier parts. A single quoted "a.b" is one
/// part, while schema.table has two; neither can collide in Key.
[<Sealed; AllowNullLiteral>]
type SqlCatalogQualifiedName internal (parts: IReadOnlyList<string>) =
    member _.Parts = parts
    member _.IsQualified = parts.Count > 1
    /// Display only: never use this to look up identity or schema dependencies.
    member _.DisplayName = String.Join(".", parts)
    /// Unambiguous, reversible key for structured identifier parts.
    member _.Key =
        parts |> Seq.map (fun part -> string part.Length + ":" + part)
              |> String.concat ""
    /// Key shared by strict AST virtual-catalog binders. Dot/backslash
    /// escaping is applied per identifier part, never to a flattened string.
    member _.BinderKey =
        parts |> Seq.map (fun part ->
            part.Replace("\\", "\\\\").Replace(".", "\\."))
              |> String.concat "."

    static member internal SinglePart(value: string) =
        SqlCatalogQualifiedName(Array.AsReadOnly([| value |]))

[<Sealed>]
type SqlCatalogColumn internal (name: string, typeName: string, nullable: bool) =
    member _.Name = name
    member _.TypeName = typeName
    member _.Nullable = nullable

/// Structural dependencies proved by a fully modeled CREATE TABLE.
/// CHECK, arbitrary indexes, triggers and CASCADE are intentionally excluded.
type SqlCatalogConstraintKind =
    | PrimaryKey = 0
    | Unique = 1
    | ForeignKey = 2

[<Sealed>]
type SqlCatalogConstraint internal (
    name: string,
    kind: SqlCatalogConstraintKind,
    columns: IReadOnlyList<string>,
    referencedTable: string,
    referencedColumns: IReadOnlyList<string>,
    referencedIdentity: SqlCatalogQualifiedName) =
    new(name, kind, columns, referencedTable, referencedColumns) =
        SqlCatalogConstraint(
            name, kind, columns, referencedTable, referencedColumns,
            if isNull referencedTable then null
            else SqlCatalogQualifiedName.SinglePart(referencedTable))
    new(name, kind, columns, referencedIdentity: SqlCatalogQualifiedName, referencedColumns) =
        SqlCatalogConstraint(
            name, kind, columns,
            (if isNull referencedIdentity then null else referencedIdentity.DisplayName),
            referencedColumns, referencedIdentity)
    /// Null for unnamed constraints; never an auto-generated database name.
    member _.Name = name
    member _.Kind = kind
    member _.Columns = columns
    /// Only populated for a ForeignKey. No implicit remote key inference.
    member _.ReferencedTable = referencedTable
    member _.ReferencedIdentity = referencedIdentity
    member _.ReferencedColumns = referencedColumns

[<Sealed>]
type SqlCatalogIndex internal (name: string, columns: IReadOnlyList<string>) =
    member _.Name = name
    member _.Columns = columns

[<Sealed>]
type SqlCatalogTable internal (
    name: string, knowledge: SqlCatalogKnowledge,
    columns: IReadOnlyList<SqlCatalogColumn>,
    constraints: IReadOnlyList<SqlCatalogConstraint>,
    indexes: IReadOnlyList<SqlCatalogIndex>,
    identity: SqlCatalogQualifiedName) =
    new(name, knowledge, columns) =
        SqlCatalogTable(name, knowledge, columns,
                        Array.AsReadOnly(Array.empty<SqlCatalogConstraint>),
                        Array.AsReadOnly(Array.empty<SqlCatalogIndex>),
                        SqlCatalogQualifiedName.SinglePart(name))
    new(name, knowledge, columns, constraints) =
        SqlCatalogTable(name, knowledge, columns, constraints,
                        Array.AsReadOnly(Array.empty<SqlCatalogIndex>),
                        SqlCatalogQualifiedName.SinglePart(name))
    new(name, knowledge, columns, constraints, indexes) =
        SqlCatalogTable(name, knowledge, columns, constraints, indexes,
                        SqlCatalogQualifiedName.SinglePart(name))
    member _.Name = name
    member _.QualifiedName = identity
    member _.Knowledge = knowledge
    member _.Columns = columns
    member _.Constraints = constraints
    /// Only standalone simple non-unique indexes, never implicit key indexes.
    member _.Indexes = indexes

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
    columnName: string,
    tableIdentity: SqlCatalogQualifiedName,
    constraintName: string) =
    new(tableName, operation, certainty, span,
        columnName: string, tableIdentity: SqlCatalogQualifiedName) =
        SqlCatalogChange(tableName, operation, certainty, span, columnName, tableIdentity, null)
    new(tableName, operation, certainty, span) =
        SqlCatalogChange(tableName, operation, certainty, span, null, null, null)
    new(tableName, operation, certainty, span, columnName: string) =
        SqlCatalogChange(tableName, operation, certainty, span, columnName, null, null)
    member _.TableName = tableName
    /// Proven structured physical table identity, unlike the ambiguous
    /// human-readable TableName. Null for indeterminate/transaction effects.
    member _.TableIdentity = tableIdentity
    member _.Operation = operation
    member _.Certainty = certainty
    member _.Span = span
    /// Populated for proven column-level changes; null for table-wide or
    /// indeterminate effects. This is not an executable schema-change plan.
    member _.ColumnName = columnName
    /// Exact constraint identifier on a proven constraint delta; null otherwise.
    member _.ConstraintName = constraintName
