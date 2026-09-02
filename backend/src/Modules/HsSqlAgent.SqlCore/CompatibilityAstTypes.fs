
namespace HsSqlAgent.SqlCore.Core.Ast

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Compilation

[<Struct>]
type SourceSpan =
    { Start: int
      End: int }
    static member Unknown = { Start = -1; End = -1 }

[<AbstractClass>]
type SqlNode(span: SourceSpan) =
    member _.Span = span

[<AbstractClass>]
type SqlStatement(span: SourceSpan) =
    inherit SqlNode(span)

[<AbstractClass>]
type SqlExpr(span: SourceSpan) =
    inherit SqlNode(span)

[<Sealed>]
type IdentifierPart(value: string, wasQuoted: bool, span: SourceSpan, preserveSpelling: bool) =
    new(value: string, wasQuoted: bool, span: SourceSpan) = IdentifierPart(value, wasQuoted, span, false)
    member _.Value = value
    member _.WasQuoted = wasQuoted
    member _.Span = span
    member _.PreserveSpelling = preserveSpelling
    static member op_Implicit(value: string) : IdentifierPart | null =
        if String.IsNullOrWhiteSpace(value) then null
        else IdentifierPart(value.Trim(), false, SourceSpan.Unknown)

[<Sealed>]
type SqlIdentifier(parts: ImmutableArray<IdentifierPart>, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Parts = parts
    static member Unquoted(value: string, span: SourceSpan) =
        SqlIdentifier(ImmutableArray.Create(IdentifierPart(value, false, span)), span)
    static member Unquoted(value: string) =
        SqlIdentifier.Unquoted(value, SourceSpan.Unknown)

[<Sealed>]
type LiteralExpr(value: obj | null, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Value = value

[<Sealed>]
type ColumnExpr(name: SqlIdentifier, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Name = name

[<Sealed>]
type UnaryExpr(operatorName: string, operand: SqlExpr, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Operator = operatorName
    member _.Operand = operand

[<Sealed>]
type BinaryExpr(left: SqlExpr, operatorName: string, right: SqlExpr, span: SourceSpan, likeEscape: string | null) =
    inherit SqlExpr(span)
    new(left: SqlExpr, operatorName: string, right: SqlExpr, span: SourceSpan) =
        BinaryExpr(left, operatorName, right, span, null)
    member _.Left = left
    member _.Operator = operatorName
    member _.Right = right
    member _.LikeEscape = likeEscape

type AggregateOrderSyntaxKind =
    | None = 0
    | Inline = 1
    | WithinGroup = 2

[<Sealed>]
type OrderByOrdinalValue internal (position: int) =
    member _.Position = position

type NullOrderingKind =
    | Default = 0
    | First = 1
    | Last = 2

[<Sealed>]
type OrderByItem(expression: SqlExpr, descending: bool, nullOrdering: NullOrderingKind, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Expression = expression
    member _.Descending = descending
    member _.NullOrdering = nullOrdering

[<Sealed>]
type FunctionCallExpr(name: SqlIdentifier, arguments: ImmutableArray<SqlExpr>, isDistinct: bool, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Name = name
    member _.Arguments = arguments
    member _.IsDistinct = isDistinct
    member val AggregateOrderBy = ImmutableArray<OrderByItem>.Empty with get, set
    member val AggregateOrderSyntax = AggregateOrderSyntaxKind.None with get, set
    member val AggregateSeparatorClause: string | null = null with get, set

/// CLR compatibility representation of the canonical regular-expression predicate.
/// This keeps typed regex semantics across ParsedStatement round-trips instead of
/// relying on a reserved function-name string.
[<Sealed>]
type RegexExpr(value: SqlExpr, pattern: SqlExpr, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Value = value
    member _.Pattern = pattern

type PostgresJsonSelectorKind =
    | Property = 0
    | ArrayIndex = 1

type JsonExtractionResultKind =
    | Json = 0
    | Text = 1

[<Sealed>]
type PostgresJsonAccessExpr(
    value: SqlExpr,
    selectorKind: PostgresJsonSelectorKind,
    propertyKey: string | null,
    arrayIndex: Nullable<int>,
    resultKind: JsonExtractionResultKind,
    span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Value = value
    member _.SelectorKind = selectorKind
    member _.PropertyKey = propertyKey
    member _.ArrayIndex = arrayIndex
    member _.ResultKind = resultKind

[<Sealed>]
type FilterExpr(expression: SqlExpr, predicate: SqlExpr, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Expression = expression
    member _.Predicate = predicate

type WindowFrameUnitKind =
    | Rows = 0
    | Range = 1

type WindowFrameBoundKindCore =
    | UnboundedPreceding = 0
    | Preceding = 1
    | CurrentRow = 2
    | Following = 3
    | UnboundedFollowing = 4

[<Sealed>]
type WindowFrameBoundCore(kind: WindowFrameBoundKindCore, offset: Nullable<int>, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Kind = kind
    member _.Offset = offset

[<Sealed>]
type WindowFrame(unitKind: WindowFrameUnitKind, startBound: WindowFrameBoundCore, endBound: WindowFrameBoundCore | null, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Unit = unitKind
    member _.Start = startBound
    member _.End = endBound

[<Sealed>]
type WindowSpec(partitionBy: ImmutableArray<SqlExpr>, orderBy: ImmutableArray<OrderByItem>, frame: WindowFrame | null, span: SourceSpan) =
    inherit SqlNode(span)
    member _.PartitionBy = partitionBy
    member _.OrderBy = orderBy
    member _.Frame = frame

[<Sealed>]
type WindowedExpr(expression: SqlExpr, window: WindowSpec, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Expression = expression
    member _.Window = window

[<Sealed>]
type CastExpr(expression: SqlExpr, typeName: string, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Expression = expression
    member _.TypeName = typeName

[<Sealed>]
type ExtractExpr(field: string, expression: SqlExpr, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Field = field
    member _.Expression = expression

[<Sealed>]
type IntervalExpr(literal: string, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Literal = literal

[<Sealed>]
type CaseBranch(condition: SqlExpr, value: SqlExpr) =
    member _.Condition = condition
    member _.Value = value

type CaseExpr(branches: ImmutableArray<CaseBranch>, elseExpression: SqlExpr | null, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Branches = branches
    member _.ElseExpression = elseExpression

[<Sealed>]
type SimpleCaseExpr(branches: ImmutableArray<CaseBranch>, elseExpression: SqlExpr | null, span: SourceSpan) =
    inherit CaseExpr(branches, elseExpression, span)

[<Sealed>]
type InExpr(value: SqlExpr, items: ImmutableArray<SqlExpr>, isNegated: bool, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Value = value
    member _.Items = items
    member _.IsNegated = isNegated

[<Sealed>]
type BetweenExpr(value: SqlExpr, lower: SqlExpr, upper: SqlExpr, isNegated: bool, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Value = value
    member _.Lower = lower
    member _.Upper = upper
    member _.IsNegated = isNegated

[<Sealed>]
type IsNullExpr(value: SqlExpr, isNegated: bool, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Value = value
    member _.IsNegated = isNegated

[<Sealed>]
type SubqueryExpr(query: SqlStatement, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Query = query

[<Sealed>]
type ExistsExpr(query: SqlStatement, isNegated: bool, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Query = query
    member _.IsNegated = isNegated

[<AbstractClass>]
type TableSource(span: SourceSpan) =
    inherit SqlNode(span)

[<Sealed>]
type NamedTableSource(name: SqlIdentifier, alias: IdentifierPart | null, span: SourceSpan) =
    inherit TableSource(span)
    member _.Name = name
    member _.Alias = alias

[<Sealed>]
type DerivedTableSource(query: SqlStatement, alias: IdentifierPart, span: SourceSpan) =
    inherit TableSource(span)
    new(query: SqlStatement, alias: string, span: SourceSpan) =
        if String.IsNullOrWhiteSpace(alias) then invalidArg "alias" "Derived table alias cannot be empty."
        DerivedTableSource(query, IdentifierPart(alias.Trim(), false, span), span)
    member _.Query = query
    member _.Alias = alias
    member val IsLateral = false with get, set

[<Sealed>]
type SelectItem(expression: SqlExpr, alias: IdentifierPart | null, span: SourceSpan) =
    inherit SqlNode(span)
    let normalizedAlias : IdentifierPart | null =
        match alias with
        | null -> null
        | value when value.Span = SourceSpan.Unknown ->
            IdentifierPart(value.Value, value.WasQuoted, value.Span, true)
        | value -> value
    member val Expression = expression with get, set
    member val Alias = normalizedAlias with get, set
    member this.Deconstruct(expression: byref<SqlExpr>, aliasOut: byref<IdentifierPart | null>, spanOut: byref<SourceSpan>) =
        expression <- this.Expression
        aliasOut <- this.Alias
        spanOut <- this.Span

[<Sealed>]
type JoinSource(kind: string, source: TableSource, predicate: SqlExpr | null, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Kind = kind
    member _.Source = source
    member _.Predicate = predicate
    member val UsingColumns = ImmutableArray<SqlIdentifier>.Empty with get, set
    member val IsNatural = false with get, set

[<Sealed>]
type CteDefinition(name: SqlIdentifier, columnAliases: ImmutableArray<SqlIdentifier>, query: SqlStatement, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Name = name
    member _.ColumnAliases = columnAliases
    member _.Query = query
    member val RecursiveScope = false with get, set

[<Sealed>]
type SelectStatement(
    ctes: ImmutableArray<CteDefinition>,
    distinct: bool,
    selectItems: ImmutableArray<SelectItem>,
    fromSource: TableSource | null,
    joins: ImmutableArray<JoinSource>,
    whereExpr: SqlExpr | null,
    groupBy: ImmutableArray<SqlExpr>,
    having: SqlExpr | null,
    orderBy: ImmutableArray<OrderByItem>,
    limit: Nullable<int>,
    offset: Nullable<int>,
    span: SourceSpan) =
    inherit SqlStatement(span)
    member _.Ctes = ctes
    member _.Distinct = distinct
    member val DistinctOn = ImmutableArray<SqlExpr>.Empty with get, set
    member _.Select = selectItems
    member _.From = fromSource
    member _.Joins = joins
    member _.Where = whereExpr
    member _.GroupBy = groupBy
    member _.Having = having
    member _.OrderBy = orderBy
    member _.Limit = limit
    member _.Offset = offset
    member val FetchPercent = Nullable<decimal>() with get, set
    member val FetchWithTies = false with get, set

[<Sealed>]
type Assignment(column: SqlIdentifier, value: SqlExpr, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Column = column
    member _.Value = value

[<AbstractClass>]
type DmlReturningItem(span: SourceSpan) =
    inherit SqlNode(span)

[<Sealed>]
type DmlReturningColumnItem(identifier: SqlIdentifier, span: SourceSpan) =
    inherit DmlReturningItem(span)
    member _.Identifier = identifier

[<Sealed>]
type DmlReturningWildcardItem(span: SourceSpan) =
    inherit DmlReturningItem(span)

[<Sealed>]
type DmlReturningExpressionItem(expression: SqlExpr, alias: IdentifierPart | null, span: SourceSpan) =
    inherit DmlReturningItem(span)
    member _.Expression = expression
    member _.Alias = alias

[<Sealed>]
type UpdateStatement(target: NamedTableSource, assignments: ImmutableArray<Assignment>, predicate: SqlExpr | null, span: SourceSpan) =
    inherit SqlStatement(span)
    member val Target = target with get, set
    member _.Assignments = assignments
    member _.Predicate = predicate
    member val From = ImmutableArray<NamedTableSource>.Empty with get, set
    /// Full PostgreSQL FROM table-expression surface. When non-empty this is the
    /// source-of-truth collection; From remains the legacy named-source view.
    member val FromSources = ImmutableArray<TableSource>.Empty with get, set
    member val Returning = ImmutableArray<DmlReturningItem>.Empty with get, set

[<Sealed>]
type DeleteStatement(target: NamedTableSource, predicate: SqlExpr | null, span: SourceSpan) =
    inherit SqlStatement(span)
    member val Target = target with get, set
    member _.Predicate = predicate
    member val Using = ImmutableArray<NamedTableSource>.Empty with get, set
    /// Full PostgreSQL USING table-expression surface. When non-empty this is the
    /// source-of-truth collection; Using remains the legacy named-source view.
    member val UsingSources = ImmutableArray<TableSource>.Empty with get, set
    member val Returning = ImmutableArray<DmlReturningItem>.Empty with get, set

[<AbstractClass>]
type InsertSource(span: SourceSpan) =
    inherit SqlNode(span)

[<Sealed>]
type InsertValuesSource(rows: ImmutableArray<ImmutableArray<SqlExpr>>, span: SourceSpan) =
    inherit InsertSource(span)
    member _.Rows = rows

[<Sealed>]
type InsertQuerySource(query: SqlStatement, span: SourceSpan) =
    inherit InsertSource(span)
    member _.Query = query

type InsertConflictActionKind =
    | DoNothing = 0
    | UpdateProposedValues = 1

[<Sealed>]
type InsertConflictAssignment(column: SqlIdentifier, proposedColumn: SqlIdentifier | null, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Column = column
    /// Legacy direct proposed-row column. Rich conflict expressions leave this unset and use Value.
    member _.ProposedColumn = proposedColumn
    /// Closed compatibility AST expression for richer deterministic conflict-update values.
    /// Null preserves the legacy target = proposed-column constructor contract.
    member val Value: SqlExpr | null = null with get, set

[<Sealed>]
type InsertConflictClause(
    targetColumns: ImmutableArray<SqlIdentifier>,
    action: InsertConflictActionKind,
    assignments: ImmutableArray<InsertConflictAssignment>,
    span: SourceSpan) =
    inherit SqlNode(span)
    member _.TargetColumns = targetColumns
    member _.Action = action
    member _.Assignments = assignments

[<Sealed>]
type InsertStatement(target: NamedTableSource, columns: ImmutableArray<SqlIdentifier>, source: InsertSource, span: SourceSpan) =
    inherit SqlStatement(span)
    member val Target = target with get, set
    member _.Columns = columns
    member _.Source = source
    member val Conflict: InsertConflictClause | null = null with get, set
    member val Returning = ImmutableArray<DmlReturningItem>.Empty with get, set

type MergeMatchedActionKind =
    | Update = 0
    | Delete = 1

[<Sealed>]
type MergeMatchedClause(kind: MergeMatchedActionKind, assignments: ImmutableArray<Assignment>, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Kind = kind
    member _.Assignments = assignments

[<Sealed>]
type MergeInsertClause(columns: ImmutableArray<SqlIdentifier>, values: ImmutableArray<SqlExpr>, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Columns = columns
    member _.Values = values

[<Sealed>]
type MergeStatement(
    target: NamedTableSource,
    sourceAlias: IdentifierPart,
    sourceColumns: ImmutableArray<SqlIdentifier>,
    sourceValues: ImmutableArray<SqlExpr>,
    matchPredicate: SqlExpr,
    matched: MergeMatchedClause | null,
    notMatched: MergeInsertClause | null,
    span: SourceSpan) =
    inherit SqlStatement(span)
    member _.Target = target
    member _.SourceAlias = sourceAlias
    member _.SourceColumns = sourceColumns
    member _.SourceValues = sourceValues
    member _.MatchPredicate = matchPredicate
    member _.Matched = matched
    member _.NotMatched = notMatched

type SetOperationKind =
    | Union = 0
    | UnionAll = 1
    | Intersect = 2
    | Except = 3
    | IntersectAll = 4
    | ExceptAll = 5

[<Sealed>]
type SetOperation(kind: SetOperationKind, query: SqlStatement, span: SourceSpan) =
    inherit SqlNode(span)
    member _.Kind = kind
    member _.Query = query

[<Sealed>]
type QueryStatement(
    head: SelectStatement,
    setOperations: ImmutableArray<SetOperation>,
    orderBy: ImmutableArray<OrderByItem>,
    limit: Nullable<int>,
    offset: Nullable<int>,
    span: SourceSpan) =
    inherit SqlStatement(span)
    member _.Head = head
    member _.SetOperations = setOperations
    member _.OrderBy = orderBy
    member _.Limit = limit
    member _.Offset = offset
    member val FetchPercent = Nullable<decimal>() with get, set
    member val FetchWithTies = false with get, set

[<AbstractClass; Sealed>]
type DmlReturningProjection private () =
    static member FromColumns(columns: ImmutableArray<SqlIdentifier>) =
        if columns.IsDefaultOrEmpty then ImmutableArray<DmlReturningItem>.Empty
        else
            let seen = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            let mutable wildcard = false
            let builder = ImmutableArray.CreateBuilder<DmlReturningItem>(columns.Length)
            for column in columns do
                if column.Parts.Length <> 1 then raise (SqlCompilationException("Portable DML RETURNING accepts unqualified target columns only."))
                let part = column.Parts[0]
                let isWildcard = part.Value = "*" && not part.WasQuoted
                wildcard <- wildcard || isWildcard
                if not (seen.Add(part.Value)) then raise (SqlCompilationException("RETURNING column '" + part.Value + "' is declared more than once."))
                builder.Add(if isWildcard then DmlReturningWildcardItem(column.Span) :> DmlReturningItem else DmlReturningColumnItem(column,column.Span) :> DmlReturningItem)
            if wildcard && columns.Length <> 1 then raise (SqlCompilationException("RETURNING * cannot be mixed with explicit RETURNING columns in the portable Core contract."))
            builder.ToImmutable()

namespace HsSqlAgent.SqlCore.Core.Binding

open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast

[<Sealed>]
type TableSymbol(name: string, alias: string, isDerived: bool, isCte: bool, span: SourceSpan) =
    member _.Name = name
    member _.Alias = alias
    member _.IsDerived = isDerived
    member _.IsCte = isCte
    member _.Span = span
    member _.VisibleName = if System.String.IsNullOrWhiteSpace(alias) then name else alias

[<Sealed>]
type BoundColumnExpr(name: SqlIdentifier, source: TableSymbol, span: SourceSpan) =
    inherit SqlExpr(span)
    member _.Name = name
    member _.Source = source
    member val IsOuterReference = false with get, set

[<Sealed>]
type QueryAliasFact(alias: string, target: string, scopeId: int) =
    member _.Alias = alias
    member _.Target = target
    member _.ScopeId = scopeId

[<Sealed>]
type QueryFacts(referencedTables: ImmutableHashSet<string>, aliases: ImmutableArray<QueryAliasFact>, containsSubquery: bool, containsCte: bool) =
    member _.ReferencedTables = referencedTables
    member _.Aliases = aliases
    member _.ContainsSubquery = containsSubquery
    member _.ContainsCte = containsCte

namespace HsSqlAgent.SqlCore.Core.Pipeline

open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

[<Sealed>]
type ParsedStatement(statement: SqlStatement, sourceDialect: SqlAgentToolType, enforceSourceDialectSyntax: bool, sourceProfile: SqlProviderCapabilityProfile | null) =
    new(statement: SqlStatement, sourceDialect: SqlAgentToolType) =
        ParsedStatement(statement, sourceDialect, false, null)
    new(statement: SqlStatement, sourceDialect: SqlAgentToolType, enforceSourceDialectSyntax: bool) =
        ParsedStatement(statement, sourceDialect, enforceSourceDialectSyntax, null)
    member val Statement = statement with get, set
    member _.SourceDialect = sourceDialect
    member val EnforceSourceDialectSyntax = enforceSourceDialectSyntax with get, set
    member val SourceProfile = sourceProfile with get, set
    member val RawSql: string | null = null with get, set

[<Sealed>]
type BoundStatement(statement: SqlStatement, facts: QueryFacts, sourceDialect: SqlAgentToolType) =
    member _.Statement = statement
    member _.Facts = facts
    member _.SourceDialect = sourceDialect

[<Sealed>]
type CanonicalStatement(statement: SqlStatement, facts: QueryFacts, sourceDialect: SqlAgentToolType, targetProvider: SqlAgentToolType) =
    member _.Statement = statement
    member _.Facts = facts
    member _.SourceDialect = sourceDialect
    member _.TargetProvider = targetProvider
