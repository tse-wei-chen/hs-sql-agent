namespace HsSqlAgent.SqlCore.Core.Normalization

open System
open System.Globalization
open System.Text.RegularExpressions
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums

[<RequireQualifiedAccess>]
type private CastTypeKind =
    | Boolean
    | SmallInteger
    | Integer
    | BigInteger
    | UnsignedBigInteger
    | Decimal
    | Real
    | Double
    | FixedString
    | VariableString
    | Text
    | FixedBinary
    | VariableBinary
    | BinaryLargeObject
    | Date
    | Time
    | TimeWithZone
    | Timestamp
    | TimestampWithZone
    | RowVersion
    | Uuid
    | Json

type private CastTypeSpec =
    { Kind: CastTypeKind
      Precision: int option
      Scale: int option }

module private FunctionalCastTypeNormalization =

    let private typePattern =
        Regex(
            @"^(?<name>[A-Z_][A-Z0-9_.]*(?:\s+[A-Z_]+)*?)(?:\s*\(\s*(?<p>MAX|[0-9]+)(?:\s*,\s*(?<s>[0-9]+))?\s*\))?(?<suffix>(?:\s+[A-Z_]+)*)$",
            RegexOptions.CultureInvariant)

    [<Literal>]
    let private MaxLengthSentinel = -1

    let private fail message = raise (SqlCompilationException(message))

    let private unsupported target source =
        fail $"CAST type '{source}' has no Core target mapping for provider {target}."

    let private combineTypeName (name: string) (suffix: string) =
        let tail = suffix.Trim()
        if String.IsNullOrEmpty(tail) then name.Trim()
        else name.Trim() + " " + tail

    let private parseOptionalInt (group: Group) =
        if not group.Success then None
        else
            match Int32.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, value -> Some value
            | _ -> fail $"CAST type argument '{group.Value}' is outside the supported integer range."

    let private parsePrecision (group: Group) =
        if not group.Success then None
        elif group.Value.Equals("MAX", StringComparison.OrdinalIgnoreCase) then Some MaxLengthSentinel
        else parseOptionalInt group

    let private validateMaxShape name precision scale source =
        match precision with
        | Some p when p = MaxLengthSentinel ->
            if source <> SqlAgentToolType.MsSqlServer then
                fail "CAST type length MAX is supported only for SQL Server source syntax."
            if scale.IsSome then
                fail "CAST type MAX does not accept a scale."
            if name <> "VARCHAR" && name <> "NVARCHAR" && name <> "VARBINARY" then
                fail $"SQL Server CAST type '{name}' does not support the MAX length marker in the Core model."
        | _ -> ()

    let private validateArguments kind precision scale name =
        if scale.IsSome && precision.IsNone then
            fail $"CAST type '{name}' scale requires a precision."

        match kind with
        | CastTypeKind.Decimal ->
            if precision = Some 0 then
                fail "DECIMAL/NUMERIC precision must be positive."
            match precision, scale with
            | Some p, Some s when s > p ->
                fail "DECIMAL/NUMERIC scale cannot exceed precision."
            | _ -> ()
        | CastTypeKind.FixedString
        | CastTypeKind.VariableString
        | CastTypeKind.FixedBinary
        | CastTypeKind.VariableBinary ->
            if precision = Some 0 then
                fail $"CAST type '{name}' length must be positive."
            if scale.IsSome then
                fail $"CAST type '{name}' does not accept a scale."
        | CastTypeKind.Time
        | CastTypeKind.TimeWithZone
        | CastTypeKind.Timestamp
        | CastTypeKind.TimestampWithZone ->
            if scale.IsSome then
                fail $"Temporal CAST type '{name}' accepts at most one precision argument."
        | _ ->
            if precision.IsSome || scale.IsSome then
                fail $"CAST type '{name}' does not accept precision/scale in the Core type model."

    let private classify name precision scale source =
        let kind =
            match name with
            | "BOOL" | "BOOLEAN" -> CastTypeKind.Boolean
            | "BIT" when source = SqlAgentToolType.MsSqlServer -> CastTypeKind.Boolean
            | "TINYINT" | "SMALLINT" | "INT2" -> CastTypeKind.SmallInteger
            | "INT" | "INTEGER" | "INT4" | "MEDIUMINT" -> CastTypeKind.Integer
            | "BIGINT" | "INT8" -> CastTypeKind.BigInteger
            | "SIGNED" | "SIGNED INTEGER" when source = SqlAgentToolType.MySQL -> CastTypeKind.BigInteger
            | "UNSIGNED" | "UNSIGNED INTEGER" when source = SqlAgentToolType.MySQL -> CastTypeKind.UnsignedBigInteger
            | "DEC" | "DECIMAL" | "NUMERIC" -> CastTypeKind.Decimal
            | "NUMBER" when source = SqlAgentToolType.Oracle -> CastTypeKind.Decimal
            | "REAL" | "FLOAT4" | "BINARY_FLOAT" -> CastTypeKind.Real
            | "DOUBLE" | "DOUBLE PRECISION" | "FLOAT8" | "BINARY_DOUBLE" -> CastTypeKind.Double
            | "FLOAT" when source = SqlAgentToolType.MySQL -> CastTypeKind.Real
            | "FLOAT" -> CastTypeKind.Double
            | "CHAR" | "CHARACTER" | "NCHAR" -> CastTypeKind.FixedString
            | "VARCHAR" | "CHAR VARYING" | "CHARACTER VARYING" | "VARCHAR2" | "NVARCHAR" | "NVARCHAR2" -> CastTypeKind.VariableString
            | "TEXT" | "NTEXT" | "CLOB" | "NCLOB" -> CastTypeKind.Text
            | "BINARY" -> CastTypeKind.FixedBinary
            | "VARBINARY" | "BYTEA" | "RAW" -> CastTypeKind.VariableBinary
            | "BLOB" | "IMAGE" -> CastTypeKind.BinaryLargeObject
            | "DATE" when source = SqlAgentToolType.Oracle -> CastTypeKind.Timestamp
            | "DATE" -> CastTypeKind.Date
            | "TIME" | "TIME WITHOUT TIME ZONE" -> CastTypeKind.Time
            | "TIME WITH TIME ZONE" | "TIMETZ" -> CastTypeKind.TimeWithZone
            | "TIMESTAMP" | "ROWVERSION" when source = SqlAgentToolType.MsSqlServer -> CastTypeKind.RowVersion
            | "TIMESTAMP" | "TIMESTAMP WITHOUT TIME ZONE" | "DATETIME" | "DATETIME2" | "SMALLDATETIME" -> CastTypeKind.Timestamp
            | "TIMESTAMP WITH TIME ZONE" | "TIMESTAMPTZ" | "DATETIMEOFFSET" -> CastTypeKind.TimestampWithZone
            | "UUID" | "UNIQUEIDENTIFIER" -> CastTypeKind.Uuid
            | "JSON" | "JSONB" -> CastTypeKind.Json
            | _ -> fail $"CAST type '{name}' from source dialect {source} has no cross-dialect Core semantic mapping yet."

        match precision with
        | Some p when p = MaxLengthSentinel ->
            match kind with
            | CastTypeKind.VariableString -> { Kind = CastTypeKind.Text; Precision = None; Scale = None }
            | CastTypeKind.VariableBinary -> { Kind = CastTypeKind.BinaryLargeObject; Precision = None; Scale = None }
            | _ -> fail $"CAST type '{name}(MAX)' is not a modeled large-value type."
        | _ ->
            validateArguments kind precision scale name
            { Kind = kind; Precision = precision; Scale = scale }

    let private boundedPrecision precision max target =
        match precision with
        | Some value when value > max ->
            fail $"Temporal precision {value} exceeds target provider {target} maximum {max} for a lossless CAST."
        | _ -> precision

    let private temporal name precision =
        match precision with
        | None -> name
        | Some value -> name + "(" + value.ToString(CultureInfo.InvariantCulture) + ")"

    let private temporalWithZone name precision =
        match precision with
        | None -> name + " WITH TIME ZONE"
        | Some value -> name + "(" + value.ToString(CultureInfo.InvariantCulture) + ") WITH TIME ZONE"

    let private firebirdTemporal name precision =
        match precision with
        | Some value when value > 4 ->
            fail $"Temporal precision {value} exceeds Firebird's four fractional-second digits for a lossless CAST."
        | _ -> name

    let private renderInteger target portableName oracleName source =
        match target with
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.MsSqlServer
        | SqlAgentToolType.Firebird -> portableName
        | SqlAgentToolType.MySQL -> "SIGNED"
        | SqlAgentToolType.Sqlite -> "INTEGER"
        | SqlAgentToolType.Oracle -> oracleName
        | _ -> unsupported target source

    let private renderDecimal spec target source =
        match spec.Precision with
        | None ->
            match target with
            | SqlAgentToolType.Postgres -> "NUMERIC"
            | SqlAgentToolType.MySQL -> "DECIMAL"
            | SqlAgentToolType.Sqlite -> "NUMERIC"
            | _ -> fail $"Unbounded exact numeric CAST '{source}' cannot be preserved losslessly by target provider {target}; specify precision and scale."
        | Some precision ->
            let suffix =
                match spec.Scale with
                | None -> "(" + precision.ToString(CultureInfo.InvariantCulture) + ")"
                | Some scale -> "(" + precision.ToString(CultureInfo.InvariantCulture) + "," + scale.ToString(CultureInfo.InvariantCulture) + ")"
            let name =
                match target with
                | SqlAgentToolType.Oracle -> "NUMBER"
                | SqlAgentToolType.Sqlite -> "NUMERIC"
                | _ -> "DECIMAL"
            name + suffix

    let private renderString spec target fixedWidth source =
        match fixedWidth, spec.Precision with
        | false, None ->
            match target with
            | SqlAgentToolType.Postgres -> "VARCHAR"
            | SqlAgentToolType.MySQL -> "CHAR"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | _ -> fail $"Unbounded variable string CAST '{source}' has no lossless target spelling for provider {target}; specify a length or use an explicit large-object type."
        | _ ->
            let length = defaultArg spec.Precision 1
            match target with
            | SqlAgentToolType.Postgres -> (if fixedWidth then "CHAR" else "VARCHAR") + $"({length})"
            | SqlAgentToolType.MySQL -> $"CHAR({length})"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> (if fixedWidth then "NCHAR" else "NVARCHAR") + $"({length})"
            | SqlAgentToolType.Oracle -> (if fixedWidth then "NCHAR" else "NVARCHAR2") + $"({length})"
            | SqlAgentToolType.Firebird -> (if fixedWidth then "CHAR" else "VARCHAR") + $"({length})"
            | _ -> unsupported target source

    let private renderText target source =
        match target with
        | SqlAgentToolType.Postgres -> "TEXT"
        | SqlAgentToolType.MySQL -> "CHAR"
        | SqlAgentToolType.Sqlite -> "TEXT"
        | SqlAgentToolType.Oracle -> "CLOB"
        | SqlAgentToolType.MsSqlServer -> "NVARCHAR(MAX)"
        | SqlAgentToolType.Firebird -> fail "Text BLOB subtype CAST is not represented by the current Core CAST grammar for Firebird."
        | _ -> unsupported target source

    let private renderBinary spec target source =
        match spec.Precision with
        | None ->
            match target with
            | SqlAgentToolType.Postgres -> "BYTEA"
            | SqlAgentToolType.MySQL -> "BINARY"
            | SqlAgentToolType.Sqlite -> "BLOB"
            | _ -> fail $"Unbounded binary CAST '{source}' has no lossless target spelling for provider {target}; specify a length or a binary large-object type."
        | Some length ->
            match target with
            | SqlAgentToolType.Postgres -> "BYTEA"
            | SqlAgentToolType.MySQL -> $"BINARY({length})"
            | SqlAgentToolType.Sqlite -> "BLOB"
            | SqlAgentToolType.MsSqlServer -> $"VARBINARY({length})"
            | SqlAgentToolType.Oracle -> $"RAW({length})"
            | SqlAgentToolType.Firebird -> fail "Binary string CAST requires Firebird OCTETS character-set semantics, which are not modeled yet."
            | _ -> unsupported target source

    let private renderBinaryLargeObject target source =
        match target with
        | SqlAgentToolType.Postgres -> "BYTEA"
        | SqlAgentToolType.MySQL -> "BINARY"
        | SqlAgentToolType.Sqlite -> "BLOB"
        | SqlAgentToolType.Oracle
        | SqlAgentToolType.Firebird -> "BLOB"
        | SqlAgentToolType.MsSqlServer -> "VARBINARY(MAX)"
        | _ -> unsupported target source

    let private renderTime spec target withZone source =
        if withZone then
            match target with
            | SqlAgentToolType.Postgres -> temporalWithZone "TIME" (boundedPrecision spec.Precision 6 target)
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIME WITH TIME ZONE" spec.Precision
            | _ -> fail $"TIME WITH TIME ZONE CAST '{source}' has no lossless target mapping for {target}."
        else
            match target with
            | SqlAgentToolType.Postgres -> temporal "TIME" (boundedPrecision spec.Precision 6 target)
            | SqlAgentToolType.MySQL -> temporal "TIME" (boundedPrecision spec.Precision 6 target)
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> temporal "TIME" (boundedPrecision spec.Precision 7 target)
            | SqlAgentToolType.Oracle -> fail "Oracle has no standalone TIME data type."
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIME" spec.Precision
            | _ -> unsupported target source

    let private renderTimestamp spec target withZone source =
        if withZone then
            match target with
            | SqlAgentToolType.Postgres -> temporalWithZone "TIMESTAMP" (boundedPrecision spec.Precision 6 target)
            | SqlAgentToolType.MySQL -> fail "MySQL CAST has no target type that preserves an explicit UTC offset."
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> temporal "DATETIMEOFFSET" (boundedPrecision spec.Precision 7 target)
            | SqlAgentToolType.Oracle -> temporalWithZone "TIMESTAMP" (boundedPrecision spec.Precision 9 target)
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIMESTAMP WITH TIME ZONE" spec.Precision
            | _ -> unsupported target source
        else
            match target with
            | SqlAgentToolType.Postgres -> temporal "TIMESTAMP" (boundedPrecision spec.Precision 6 target)
            | SqlAgentToolType.MySQL -> temporal "DATETIME" (boundedPrecision spec.Precision 6 target)
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> temporal "DATETIME2" (boundedPrecision spec.Precision 7 target)
            | SqlAgentToolType.Oracle -> temporal "TIMESTAMP" (boundedPrecision spec.Precision 9 target)
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIMESTAMP" spec.Precision
            | _ -> unsupported target source

    let private render spec target source =
        match spec.Kind with
        | CastTypeKind.Boolean ->
            match target with
            | SqlAgentToolType.Postgres -> "BOOLEAN"
            | SqlAgentToolType.MySQL -> "SIGNED"
            | SqlAgentToolType.Sqlite -> "INTEGER"
            | SqlAgentToolType.MsSqlServer -> "BIT"
            | SqlAgentToolType.Oracle -> "NUMBER(1)"
            | SqlAgentToolType.Firebird -> "BOOLEAN"
            | _ -> unsupported target source
        | CastTypeKind.SmallInteger -> renderInteger target "SMALLINT" "NUMBER(5)" source
        | CastTypeKind.Integer -> renderInteger target "INTEGER" "NUMBER(10)" source
        | CastTypeKind.BigInteger -> renderInteger target "BIGINT" "NUMBER(19)" source
        | CastTypeKind.UnsignedBigInteger ->
            match target with
            | SqlAgentToolType.Postgres -> "NUMERIC(20,0)"
            | SqlAgentToolType.MySQL -> "UNSIGNED"
            | SqlAgentToolType.Sqlite -> "NUMERIC"
            | SqlAgentToolType.MsSqlServer -> "DECIMAL(20,0)"
            | SqlAgentToolType.Oracle -> "NUMBER(20,0)"
            | SqlAgentToolType.Firebird -> "DECIMAL(20,0)"
            | _ -> unsupported target source
        | CastTypeKind.Decimal -> renderDecimal spec target source
        | CastTypeKind.Real ->
            match target with
            | SqlAgentToolType.Postgres
            | SqlAgentToolType.MsSqlServer
            | SqlAgentToolType.Firebird -> "REAL"
            | SqlAgentToolType.MySQL -> "FLOAT"
            | SqlAgentToolType.Sqlite -> "REAL"
            | SqlAgentToolType.Oracle -> "BINARY_FLOAT"
            | _ -> unsupported target source
        | CastTypeKind.Double ->
            match target with
            | SqlAgentToolType.Postgres
            | SqlAgentToolType.Firebird -> "DOUBLE PRECISION"
            | SqlAgentToolType.MySQL -> "DOUBLE"
            | SqlAgentToolType.Sqlite -> "REAL"
            | SqlAgentToolType.MsSqlServer -> "FLOAT"
            | SqlAgentToolType.Oracle -> "BINARY_DOUBLE"
            | _ -> unsupported target source
        | CastTypeKind.FixedString -> renderString spec target true source
        | CastTypeKind.VariableString -> renderString spec target false source
        | CastTypeKind.Text -> renderText target source
        | CastTypeKind.FixedBinary
        | CastTypeKind.VariableBinary -> renderBinary spec target source
        | CastTypeKind.BinaryLargeObject -> renderBinaryLargeObject target source
        | CastTypeKind.Date ->
            match target with
            | SqlAgentToolType.Postgres
            | SqlAgentToolType.MySQL
            | SqlAgentToolType.MsSqlServer
            | SqlAgentToolType.Oracle
            | SqlAgentToolType.Firebird -> "DATE"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | _ -> unsupported target source
        | CastTypeKind.Time -> renderTime spec target false source
        | CastTypeKind.TimeWithZone -> renderTime spec target true source
        | CastTypeKind.Timestamp -> renderTimestamp spec target false source
        | CastTypeKind.TimestampWithZone -> renderTimestamp spec target true source
        | CastTypeKind.RowVersion ->
            match target with
            | SqlAgentToolType.Postgres -> "BYTEA"
            | SqlAgentToolType.MySQL -> "BINARY(8)"
            | SqlAgentToolType.Sqlite -> "BLOB"
            | SqlAgentToolType.MsSqlServer -> "BINARY(8)"
            | SqlAgentToolType.Oracle -> "RAW(8)"
            | SqlAgentToolType.Firebird -> fail "SQL Server rowversion/timestamp has no lossless Firebird CAST target in the current Core model."
            | _ -> unsupported target source
        | CastTypeKind.Uuid ->
            match target with
            | SqlAgentToolType.Postgres -> "UUID"
            | SqlAgentToolType.MySQL -> "CHAR(36)"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> "UNIQUEIDENTIFIER"
            | SqlAgentToolType.Oracle -> "VARCHAR2(36)"
            | SqlAgentToolType.Firebird -> "CHAR(36)"
            | _ -> unsupported target source
        | CastTypeKind.Json ->
            match target with
            | SqlAgentToolType.Postgres -> "JSONB"
            | SqlAgentToolType.MySQL -> "JSON"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.Oracle -> "JSON"
            | SqlAgentToolType.MsSqlServer -> fail "JSON has no version-independent dedicated SQL Server CAST target in the current Core capability profile."
            | SqlAgentToolType.Firebird -> fail "JSON has no dedicated Firebird CAST target in the current Core model."
            | _ -> unsupported target source

    let normalize typeName sourceDialect targetProvider =
        if String.IsNullOrWhiteSpace(typeName) then
            fail "CAST target type cannot be empty."

        let normalized =
            Regex.Replace(typeName.Trim(), @"\s+", " ").ToUpperInvariant()
        let matched = typePattern.Match(normalized)
        if not matched.Success then
            fail $"CAST type '{typeName}' is not a safe modeled type shape."

        let name = combineTypeName matched.Groups["name"].Value matched.Groups["suffix"].Value
        let precision = parsePrecision matched.Groups["p"]
        let scale = parseOptionalInt matched.Groups["s"]
        validateMaxShape name precision scale sourceDialect

        if sourceDialect = targetProvider then normalized
        else
            classify name precision scale sourceDialect
            |> fun spec -> render spec targetProvider normalized

/// F# ownership of source-semantic CAST type normalization and cross-dialect target spelling.
[<AbstractClass; Sealed>]
type internal CoreCastTypeNormalizer private () =
    static member Normalize(
        typeName: string,
        sourceDialect: SqlAgentToolType,
        targetProvider: SqlAgentToolType) =
        FunctionalCastTypeNormalization.normalize typeName sourceDialect targetProvider
