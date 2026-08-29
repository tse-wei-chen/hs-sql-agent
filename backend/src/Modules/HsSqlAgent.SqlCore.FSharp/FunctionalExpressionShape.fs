namespace HsSqlAgent.SqlCore.Core.Lowering

open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding

/// Closed F# view over the compatibility AST used by native lowering.
/// Runtime C# inheritance is normalized once at this boundary so downstream
/// lowering can move toward exhaustive pattern matching incrementally.
module internal FunctionalExpressionShape =

    type ExpressionShape =
        | BoundColumn of BoundColumnExpr
        | Column of ColumnExpr
        | Literal of LiteralExpr
        | Interval of IntervalExpr
        | Unary of UnaryExpr
        | Binary of BinaryExpr
        | FunctionCall of FunctionCallExpr
        | Filter of FilterExpr
        | Windowed of WindowedExpr
        | Cast of CastExpr
        | SimpleCase of SimpleCaseExpr
        | SearchedCase of CaseExpr
        | InList of InExpr
        | Between of BetweenExpr
        | IsNull of IsNullExpr
        | ScalarSubquery of SubqueryExpr
        | Exists of ExistsExpr
        | Unsupported of SqlExpr

    let ofSqlExpr (expression: SqlExpr) =
        match expression with
        | :? BoundColumnExpr as value -> BoundColumn value
        | :? ColumnExpr as value -> Column value
        | :? LiteralExpr as value -> Literal value
        | :? IntervalExpr as value -> Interval value
        | :? UnaryExpr as value -> Unary value
        | :? BinaryExpr as value -> Binary value
        | :? FunctionCallExpr as value -> FunctionCall value
        | :? FilterExpr as value -> Filter value
        | :? WindowedExpr as value -> Windowed value
        | :? CastExpr as value -> Cast value
        | :? SimpleCaseExpr as value -> SimpleCase value
        | :? CaseExpr as value -> SearchedCase value
        | :? InExpr as value -> InList value
        | :? BetweenExpr as value -> Between value
        | :? IsNullExpr as value -> IsNull value
        | :? SubqueryExpr as value -> ScalarSubquery value
        | :? ExistsExpr as value -> Exists value
        | value -> Unsupported value
