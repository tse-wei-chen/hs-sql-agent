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

    type UnaryOperator =
        | Not
        | Negate
        | Positive

    type BinaryOperator =
        | Add
        | Subtract
        | Multiply
        | Divide
        | Modulo
        | Concat
        | Equal
        | NotEqual
        | GreaterThan
        | LessThan
        | GreaterThanOrEqual
        | LessThanOrEqual
        | Like
        | ILike
        | And
        | Or

    type JoinKind =
        | Inner
        | Left
        | Right
        | Full
        | Cross

    /// Predicate-bearing joins cannot be CROSS joins by construction.
    type OnJoinKind =
        | Inner
        | Left
        | Right
        | Full

    type NullOrdering =
        | Default
        | NullsFirst
        | NullsLast

    type SetOperator =
        | Union
        | UnionAll
        | Intersect
        | Except

    type WindowFrameUnit =
        | Rows
        | Range

    type WindowFrameBound =
        | UnboundedPreceding
        | Preceding of int
        | CurrentRow
        | Following of int
        | UnboundedFollowing

    type WindowFrame =
        { Unit: WindowFrameUnit
          Start: WindowFrameBound
          End: WindowFrameBound option }

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

    type CastType = private CastType of string

    module CastType =
        let private safeCastType =
            Regex("^[A-Za-z][A-Za-z0-9_ ]*(\\([0-9]+(,[0-9]+)?\\))?$", RegexOptions.CultureInvariant)

        let create value =
            if String.IsNullOrWhiteSpace(value) || not (safeCastType.IsMatch(value)) then
                invalidArg (nameof value) ("Unsafe CAST type '" + string value + "'.")
            CastType value

        let value (CastType value) = value

    type FunctionName = private FunctionName of string

    module FunctionName =
        let private safeFunctionName =
            Regex("^[A-Za-z_][A-Za-z0-9_$.]*$", RegexOptions.CultureInvariant)

        let create value =
            if String.IsNullOrWhiteSpace(value) || not (safeFunctionName.IsMatch(value)) then
                invalidArg (nameof value) ("Unsafe function name '" + string value + "'.")
            FunctionName value

        let value (FunctionName value) = value

    type Expr =
        | Column of Identifier
        | Literal of ScalarValue
        | Interval of string
        | Unary of UnaryOperator * Expr
        | Binary of BinaryOperator * Expr * Expr
        | FunctionCall of FunctionCall
        | FilteredAggregate of Expr * Expr
        | Windowed of Expr * WindowSpec
        | Cast of Expr * CastType
        | SimpleCase of Expr * SimpleCaseBranch list * Expr option
        | SearchedCase of SearchedCaseBranch list * Expr option
        | InList of Expr * Expr list * bool
        | Between of Expr * Expr * Expr * bool
        | IsNull of Expr * bool
        | ScalarSubquery of Query
        | Exists of Query * bool

    and FunctionCall =
        { Name: FunctionName
          Arguments: Expr list
          IsDistinct: bool }

    and SimpleCaseBranch =
        { Match: Expr
          Result: Expr }

    and SearchedCaseBranch =
        { Condition: Expr
          Result: Expr }

    and WindowSpec =
        { PartitionBy: Expr list
          OrderBy: OrderBy list
          Frame: WindowFrame option }

    and OrderBy =
        { Expression: Expr
          Descending: bool
          NullOrdering: NullOrdering }

    and SelectItem =
        { Expression: Expr
          Alias: IdentifierPart option }

    and TableSource =
        | NamedTable of Identifier * IdentifierPart option
        | DerivedTable of Query * IdentifierPart

    /// Join shape encodes the ON-predicate invariant directly:
    /// CROSS JOIN has no predicate; every other join always has one.
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
            match this with
            | CrossJoin source
            | OnJoin(_, source, _) -> source
        member this.Predicate =
            match this with
            | CrossJoin _ -> None
            | OnJoin(_, _, predicate) -> Some predicate

    and Select =
        { Distinct: bool
          Projection: SelectItem list
          From: TableSource option
          Joins: Join list
          Where: Expr option
          GroupBy: Expr list
          Having: Expr option }

    and SetBranch =
        { Operator: SetOperator
          Query: Query }

    and Query =
        { Head: Select
          SetOperations: SetBranch list
          OrderBy: OrderBy list
          Limit: int option
          Offset: int option }

    type Assignment =
        { Target: Identifier
          Value: Expr }

    /// INSERT source is exactly one of VALUES, query source, or DEFAULT VALUES.
    type InsertInput =
        | Values of Expr list list
        | QuerySource of Query
        | DefaultValues

    type Insert =
        { Target: Identifier
          Columns: IdentifierPart list
          Input: InsertInput
          Returning: SelectItem list }
        member this.Rows =
            match this.Input with
            | Values rows -> rows
            | QuerySource _
            | DefaultValues -> []
        member this.Source =
            match this.Input with
            | QuerySource query -> Some query
            | Values _
            | DefaultValues -> None

    type Update =
        { Target: Identifier
          Assignments: Assignment list
          Where: Expr option
          Returning: SelectItem list }

    type Delete =
        { Target: Identifier
          Where: Expr option
          Returning: SelectItem list }

    type Statement =
        | QueryStatement of Query
        | InsertStatement of Insert
        | UpdateStatement of Update
        | DeleteStatement of Delete

    type Document =
        { Statement: Statement
          Span: Span }
