namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Text.RegularExpressions

/// Pure F# compiler model. No compatibility AST classes are allowed below this boundary.
module internal CoreModel =

    [<Struct>]
    type Span =
        { Start: int
          Length: int }

    type IdentifierPart =
        { Value: string
          WasQuoted: bool
          Span: Span }

    type Identifier = private Identifier of IdentifierPart list

    module Identifier =
        let create parts =
            match parts with
            | [] -> invalidArg (nameof parts) "SQL identifier must contain at least one part."
            | values when values |> List.exists (fun part -> String.IsNullOrWhiteSpace(part.Value)) ->
                invalidArg (nameof parts) "SQL identifier parts cannot be empty or whitespace."
            | values -> Identifier values

        let parts (Identifier parts) = parts
        let text identifier = identifier |> parts |> List.map (fun part -> part.Value) |> String.concat "."

    type NonEmpty<'T> = private NonEmpty of head: 'T * tail: 'T list

    module NonEmpty =
        let create head tail = NonEmpty(head, tail)
        let ofList argumentName values =
            match values with
            | head :: tail -> NonEmpty(head, tail)
            | [] -> invalidArg argumentName "Collection must contain at least one item."
        let toList (NonEmpty(head, tail)) = head :: tail
        let map mapping (NonEmpty(head, tail)) = NonEmpty(mapping head, tail |> List.map mapping)
        let iter action values = values |> toList |> List.iter action
        let length values = values |> toList |> List.length

    type NonNegativeRowCount = private NonNegativeRowCount of int

    module NonNegativeRowCount =
        let create value =
            if value < 0 then invalidArg (nameof value) "Row count cannot be negative."
            NonNegativeRowCount value
        let value (NonNegativeRowCount value) = value

    type PositiveRowCount = private PositiveRowCount of int

    module PositiveRowCount =
        let create value =
            if value <= 0 then invalidArg (nameof value) "Row count must be positive."
            PositiveRowCount value
        let value (PositiveRowCount value) = value

    type UnaryOperator = Not | Negate | Positive

    type BinaryOperator =
        | Add | Subtract | Multiply | Divide | Modulo | Concat
        | Equal | NotEqual | GreaterThan | LessThan | GreaterThanOrEqual | LessThanOrEqual
        | And | Or

    type JoinKind = Inner | Left | Right | Full | Cross
    type OnJoinKind = Inner | Left | Right | Full
    type NullOrdering = Default | NullsFirst | NullsLast
    type SetOperator = Union | UnionAll | Intersect | Except
    type WindowFrameUnit = Rows | Range

    type FrameOffset = private FrameOffset of int

    module FrameOffset =
        let create value =
            if value < 0 then invalidArg (nameof value) "Window frame offset cannot be negative."
            FrameOffset value
        let value (FrameOffset value) = value

    type WindowFrameBound =
        | UnboundedPreceding
        | Preceding of FrameOffset
        | CurrentRow
        | Following of FrameOffset
        | UnboundedFollowing

    type WindowFrameExtent =
        | SingleBound of WindowFrameBound
        | BetweenBounds of WindowFrameBound * WindowFrameBound

    type WindowFrame =
        { Unit: WindowFrameUnit
          Extent: WindowFrameExtent }
        member this.Start =
            match this.Extent with
            | SingleBound start | BetweenBounds(start, _) -> start
        member this.End =
            match this.Extent with
            | SingleBound _ -> None
            | BetweenBounds(_, finish) -> Some finish

    type ScalarValue =
        | Null
        | Boolean of bool
        | Integer of int64
        | Decimal of decimal
        | Floating of double
        | Text of string
        | Date of DateOnly
        | Time of TimeOnly
        | LocalDateTime of DateTime
        | OffsetDateTime of DateTimeOffset
        | Duration of TimeSpan
        | Bytes of byte array

    type IntervalLiteral = private IntervalLiteral of string

    module IntervalLiteral =
        let create value =
            if String.IsNullOrWhiteSpace(value) then invalidArg (nameof value) "INTERVAL literal cannot be empty."
            if value.IndexOfAny([| '\000'; '\r'; '\n' |]) >= 0 then invalidArg (nameof value) "INTERVAL literal contains invalid control characters."
            IntervalLiteral value
        let value (IntervalLiteral value) = value

    type CastType = private CastType of string

    module CastType =
        let private safeCastType = Regex("^[A-Za-z][A-Za-z0-9_ ]*(\\([0-9]+(,[0-9]+)?\\))?$", RegexOptions.CultureInvariant)
        let create value =
            if String.IsNullOrWhiteSpace(value) || not (safeCastType.IsMatch(value)) then invalidArg (nameof value) ("Unsafe CAST type '" + string value + "'.")
            CastType value
        let value (CastType value) = value

    type FunctionName = private FunctionName of string

    module FunctionName =
        let private safeFunctionName = Regex("^[A-Za-z_][A-Za-z0-9_$.]*$", RegexOptions.CultureInvariant)
        let create value =
            if String.IsNullOrWhiteSpace(value) || not (safeFunctionName.IsMatch(value)) then invalidArg (nameof value) ("Unsafe function name '" + string value + "'.")
            FunctionName value
        let value (FunctionName value) = value

    type ExtractField = private ExtractField of string

    module ExtractField =
        let private allowed = set [ "YEAR"; "MONTH"; "DAY"; "HOUR"; "MINUTE"; "SECOND"; "DOW"; "DOY"; "WEEK"; "QUARTER" ]
        let create (value: string) =
            let upper = value.ToUpperInvariant()
            if not (allowed.Contains upper) then invalidArg (nameof value) ("Unsupported EXTRACT field '" + value + "'.")
            ExtractField upper
        let value (ExtractField value) = value

    type Expr =
        | Column of Identifier
        | Wildcard of Identifier option
        | OrderOrdinal of PositiveRowCount
        | Literal of ScalarValue
        | Interval of IntervalLiteral
        | Unary of UnaryOperator * Expr
        | Binary of BinaryOperator * Expr * Expr
        | Like of value: Expr * pattern: Expr * escape: Expr option * negated: bool * caseInsensitive: bool
        | FunctionCall of FunctionCall
        | FilteredAggregate of Expr * Expr
        | Windowed of Expr * WindowSpec
        | Cast of Expr * CastType
        | Extract of ExtractField * Expr
        | SimpleCase of Expr * NonEmpty<SimpleCaseBranch> * Expr option
        | SearchedCase of NonEmpty<SearchedCaseBranch> * Expr option
        | InList of Expr * NonEmpty<Expr> * bool
        | InSubquery of Expr * Query * bool
        | Between of Expr * Expr * Expr * bool
        | IsNull of Expr * bool
        | ScalarSubquery of Query
        | Exists of Query * bool

    and FunctionCall =
        { Name: FunctionName
          Arguments: Expr list
          IsDistinct: bool }

    and SimpleCaseBranch = { Match: Expr; Result: Expr }
    and SearchedCaseBranch = { Condition: Expr; Result: Expr }
    and WindowSpec = { PartitionBy: Expr list; OrderBy: OrderBy list; Frame: WindowFrame option }
    and OrderBy = { Expression: Expr; Descending: bool; NullOrdering: NullOrdering }
    and SelectItem = { Expression: Expr; Alias: IdentifierPart option }

    and Cte =
        { Name: IdentifierPart
          ColumnAliases: IdentifierPart list
          Query: Query }

    and TableSource =
        | NamedTable of Identifier * IdentifierPart option
        | CteTable of Identifier * IdentifierPart option
        | DerivedTable of Query * IdentifierPart

    and Join =
        | CrossJoin of TableSource
        | OnJoin of OnJoinKind * TableSource * Expr
        member this.Kind =
            match this with
            | CrossJoin _ -> JoinKind.Cross
            | OnJoin(OnJoinKind.Inner, _, _) -> JoinKind.Inner
            | OnJoin(OnJoinKind.Left, _, _) -> JoinKind.Left
            | OnJoin(OnJoinKind.Right, _, _) -> JoinKind.Right
            | OnJoin(OnJoinKind.Full, _, _) -> JoinKind.Full
        member this.Source =
            match this with CrossJoin source | OnJoin(_, source, _) -> source
        member this.Predicate =
            match this with CrossJoin _ -> None | OnJoin(_, _, predicate) -> Some predicate

    and Select =
        { Ctes: Cte list
          Distinct: bool
          ProjectionItems: NonEmpty<SelectItem>
          From: TableSource option
          Joins: Join list
          Where: Expr option
          GroupBy: Expr list
          Having: Expr option }
        member this.Projection = NonEmpty.toList this.ProjectionItems

    and SetBranch = { Operator: SetOperator; Query: Query }

    and Query =
        { Head: Select
          SetOperations: SetBranch list
          OrderBy: OrderBy list
          Limit: NonNegativeRowCount option
          Offset: NonNegativeRowCount option }

    type Assignment = { Target: Identifier; Value: Expr }

    type ConflictAssignment =
        { Target: Identifier
          Proposed: Identifier }

    type InsertConflictAction =
        | DoNothing
        | UpdateProposedValues of NonEmpty<ConflictAssignment>

    type InsertConflict =
        { TargetColumns: NonEmpty<Identifier>
          Action: InsertConflictAction }

    type InsertInput =
        | Values of NonEmpty<NonEmpty<Expr>>
        | QuerySource of Query
        | DefaultValues

    type Insert =
        { Target: Identifier
          Columns: IdentifierPart list
          Input: InsertInput
          Conflict: InsertConflict option
          Returning: SelectItem list }
        member this.Rows =
            match this.Input with
            | Values rows -> rows |> NonEmpty.toList |> List.map NonEmpty.toList
            | QuerySource _ | DefaultValues -> []
        member this.Source =
            match this.Input with
            | QuerySource query -> Some query
            | Values _ | DefaultValues -> None

    type Update =
        { Target: Identifier
          AssignmentItems: NonEmpty<Assignment>
          From: TableSource list
          Where: Expr option
          Returning: SelectItem list }
        member this.Assignments = NonEmpty.toList this.AssignmentItems

    type Delete =
        { Target: Identifier
          Using: TableSource list
          Where: Expr option
          Returning: SelectItem list }

    type Statement =
        | QueryStatement of Query
        | InsertStatement of Insert
        | UpdateStatement of Update
        | DeleteStatement of Delete

    type Document = { Statement: Statement; Span: Span }
