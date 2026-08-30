namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Globalization
open System.Text.RegularExpressions
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel

module internal RewriteCastTypes =

    type private CastKind =
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

    type private TypeSpec =
        { Kind: CastKind
          Precision: int option
          Scale: int option }

    let private maxLengthSentinel = -1

    let private typePattern =
        Regex(
            "^(?<name>[A-Z_][A-Z0-9_.]*(?:\\s+[A-Z_]+)*?)(?:\\s*\\(\\s*(?<p>MAX|[0-9]+)(?:\\s*,\\s*(?<s>[0-9]+))?\\s*\\))?(?<suffix>(?:\\s+[A-Z_]+)*)$",
            RegexOptions.CultureInvariant)

    let private fail message : 'T = raise (SqlCompilationException(message))

    let private normalizeSpaces (value: string) =
        Regex.Replace(value.Trim(), "\\s+", " ").ToUpperInvariant()

    let private parseInt value =
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed -> parsed
        | _ -> fail ("CAST type argument '" + value + "' is outside the supported integer range.")

    let private validateArguments kind precision scale name =
        if scale.IsSome && precision.IsNone then
            fail ("CAST type '" + name + "' scale requires a precision.")
        match kind with
        | Decimal ->
            if precision = Some 0 then fail "DECIMAL/NUMERIC precision must be positive."
            match precision, scale with
            | Some p, Some s when s > p -> fail "DECIMAL/NUMERIC scale cannot exceed precision."
            | _ -> ()
        | FixedString | VariableString | FixedBinary | VariableBinary ->
            if precision = Some 0 then fail ("CAST type '" + name + "' length must be positive.")
            if scale.IsSome then fail ("CAST type '" + name + "' does not accept a scale.")
        | Time | TimeWithZone | Timestamp | TimestampWithZone ->
            if scale.IsSome then fail ("Temporal CAST type '" + name + "' accepts at most one precision argument.")
        | _ ->
            if precision.IsSome || scale.IsSome then
                fail ("CAST type '" + name + "' does not accept precision/scale in the Core type model.")

    let private classify name precision scale source =
        let kind =
            match name with
            | "BOOL" | "BOOLEAN" -> Boolean
            | "BIT" when source = SqlAgentToolType.MsSqlServer -> Boolean
            | "TINYINT" | "SMALLINT" | "INT2" -> SmallInteger
            | "INT" | "INTEGER" | "INT4" | "MEDIUMINT" -> Integer
            | "BIGINT" | "INT8" -> BigInteger
            | "SIGNED" | "SIGNED INTEGER" when source = SqlAgentToolType.MySQL -> BigInteger
            | "UNSIGNED" | "UNSIGNED INTEGER" when source = SqlAgentToolType.MySQL -> UnsignedBigInteger
            | "DEC" | "DECIMAL" | "NUMERIC" -> Decimal
            | "NUMBER" when source = SqlAgentToolType.Oracle -> Decimal
            | "REAL" | "FLOAT4" | "BINARY_FLOAT" -> Real
            | "DOUBLE" | "DOUBLE PRECISION" | "FLOAT8" | "BINARY_DOUBLE" -> Double
            | "FLOAT" when source = SqlAgentToolType.MySQL -> Real
            | "FLOAT" -> Double
            | "CHAR" | "CHARACTER" | "NCHAR" -> FixedString
            | "VARCHAR" | "CHAR VARYING" | "CHARACTER VARYING" | "VARCHAR2" | "NVARCHAR" | "NVARCHAR2" -> VariableString
            | "TEXT" | "NTEXT" | "CLOB" | "NCLOB" -> Text
            | "BINARY" -> FixedBinary
            | "VARBINARY" | "BYTEA" | "RAW" -> VariableBinary
            | "BLOB" | "IMAGE" -> BinaryLargeObject
            | "DATE" when source = SqlAgentToolType.Oracle -> Timestamp
            | "DATE" -> Date
            | "TIME" | "TIME WITHOUT TIME ZONE" -> Time
            | "TIME WITH TIME ZONE" | "TIMETZ" -> TimeWithZone
            | "TIMESTAMP" | "ROWVERSION" when source = SqlAgentToolType.MsSqlServer -> RowVersion
            | "TIMESTAMP" | "TIMESTAMP WITHOUT TIME ZONE" | "DATETIME" | "DATETIME2" | "SMALLDATETIME" -> Timestamp
            | "TIMESTAMP WITH TIME ZONE" | "TIMESTAMPTZ" | "DATETIMEOFFSET" -> TimestampWithZone
            | "UUID" | "UNIQUEIDENTIFIER" -> Uuid
            | "JSON" | "JSONB" -> Json
            | _ ->
                fail (
                    "CAST type '" + name + "' from source dialect " + string source
                    + " has no cross-dialect Core semantic mapping yet.")

        if precision = Some maxLengthSentinel then
            match kind with
            | VariableString -> { Kind = Text; Precision = None; Scale = None }
            | VariableBinary -> { Kind = BinaryLargeObject; Precision = None; Scale = None }
            | _ -> fail ("CAST type '" + name + "(MAX)' is not a modeled large-value type.")
        else
            validateArguments kind precision scale name
            { Kind = kind; Precision = precision; Scale = scale }

    let private bounded precision maximum target =
        match precision with
        | None -> None
        | Some p when p <= maximum -> Some p
        | Some p ->
            fail (
                "Temporal precision " + string p + " exceeds target provider " + string target
                + " maximum " + string maximum + " for a lossless CAST.")

    let private temporal name precision =
        match precision with None -> name | Some p -> name + "(" + string p + ")"

    let private temporalWithZone name precision =
        match precision with
        | None -> name + " WITH TIME ZONE"
        | Some p -> name + "(" + string p + ") WITH TIME ZONE"

    let private firebirdTemporal name precision =
        match precision with
        | Some p when p > 4 ->
            fail (
                "Temporal precision " + string p
                + " exceeds Firebird's four fractional-second digits for a lossless CAST.")
        | _ -> name

    let private renderDecimal spec target source =
        match spec.Precision with
        | None ->
            match target with
            | SqlAgentToolType.Postgres -> "NUMERIC"
            | SqlAgentToolType.MySQL -> "DECIMAL"
            | SqlAgentToolType.Sqlite -> "NUMERIC"
            | _ ->
                fail (
                    "Unbounded exact numeric CAST '" + source
                    + "' cannot be preserved losslessly by target provider " + string target
                    + "; specify precision and scale.")
        | Some p ->
            let suffix =
                match spec.Scale with
                | None -> "(" + string p + ")"
                | Some s -> "(" + string p + "," + string s + ")"
            (match target with
             | SqlAgentToolType.Oracle -> "NUMBER"
             | SqlAgentToolType.Sqlite -> "NUMERIC"
             | _ -> "DECIMAL") + suffix

    let private renderString spec target fixedWidth source =
        if not fixedWidth && spec.Precision.IsNone then
            match target with
            | SqlAgentToolType.Postgres -> "VARCHAR"
            | SqlAgentToolType.MySQL -> "CHAR"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | _ ->
                fail (
                    "Unbounded variable string CAST '" + source
                    + "' has no lossless target spelling for provider " + string target
                    + "; specify a length or use an explicit large-object type.")
        else
            let length = defaultArg spec.Precision 1
            match target with
            | SqlAgentToolType.Postgres -> (if fixedWidth then "CHAR" else "VARCHAR") + "(" + string length + ")"
            | SqlAgentToolType.MySQL -> "CHAR(" + string length + ")"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> (if fixedWidth then "NCHAR" else "NVARCHAR") + "(" + string length + ")"
            | SqlAgentToolType.Oracle -> (if fixedWidth then "NCHAR" else "NVARCHAR2") + "(" + string length + ")"
            | SqlAgentToolType.Firebird -> (if fixedWidth then "CHAR" else "VARCHAR") + "(" + string length + ")"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")

    let private renderBinary spec target source =
        match spec.Precision with
        | None ->
            match target with
            | SqlAgentToolType.Postgres -> "BYTEA"
            | SqlAgentToolType.MySQL -> "BINARY"
            | SqlAgentToolType.Sqlite -> "BLOB"
            | _ ->
                fail (
                    "Unbounded binary CAST '" + source
                    + "' has no lossless target spelling for provider " + string target
                    + "; specify a length or a binary large-object type.")
        | Some length ->
            match target with
            | SqlAgentToolType.Postgres -> "BYTEA"
            | SqlAgentToolType.MySQL -> "BINARY(" + string length + ")"
            | SqlAgentToolType.Sqlite -> "BLOB"
            | SqlAgentToolType.MsSqlServer -> "VARBINARY(" + string length + ")"
            | SqlAgentToolType.Oracle -> "RAW(" + string length + ")"
            | SqlAgentToolType.Firebird ->
                fail "Binary string CAST requires Firebird OCTETS character-set semantics, which are not modeled yet."
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")

    let private render spec target source =
        match spec.Kind with
        | Boolean ->
            match target with
            | SqlAgentToolType.Postgres -> "BOOLEAN"
            | SqlAgentToolType.MySQL -> "SIGNED"
            | SqlAgentToolType.Sqlite -> "INTEGER"
            | SqlAgentToolType.MsSqlServer -> "BIT"
            | SqlAgentToolType.Oracle -> "NUMBER(1)"
            | SqlAgentToolType.Firebird -> "BOOLEAN"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SmallInteger | Integer | BigInteger as kind ->
            match target with
            | SqlAgentToolType.Postgres | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Firebird ->
                match kind with SmallInteger -> "SMALLINT" | Integer -> "INTEGER" | _ -> "BIGINT"
            | SqlAgentToolType.MySQL -> "SIGNED"
            | SqlAgentToolType.Sqlite -> "INTEGER"
            | SqlAgentToolType.Oracle ->
                match kind with SmallInteger -> "NUMBER(5)" | Integer -> "NUMBER(10)" | _ -> "NUMBER(19)"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | UnsignedBigInteger ->
            match target with
            | SqlAgentToolType.Postgres -> "NUMERIC(20,0)"
            | SqlAgentToolType.MySQL -> "UNSIGNED"
            | SqlAgentToolType.Sqlite -> "NUMERIC"
            | SqlAgentToolType.MsSqlServer -> "DECIMAL(20,0)"
            | SqlAgentToolType.Oracle -> "NUMBER(20,0)"
            | SqlAgentToolType.Firebird -> "DECIMAL(20,0)"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | Decimal -> renderDecimal spec target source
        | Real ->
            match target with
            | SqlAgentToolType.Postgres | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Firebird -> "REAL"
            | SqlAgentToolType.MySQL -> "FLOAT"
            | SqlAgentToolType.Sqlite -> "REAL"
            | SqlAgentToolType.Oracle -> "BINARY_FLOAT"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | Double ->
            match target with
            | SqlAgentToolType.Postgres | SqlAgentToolType.Firebird -> "DOUBLE PRECISION"
            | SqlAgentToolType.MySQL -> "DOUBLE"
            | SqlAgentToolType.Sqlite -> "REAL"
            | SqlAgentToolType.MsSqlServer -> "FLOAT"
            | SqlAgentToolType.Oracle -> "BINARY_DOUBLE"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | FixedString -> renderString spec target true source
        | VariableString -> renderString spec target false source
        | Text ->
            match target with
            | SqlAgentToolType.Postgres -> "TEXT"
            | SqlAgentToolType.MySQL -> "CHAR"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.Oracle -> "CLOB"
            | SqlAgentToolType.MsSqlServer -> "NVARCHAR(MAX)"
            | SqlAgentToolType.Firebird ->
                fail "Text BLOB subtype CAST is not represented by the current Core CAST grammar for Firebird."
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | FixedBinary | VariableBinary -> renderBinary spec target source
        | BinaryLargeObject ->
            match target with
            | SqlAgentToolType.Postgres -> "BYTEA"
            | SqlAgentToolType.MySQL -> "BINARY"
            | SqlAgentToolType.Sqlite -> "BLOB"
            | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird -> "BLOB"
            | SqlAgentToolType.MsSqlServer -> "VARBINARY(MAX)"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | Date ->
            match target with
            | SqlAgentToolType.Postgres | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer
            | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird -> "DATE"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | Time ->
            match target with
            | SqlAgentToolType.Postgres -> temporal "TIME" (bounded spec.Precision 6 target)
            | SqlAgentToolType.MySQL -> temporal "TIME" (bounded spec.Precision 6 target)
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> temporal "TIME" (bounded spec.Precision 7 target)
            | SqlAgentToolType.Oracle -> fail "Oracle has no standalone TIME data type."
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIME" spec.Precision
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | TimeWithZone ->
            match target with
            | SqlAgentToolType.Postgres -> temporalWithZone "TIME" (bounded spec.Precision 6 target)
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIME WITH TIME ZONE" spec.Precision
            | _ -> fail ("TIME WITH TIME ZONE CAST '" + source + "' has no lossless target mapping for " + string target + ".")
        | Timestamp ->
            match target with
            | SqlAgentToolType.Postgres -> temporal "TIMESTAMP" (bounded spec.Precision 6 target)
            | SqlAgentToolType.MySQL -> temporal "DATETIME" (bounded spec.Precision 6 target)
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> temporal "DATETIME2" (bounded spec.Precision 7 target)
            | SqlAgentToolType.Oracle -> temporal "TIMESTAMP" (bounded spec.Precision 9 target)
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIMESTAMP" spec.Precision
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | TimestampWithZone ->
            match target with
            | SqlAgentToolType.Postgres -> temporalWithZone "TIMESTAMP" (bounded spec.Precision 6 target)
            | SqlAgentToolType.MySQL -> fail "MySQL CAST has no target type that preserves an explicit UTC offset."
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> temporal "DATETIMEOFFSET" (bounded spec.Precision 7 target)
            | SqlAgentToolType.Oracle -> temporalWithZone "TIMESTAMP" (bounded spec.Precision 9 target)
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIMESTAMP WITH TIME ZONE" spec.Precision
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | RowVersion ->
            match target with
            | SqlAgentToolType.Postgres -> "BYTEA"
            | SqlAgentToolType.MySQL -> "BINARY(8)"
            | SqlAgentToolType.Sqlite -> "BLOB"
            | SqlAgentToolType.MsSqlServer -> "BINARY(8)"
            | SqlAgentToolType.Oracle -> "RAW(8)"
            | SqlAgentToolType.Firebird ->
                fail "SQL Server rowversion/timestamp has no lossless Firebird CAST target in the current Core model."
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | Uuid ->
            match target with
            | SqlAgentToolType.Postgres -> "UUID"
            | SqlAgentToolType.MySQL -> "CHAR(36)"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> "UNIQUEIDENTIFIER"
            | SqlAgentToolType.Oracle -> "VARCHAR2(36)"
            | SqlAgentToolType.Firebird -> "CHAR(36)"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | Json ->
            match target with
            | SqlAgentToolType.Postgres -> "JSONB"
            | SqlAgentToolType.MySQL -> "JSON"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.Oracle -> "JSON"
            | SqlAgentToolType.MsSqlServer ->
                fail "JSON has no version-independent dedicated SQL Server CAST target in the current Core capability profile."
            | SqlAgentToolType.Firebird ->
                fail "JSON has no dedicated Firebird CAST target in the current Core model."
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")

    let normalize source target (castType: CastType) =
        let raw = CastType.value castType
        let normalized = normalizeSpaces raw
        let m = typePattern.Match(normalized)
        if not m.Success then
            fail ("CAST type '" + raw + "' is not a safe modeled type shape.")

        let first = m.Groups["name"].Value.Trim()
        let suffix = m.Groups["suffix"].Value.Trim()
        let name = if suffix.Length = 0 then first else first + " " + suffix

        let precision =
            if not m.Groups["p"].Success then None
            elif m.Groups["p"].Value = "MAX" then Some maxLengthSentinel
            else Some(parseInt m.Groups["p"].Value)
        let scale =
            if m.Groups["s"].Success then Some(parseInt m.Groups["s"].Value) else None

        if precision = Some maxLengthSentinel then
            if source <> SqlAgentToolType.MsSqlServer then
                fail "CAST type length MAX is supported only for SQL Server source syntax."
            if scale.IsSome then fail "CAST type MAX does not accept a scale."
            if name <> "VARCHAR" && name <> "NVARCHAR" && name <> "VARBINARY" then
                fail (
                    "SQL Server CAST type '" + name
                    + "' does not support the MAX length marker in the Core model.")

        if source = target then
            CastType.create normalized
        else
            classify name precision scale source
            |> fun spec -> render spec target normalized
            |> CastType.create
