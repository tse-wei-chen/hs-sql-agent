namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.CoreModel

/// Shared static PostgreSQL Function primitive return-type observations.
/// The ordinary closed Query AST and Function-only positional AST consume
/// these same rules. No server/catalog/authorization/execution proof is implied.
module internal RewritePrimitiveTypeEvidence =

    let integerType = RewriteScalarTypeEvidence.integerType

    let literalType literal = RewriteScalarTypeEvidence.literalPrimitive literal

    let literalCompatible declared literal =
        match declared, literal with
        | SqlFunctionReturnType.Integer, ScalarValue.Integer n ->
            n >= int64 Int32.MinValue && n <= int64 Int32.MaxValue
        | SqlFunctionReturnType.BigInteger, ScalarValue.Integer _ -> true
        | SqlFunctionReturnType.Boolean, ScalarValue.Boolean _ -> true
        | SqlFunctionReturnType.Text, ScalarValue.Text _ -> true
        | _ -> false

    let private isInteger = function
        | SqlFunctionReturnType.Integer | SqlFunctionReturnType.BigInteger -> true
        | _ -> false

    /// Closed subset only: never guess at provider-specific overloaded
    /// operator resolution, mixed scalar families, or NULL semantics.
    let unaryType op operand =
        match op, operand with
        | UnaryOperator.Not, SqlFunctionReturnType.Boolean ->
            Some SqlFunctionReturnType.Boolean
        | (UnaryOperator.Positive | UnaryOperator.Negate), value
            when isInteger value -> Some value
        | _ -> None

    let binaryType op left right =
        match op with
        | BinaryOperator.And | BinaryOperator.Or when
            left = SqlFunctionReturnType.Boolean
            && right = SqlFunctionReturnType.Boolean ->
            Some SqlFunctionReturnType.Boolean
        | BinaryOperator.Add | BinaryOperator.Subtract | BinaryOperator.Multiply
            when isInteger left && isInteger right ->
            if left = SqlFunctionReturnType.BigInteger
               || right = SqlFunctionReturnType.BigInteger then
                Some SqlFunctionReturnType.BigInteger
            else Some SqlFunctionReturnType.Integer
        | _ -> None

    /// Parameter CAST: exact primitive type or int4 -> int8 only.
    let parameterCastType source target =
        if source = target
           || (source = SqlFunctionReturnType.Integer
               && target = SqlFunctionReturnType.BigInteger) then Some target
        else None

    let castTargetType (target: CastType) =
        match CastType.semantic target with
        | Some SqlType.SqlInteger -> Some SqlFunctionReturnType.Integer
        | Some SqlType.SqlBigInteger -> Some SqlFunctionReturnType.BigInteger
        | Some SqlType.SqlBoolean -> Some SqlFunctionReturnType.Boolean
        | Some SqlType.SqlText -> Some SqlFunctionReturnType.Text
        | _ -> None
