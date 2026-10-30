namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.CoreModel

/// Internal type-evidence boundary shared by schema-family validation and
/// PostgreSQL Function exact primitive inference. None of these variants
/// constitutes an executable plan, a server type/overload lookup, or rights.
type internal StaticScalarTypeEvidence =
    /// NULL and unsupported/unmodeled scalar types must never become known.
    | Unproven
    /// Schema-snapshot validation knows only a family, not a concrete SQL type.
    | FamilyOnly of SqlSchemaValueKind
    /// A closed literal supports a specific modeled primitive SQL type shape.
    | ExactPrimitive of SqlFunctionReturnType

module internal RewriteScalarTypeEvidence =

    let integerType (number: int64) =
        if number >= int64 Int32.MinValue && number <= int64 Int32.MaxValue then
            SqlFunctionReturnType.Integer
        else SqlFunctionReturnType.BigInteger

    /// Pure, lossless-in-certainty projection from exact primitive to family.
    /// No reverse FamilyOnly -> ExactPrimitive operation exists.
    let private primitiveFamily = function
        | SqlFunctionReturnType.Integer | SqlFunctionReturnType.BigInteger ->
            Some SqlSchemaValueKind.Numeric
        | SqlFunctionReturnType.Boolean -> Some SqlSchemaValueKind.Boolean
        | SqlFunctionReturnType.Text -> Some SqlSchemaValueKind.Text
        | SqlFunctionReturnType.Void -> None

    let ofLiteral = function
        | ScalarValue.Null -> Unproven
        | ScalarValue.Integer number -> ExactPrimitive (integerType number)
        | ScalarValue.Boolean _ -> ExactPrimitive SqlFunctionReturnType.Boolean
        | ScalarValue.Text _ -> ExactPrimitive SqlFunctionReturnType.Text
        | ScalarValue.Decimal _ | ScalarValue.ExactNumeric _ | ScalarValue.Floating _ ->
            FamilyOnly SqlSchemaValueKind.Numeric
        | ScalarValue.Date _ | ScalarValue.Time _ | ScalarValue.LocalDateTime _
        | ScalarValue.OffsetDateTime _ | ScalarValue.Duration _ ->
            FamilyOnly SqlSchemaValueKind.Temporal
        | ScalarValue.Bytes _ -> FamilyOnly SqlSchemaValueKind.Binary

    let family = function
        | Unproven -> None
        | FamilyOnly SqlSchemaValueKind.Unknown -> None
        | FamilyOnly known -> Some known
        | ExactPrimitive exact -> primitiveFamily exact

    /// Only a first-party, modeled primitive closed syntax can produce exact
    /// evidence. Family-only decimal/float/temporal/binary values cannot.
    let exact = function
        | ExactPrimitive primitive when primitive <> SqlFunctionReturnType.Void ->
            Some primitive
        | Unproven | FamilyOnly _ | ExactPrimitive _ -> None

    let literalFamily literal = literal |> ofLiteral |> family
    let literalPrimitive literal = literal |> ofLiteral |> exact

    let hasFamily expected primitive =
        (ExactPrimitive primitive |> family) = Some expected
