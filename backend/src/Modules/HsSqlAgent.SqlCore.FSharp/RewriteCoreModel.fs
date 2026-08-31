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
          PreserveSpelling: bool
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

        let equivalent left right =
            let leftParts = parts left
            let rightParts = parts right
            leftParts.Length = rightParts.Length
            && List.forall2
                (fun leftPart rightPart ->
                    leftPart.Value = rightPart.Value
                    && leftPart.WasQuoted = rightPart.WasQuoted
                    && leftPart.PreserveSpelling = rightPart.PreserveSpelling)
                leftParts
                rightParts

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
        let private safeCastType =
            Regex(
                "^[A-Za-z_][A-Za-z0-9_.]*(?:\\s+[A-Za-z_]+)*(?:\\s*\\(\\s*(?:MAX|[0-9]+)(?:\\s*,\\s*[0-9]+)?\\s*\\))?(?:\\s+[A-Za-z_]+)*$",
                RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)
        let create value =
            if String.IsNullOrWhiteSpace(value) || not (safeCastType.IsMatch(value)) then
                raise (HsSqlAgent.SqlCore.Core.Compilation.SqlCompilationException(
                    "CAST type '" + string value + "' is not a safe modeled type shape."))
            CastType(Regex.Replace(value.Trim(), "\\s+", " ").ToUpperInvariant())
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
        let private allowed =
            set [
                "YEAR"; "MONTH"; "DAY"; "QUARTER"
                "HOUR"; "MINUTE"; "SECOND"
                "DOW"; "DOY"; "ISODOW"; "ISOYEAR"; "WEEK"
                "EPOCH"; "CENTURY"; "DECADE"; "MILLENNIUM"; "JULIAN"
                "MILLISECONDS"; "MICROSECONDS"
                "TIMEZONE"; "TIMEZONE_HOUR"; "TIMEZONE_MINUTE"
            ]
        let create (value: string) =
            let upper = value.ToUpperInvariant()
            if not (allowed.Contains upper) then invalidArg (nameof value) ("Unsupported EXTRACT field '" + value + "'.")
            ExtractField upper
        let value (ExtractField value) = value

    type LikeEscape = private LikeEscape of char

    module LikeEscape =
        let create value =
            if Char.IsControl(value) then invalidArg (nameof value) "LIKE ESCAPE requires exactly one non-control character."
            LikeEscape value

        let value (LikeEscape value) = value

    type ColumnBinding =
        | LocalRowSource
        | OuterRowSource
        | ProjectionAlias

    type Expr =
        | Column of Identifier
        | BoundColumn of Identifier * ColumnBinding
        | Wildcard of Identifier option
        | OrderOrdinal of PositiveRowCount
        | Literal of ScalarValue
        | Interval of IntervalLiteral
        | Unary of UnaryOperator * Expr
        | Binary of BinaryOperator * Expr * Expr
        | Like of value: Expr * pattern: Expr * escape: LikeEscape option * negated: bool * caseInsensitive: bool
        | RawRegexCall of arguments: Expr list * isDistinct: bool
        | RegexMatch of value: Expr * pattern: Expr
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
          IsDistinct: bool
          AggregateOrderBy: OrderBy list
          AggregateOrderSyntax: AggregateOrderSyntax
          AggregateSeparator: string option }

    and AggregateOrderSyntax =
        | NoAggregateOrder
        | InlineAggregateOrder
        | WithinGroupAggregateOrder

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

    type ReturningItem =
        | ReturningColumn of Identifier * IdentifierPart option
        | ReturningWildcard of IdentifierPart option
        | ReturningExpression of Expr * IdentifierPart option
        member this.Expression =
            match this with
            | ReturningColumn(identifier, _) -> Column identifier
            | ReturningWildcard _ -> Wildcard None
            | ReturningExpression(expression, _) -> expression
        member this.Alias =
            match this with
            | ReturningColumn(_, alias)
            | ReturningWildcard alias
            | ReturningExpression(_, alias) -> alias

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
          Returning: ReturningItem list }
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
          TargetAlias: IdentifierPart option
          AssignmentItems: NonEmpty<Assignment>
          From: TableSource list
          Where: Expr option
          Returning: ReturningItem list }
        member this.Assignments = NonEmpty.toList this.AssignmentItems

    type Delete =
        { Target: Identifier
          TargetAlias: IdentifierPart option
          Using: TableSource list
          Where: Expr option
          Returning: ReturningItem list }

    type Statement =
        | QueryStatement of Query
        | InsertStatement of Insert
        | UpdateStatement of Update
        | DeleteStatement of Delete

    type Document = { Statement: Statement; Span: Span }

    module Expr =
        let private optionEquivalent (comparer: 'a -> 'a -> bool) (left: 'a option) (right: 'a option) =
            match left, right with
            | None, None -> true
            | Some leftValue, Some rightValue -> comparer leftValue rightValue
            | _ -> false

        let private listEquivalent (comparer: 'a -> 'a -> bool) (left: 'a list) (right: 'a list) =
            List.length left = List.length right && List.forall2 comparer left right

        let rec equivalent (left: Expr) (right: Expr) =
            match left, right with
            | Column leftId, Column rightId
            | Column leftId, BoundColumn(rightId, _)
            | BoundColumn(leftId, _), Column rightId
            | BoundColumn(leftId, _), BoundColumn(rightId, _) ->
                Identifier.equivalent leftId rightId
            | Wildcard leftId, Wildcard rightId ->
                optionEquivalent Identifier.equivalent leftId rightId
            | OrderOrdinal leftOrdinal, OrderOrdinal rightOrdinal ->
                PositiveRowCount.value leftOrdinal = PositiveRowCount.value rightOrdinal
            | Literal leftValue, Literal rightValue ->
                leftValue = rightValue
            | Interval leftValue, Interval rightValue ->
                IntervalLiteral.value leftValue = IntervalLiteral.value rightValue
            | Unary(leftOperator, leftValue), Unary(rightOperator, rightValue) ->
                leftOperator = rightOperator && equivalent leftValue rightValue
            | Binary(leftOperator, leftLeft, leftRight), Binary(rightOperator, rightLeft, rightRight) ->
                leftOperator = rightOperator
                && equivalent leftLeft rightLeft
                && equivalent leftRight rightRight
            | Like(leftValue, leftPattern, leftEscape, leftNegated, leftInsensitive),
              Like(rightValue, rightPattern, rightEscape, rightNegated, rightInsensitive) ->
                leftNegated = rightNegated
                && leftInsensitive = rightInsensitive
                && leftEscape = rightEscape
                && equivalent leftValue rightValue
                && equivalent leftPattern rightPattern
            | RawRegexCall(leftArguments, leftDistinct), RawRegexCall(rightArguments, rightDistinct) ->
                leftDistinct = rightDistinct
                && listEquivalent equivalent leftArguments rightArguments
            | RegexMatch(leftValue, leftPattern), RegexMatch(rightValue, rightPattern) ->
                equivalent leftValue rightValue
                && equivalent leftPattern rightPattern
            | FunctionCall leftCall, FunctionCall rightCall ->
                leftCall.Name = rightCall.Name
                && leftCall.IsDistinct = rightCall.IsDistinct
                && leftCall.AggregateOrderSyntax = rightCall.AggregateOrderSyntax
                && leftCall.AggregateSeparator = rightCall.AggregateSeparator
                && listEquivalent equivalent leftCall.Arguments rightCall.Arguments
                && listEquivalent orderEquivalent leftCall.AggregateOrderBy rightCall.AggregateOrderBy
            | FilteredAggregate(leftValue, leftPredicate), FilteredAggregate(rightValue, rightPredicate) ->
                equivalent leftValue rightValue && equivalent leftPredicate rightPredicate
            | Windowed(leftValue, leftWindow), Windowed(rightValue, rightWindow) ->
                equivalent leftValue rightValue
                && listEquivalent equivalent leftWindow.PartitionBy rightWindow.PartitionBy
                && listEquivalent orderEquivalent leftWindow.OrderBy rightWindow.OrderBy
                && leftWindow.Frame = rightWindow.Frame
            | Cast(leftValue, leftType), Cast(rightValue, rightType) ->
                leftType = rightType && equivalent leftValue rightValue
            | Extract(leftField, leftValue), Extract(rightField, rightValue) ->
                leftField = rightField && equivalent leftValue rightValue
            | SimpleCase(leftInput, leftBranches, leftFallback),
              SimpleCase(rightInput, rightBranches, rightFallback) ->
                equivalent leftInput rightInput
                && listEquivalent
                    (fun leftBranch rightBranch ->
                        equivalent leftBranch.Match rightBranch.Match
                        && equivalent leftBranch.Result rightBranch.Result)
                    (NonEmpty.toList leftBranches)
                    (NonEmpty.toList rightBranches)
                && optionEquivalent equivalent leftFallback rightFallback
            | SearchedCase(leftBranches, leftFallback),
              SearchedCase(rightBranches, rightFallback) ->
                listEquivalent
                    (fun leftBranch rightBranch ->
                        equivalent leftBranch.Condition rightBranch.Condition
                        && equivalent leftBranch.Result rightBranch.Result)
                    (NonEmpty.toList leftBranches)
                    (NonEmpty.toList rightBranches)
                && optionEquivalent equivalent leftFallback rightFallback
            | InList(leftValue, leftItems, leftNegated), InList(rightValue, rightItems, rightNegated) ->
                leftNegated = rightNegated
                && equivalent leftValue rightValue
                && listEquivalent equivalent (NonEmpty.toList leftItems) (NonEmpty.toList rightItems)
            | Between(leftValue, leftLower, leftUpper, leftNegated),
              Between(rightValue, rightLower, rightUpper, rightNegated) ->
                leftNegated = rightNegated
                && equivalent leftValue rightValue
                && equivalent leftLower rightLower
                && equivalent leftUpper rightUpper
            | IsNull(leftValue, leftNegated), IsNull(rightValue, rightNegated) ->
                leftNegated = rightNegated && equivalent leftValue rightValue
            | InSubquery _, InSubquery _
            | ScalarSubquery _, ScalarSubquery _
            | Exists _, Exists _ ->
                false
            | _ ->
                false

        and private orderEquivalent (left: OrderBy) (right: OrderBy) =
            left.Descending = right.Descending
            && left.NullOrdering = right.NullOrdering
            && equivalent left.Expression right.Expression
