namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Globalization
open System.Text.RegularExpressions
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel

module internal RewriteCastTypes =

    let private typePattern =
        Regex(
            "^(?<name>[A-Z_][A-Z0-9_.]*(?:\\s+[A-Z_]+)*?)(?:\\s*\\(\\s*(?<p>MAX|[0-9]+)(?:\\s*,\\s*(?<s>[0-9]+))?\\s*\\))?(?<suffix>(?:\\s+[A-Z_]+)*)$",
            RegexOptions.CultureInvariant)

    let private fail message : 'T = raise (SqlCompilationException(message))

    let private normalizeSpaces (value: string) =
        Regex.Replace(value.Trim(), "\\s+", " ").ToUpperInvariant()

    let private parseInt (value: string) =
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, parsed -> parsed
        | _ -> fail ("CAST type argument '" + value + "' is outside the supported integer range.")

    let private noArguments (name: string) (precision: int option) (scale: int option) (semantic: SqlType) =
        if precision.IsSome || scale.IsSome then
            fail ("CAST type '" + name + "' does not accept precision/scale in the Core type model.")
        semantic

    let private onePositiveArgument (name: string) (precision: int option) (scale: int option) (constructor: int option -> SqlType) =
        if scale.IsSome then
            fail ("CAST type '" + name + "' does not accept a scale.")
        match precision with
        | Some 0 -> fail ("CAST type '" + name + "' length must be positive.")
        | value -> constructor value

    let private temporalType (name: string) (precision: int option) (scale: int option) (withTimeZone: bool) (constructor: int option * bool -> SqlType) =
        if scale.IsSome then
            fail ("Temporal CAST type '" + name + "' accepts at most one precision argument.")
        constructor (precision, withTimeZone)

    let private decimalType (precision: int option) (scale: int option) =
        if scale.IsSome && precision.IsNone then
            fail "DECIMAL/NUMERIC scale requires a precision."
        if precision = Some 0 then fail "DECIMAL/NUMERIC precision must be positive."
        match precision, scale with
        | Some p, Some s when s > p -> fail "DECIMAL/NUMERIC scale cannot exceed precision."
        | _ -> SqlDecimal(precision, scale)

    let private providerNativeType
        (source: SqlAgentToolType)
        (first: string)
        (suffix: string)
        (precision: int option)
        (scale: int option)
        (isMax: bool) =
        let qualifiers =
            if String.IsNullOrWhiteSpace(suffix) then []
            else
                suffix.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map ProviderTypeQualifier.create
                |> Array.toList
        let arguments =
            match precision, scale with
            | None, None -> []
            | Some p, None -> [ ProviderTypeInteger p ]
            | Some p, Some s -> [ ProviderTypeInteger p; ProviderTypeInteger s ]
            | None, Some _ -> fail "Provider-native CAST scale requires a leading type argument."
        SqlProviderNative
            { Provider = source
              Name = ProviderTypeName.create first
              Qualifiers = qualifiers
              Arguments = arguments }

    let private classify (source: SqlAgentToolType) (first: string) (suffix: string) (name: string) (precision: int option) (scale: int option) (isMax: bool) =
        if isMax then
            if source <> SqlAgentToolType.MsSqlServer then
                fail "CAST type length MAX is supported only for SQL Server source syntax."
            if scale.IsSome then fail "CAST type MAX does not accept a scale."
            match name with
            | "VARCHAR" | "NVARCHAR" -> SqlText
            | "VARBINARY" -> SqlBinaryLargeObject
            | _ ->
                fail (
                    "SQL Server CAST type '" + name
                    + "' does not support the MAX length marker in the Core model.")
        else
            match name with
            | "BOOL" | "BOOLEAN" -> noArguments name precision scale SqlBoolean
            | "BIT" when source = SqlAgentToolType.MsSqlServer -> noArguments name precision scale SqlBoolean
            | "TINYINT" | "SMALLINT" | "INT2" -> noArguments name precision scale SqlSmallInteger
            | "INT" | "INTEGER" | "INT4" | "MEDIUMINT" -> noArguments name precision scale SqlInteger
            | "BIGINT" | "INT8" -> noArguments name precision scale SqlBigInteger
            | "SIGNED" | "SIGNED INTEGER" when source = SqlAgentToolType.MySQL ->
                noArguments name precision scale SqlBigInteger
            | "UNSIGNED" | "UNSIGNED INTEGER" when source = SqlAgentToolType.MySQL ->
                noArguments name precision scale SqlUnsignedBigInteger
            | "DEC" | "DECIMAL" | "NUMERIC" -> decimalType precision scale
            | "NUMBER" when source = SqlAgentToolType.Oracle -> decimalType precision scale
            | "REAL" | "FLOAT4" | "BINARY_FLOAT" -> noArguments name precision scale SqlReal
            | "DOUBLE" | "DOUBLE PRECISION" | "FLOAT8" | "BINARY_DOUBLE" ->
                noArguments name precision scale SqlDouble
            | "FLOAT" when source = SqlAgentToolType.MySQL -> noArguments name precision scale SqlReal
            | "FLOAT" -> noArguments name precision scale SqlDouble
            | "CHAR" | "CHARACTER" | "NCHAR" ->
                onePositiveArgument name precision scale SqlFixedString
            | "VARCHAR" | "CHAR VARYING" | "CHARACTER VARYING" | "VARCHAR2" | "NVARCHAR" | "NVARCHAR2" ->
                onePositiveArgument name precision scale (fun length ->
                    SqlVariableString length)
            | "TEXT" | "NTEXT" | "CLOB" | "NCLOB" ->
                noArguments name precision scale SqlText
            | "BINARY" -> onePositiveArgument name precision scale SqlFixedBinary
            | "VARBINARY" | "BYTEA" | "RAW" ->
                onePositiveArgument name precision scale (fun length ->
                    SqlVariableBinary length)
            | "BLOB" | "IMAGE" -> noArguments name precision scale SqlBinaryLargeObject
            | "DATE" when source = SqlAgentToolType.Oracle ->
                noArguments name precision scale (SqlTimestamp(None, false))
            | "DATE" -> noArguments name precision scale SqlDate
            | "TIME" | "TIME WITHOUT TIME ZONE" ->
                temporalType name precision scale false SqlTime
            | "TIME WITH TIME ZONE" | "TIMETZ" ->
                temporalType name precision scale true SqlTime
            | "TIMESTAMP" | "ROWVERSION" when source = SqlAgentToolType.MsSqlServer ->
                noArguments name precision scale SqlRowVersion
            | "TIMESTAMP" | "TIMESTAMP WITHOUT TIME ZONE" | "DATETIME" | "DATETIME2" | "SMALLDATETIME" ->
                temporalType name precision scale false SqlTimestamp
            | "TIMESTAMP WITH TIME ZONE" | "TIMESTAMPTZ" | "DATETIMEOFFSET" ->
                temporalType name precision scale true SqlTimestamp
            | "UUID" | "UNIQUEIDENTIFIER" -> noArguments name precision scale SqlUuid
            | "JSON" | "JSONB" -> noArguments name precision scale SqlJson
            | _ -> providerNativeType source first suffix precision scale isMax

    let parseSource source (raw: string) =
        let normalized = normalizeSpaces raw
        let m = typePattern.Match(normalized)
        if not m.Success then
            fail ("CAST type '" + raw + "' is not a safe modeled type shape.")

        let first = m.Groups["name"].Value.Trim()
        let suffix = m.Groups["suffix"].Value.Trim()
        let name = if suffix.Length = 0 then first else first + " " + suffix

        let isMax = m.Groups["p"].Success && m.Groups["p"].Value = "MAX"
        let precision =
            if not m.Groups["p"].Success || isMax then None
            else Some(parseInt m.Groups["p"].Value)
        let scale =
            if m.Groups["s"].Success then Some(parseInt m.Groups["s"].Value) else None

        let semantic = classify source first suffix name precision scale isMax
        CastType.modeled source semantic normalized

    let private bounded (precision: int option) (maximum: int) (target: SqlAgentToolType) =
        match precision with
        | None -> None
        | Some p when p <= maximum -> Some p
        | Some p ->
            fail (
                "Temporal precision " + string p + " exceeds target provider " + string target
                + " maximum " + string maximum + " for a lossless CAST.")

    let private temporal (name: string) (precision: int option) =
        match precision with None -> name | Some p -> name + "(" + string p + ")"

    let private temporalWithZone (name: string) (precision: int option) =
        match precision with
        | None -> name + " WITH TIME ZONE"
        | Some p -> name + "(" + string p + ") WITH TIME ZONE"

    let private firebirdTemporal (name: string) (precision: int option) =
        match precision with
        | Some p when p > 4 ->
            fail (
                "Temporal precision " + string p
                + " exceeds Firebird's four fractional-second digits for a lossless CAST.")
        | _ -> name

    let private renderDecimal (precision: int option) (scale: int option) (target: SqlAgentToolType) (source: string) =
        match precision with
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
                match scale with
                | None -> "(" + string p + ")"
                | Some s -> "(" + string p + "," + string s + ")"
            (match target with
             | SqlAgentToolType.Oracle -> "NUMBER"
             | SqlAgentToolType.Sqlite -> "NUMERIC"
             | _ -> "DECIMAL") + suffix

    let private renderString (length: int option) (target: SqlAgentToolType) (fixedWidth: bool) (source: string) =
        if not fixedWidth && length.IsNone then
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
            let length = defaultArg length 1
            match target with
            | SqlAgentToolType.Postgres -> (if fixedWidth then "CHAR" else "VARCHAR") + "(" + string length + ")"
            | SqlAgentToolType.MySQL -> "CHAR(" + string length + ")"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> (if fixedWidth then "NCHAR" else "NVARCHAR") + "(" + string length + ")"
            | SqlAgentToolType.Oracle -> (if fixedWidth then "NCHAR" else "NVARCHAR2") + "(" + string length + ")"
            | SqlAgentToolType.Firebird -> (if fixedWidth then "CHAR" else "VARCHAR") + "(" + string length + ")"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")

    let private renderBinary (length: int option) (target: SqlAgentToolType) (source: string) =
        match length with
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

    let private renderSemantic (semantic: SqlType) (target: SqlAgentToolType) (source: string) =
        match semantic with
        | SqlBoolean ->
            match target with
            | SqlAgentToolType.Postgres -> "BOOLEAN"
            | SqlAgentToolType.MySQL -> "SIGNED"
            | SqlAgentToolType.Sqlite -> "INTEGER"
            | SqlAgentToolType.MsSqlServer -> "BIT"
            | SqlAgentToolType.Oracle -> "NUMBER(1)"
            | SqlAgentToolType.Firebird -> "BOOLEAN"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlSmallInteger | SqlInteger | SqlBigInteger as kind ->
            match target with
            | SqlAgentToolType.Postgres | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Firebird ->
                match kind with SqlSmallInteger -> "SMALLINT" | SqlInteger -> "INTEGER" | _ -> "BIGINT"
            | SqlAgentToolType.MySQL -> "SIGNED"
            | SqlAgentToolType.Sqlite -> "INTEGER"
            | SqlAgentToolType.Oracle ->
                match kind with SqlSmallInteger -> "NUMBER(5)" | SqlInteger -> "NUMBER(10)" | _ -> "NUMBER(19)"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlUnsignedBigInteger ->
            match target with
            | SqlAgentToolType.Postgres -> "NUMERIC(20,0)"
            | SqlAgentToolType.MySQL -> "UNSIGNED"
            | SqlAgentToolType.Sqlite -> "NUMERIC"
            | SqlAgentToolType.MsSqlServer -> "DECIMAL(20,0)"
            | SqlAgentToolType.Oracle -> "NUMBER(20,0)"
            | SqlAgentToolType.Firebird -> "DECIMAL(20,0)"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlDecimal(precision, scale) -> renderDecimal precision scale target source
        | SqlReal ->
            match target with
            | SqlAgentToolType.Postgres | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Firebird -> "REAL"
            | SqlAgentToolType.MySQL -> "FLOAT"
            | SqlAgentToolType.Sqlite -> "REAL"
            | SqlAgentToolType.Oracle -> "BINARY_FLOAT"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlDouble ->
            match target with
            | SqlAgentToolType.Postgres | SqlAgentToolType.Firebird -> "DOUBLE PRECISION"
            | SqlAgentToolType.MySQL -> "DOUBLE"
            | SqlAgentToolType.Sqlite -> "REAL"
            | SqlAgentToolType.MsSqlServer -> "FLOAT"
            | SqlAgentToolType.Oracle -> "BINARY_DOUBLE"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlFixedString length -> renderString length target true source
        | SqlVariableString length ->
            renderString length target false source
        | SqlText ->
            match target with
            | SqlAgentToolType.Postgres -> "TEXT"
            | SqlAgentToolType.MySQL -> "CHAR"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.Oracle -> "CLOB"
            | SqlAgentToolType.MsSqlServer -> "NVARCHAR(MAX)"
            | SqlAgentToolType.Firebird ->
                fail "Text BLOB subtype CAST is not represented by the current Core CAST grammar for Firebird."
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlFixedBinary length -> renderBinary length target source
        | SqlVariableBinary length ->
            renderBinary length target source
        | SqlBinaryLargeObject ->
            match target with
            | SqlAgentToolType.Postgres -> "BYTEA"
            | SqlAgentToolType.MySQL -> "BINARY"
            | SqlAgentToolType.Sqlite -> "BLOB"
            | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird -> "BLOB"
            | SqlAgentToolType.MsSqlServer -> "VARBINARY(MAX)"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlDate ->
            match target with
            | SqlAgentToolType.Postgres | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer
            | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird -> "DATE"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlTime(precision, false) ->
            match target with
            | SqlAgentToolType.Postgres -> temporal "TIME" (bounded precision 6 target)
            | SqlAgentToolType.MySQL -> temporal "TIME" (bounded precision 6 target)
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> temporal "TIME" (bounded precision 7 target)
            | SqlAgentToolType.Oracle -> fail "Oracle has no standalone TIME data type."
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIME" precision
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlTime(precision, true) ->
            match target with
            | SqlAgentToolType.Postgres -> temporalWithZone "TIME" (bounded precision 6 target)
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIME WITH TIME ZONE" precision
            | _ -> fail ("TIME WITH TIME ZONE CAST '" + source + "' has no lossless target mapping for " + string target + ".")
        | SqlTimestamp(precision, false) ->
            match target with
            | SqlAgentToolType.Postgres -> temporal "TIMESTAMP" (bounded precision 6 target)
            | SqlAgentToolType.MySQL -> temporal "DATETIME" (bounded precision 6 target)
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> temporal "DATETIME2" (bounded precision 7 target)
            | SqlAgentToolType.Oracle -> temporal "TIMESTAMP" (bounded precision 9 target)
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIMESTAMP" precision
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlTimestamp(precision, true) ->
            match target with
            | SqlAgentToolType.Postgres -> temporalWithZone "TIMESTAMP" (bounded precision 6 target)
            | SqlAgentToolType.MySQL -> fail "MySQL CAST has no target type that preserves an explicit UTC offset."
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> temporal "DATETIMEOFFSET" (bounded precision 7 target)
            | SqlAgentToolType.Oracle -> temporalWithZone "TIMESTAMP" (bounded precision 9 target)
            | SqlAgentToolType.Firebird -> firebirdTemporal "TIMESTAMP WITH TIME ZONE" precision
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlRowVersion ->
            match target with
            | SqlAgentToolType.Postgres -> "BYTEA"
            | SqlAgentToolType.MySQL -> "BINARY(8)"
            | SqlAgentToolType.Sqlite -> "BLOB"
            | SqlAgentToolType.MsSqlServer -> "BINARY(8)"
            | SqlAgentToolType.Oracle -> "RAW(8)"
            | SqlAgentToolType.Firebird ->
                fail "SQL Server rowversion/timestamp has no lossless Firebird CAST target in the current Core model."
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlUuid ->
            match target with
            | SqlAgentToolType.Postgres -> "UUID"
            | SqlAgentToolType.MySQL -> "CHAR(36)"
            | SqlAgentToolType.Sqlite -> "TEXT"
            | SqlAgentToolType.MsSqlServer -> "UNIQUEIDENTIFIER"
            | SqlAgentToolType.Oracle -> "VARCHAR2(36)"
            | SqlAgentToolType.Firebird -> "CHAR(36)"
            | _ -> fail ("CAST type '" + source + "' has no Core target mapping for provider " + string target + ".")
        | SqlJson ->
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
        | SqlProviderNative nativeType ->
            if nativeType.Provider = target then source
            else
                fail (
                    "CAST type '" + source + "' from source dialect " + string nativeType.Provider
                    + " has no cross-dialect Core semantic mapping yet.")

    let renderTarget (target: SqlAgentToolType) (castType: CastType) =
        match CastType.semantic castType, CastType.sourceProvider castType with
        | Some semantic, Some sourceProvider when sourceProvider = target ->
            CastType.value castType
        | Some semantic, Some _ ->
            renderSemantic semantic target (CastType.value castType)
        | _ ->
            fail "Compatibility raw CAST type reached rendering before semantic normalization."

    let validateTarget (target: SqlAgentToolType) (castType: CastType) =
        renderTarget target castType |> ignore

    let normalize (source: SqlAgentToolType) (_target: SqlAgentToolType) (castType: CastType) =
        let modeled =
            match CastType.semantic castType with
            | Some _ -> castType
            | None -> parseSource source (CastType.value castType)

        match CastType.sourceProvider modeled with
        | Some provider when provider <> source ->
            fail (
                "CAST type source-provider invariant mismatch: modeled for " + string provider
                + " but compiled from " + string source + ".")
        | _ -> ()

        modeled
