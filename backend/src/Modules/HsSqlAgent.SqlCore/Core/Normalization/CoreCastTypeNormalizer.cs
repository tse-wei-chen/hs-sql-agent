using System.Globalization;
using System.Text.RegularExpressions;

namespace HsSqlAgent.SqlCore.Core.Normalization;

/// <summary>
/// Gives CAST types source-dialect semantics before selecting a target-dialect CAST spelling.
/// Native same-dialect casts keep every safe vendor type; cross-dialect casts use the modeled
/// semantic families below so identically spelled but incompatible types are never passed through.
/// </summary>
internal static class CoreCastTypeNormalizer
{
    private static readonly Regex TypePattern = new(
        @"^(?<name>[A-Z_][A-Z0-9_.]*(?:\s+[A-Z_]+)*?)(?:\s*\(\s*(?<p>MAX|[0-9]+)(?:\s*,\s*(?<s>[0-9]+))?\s*\))?(?<suffix>(?:\s+[A-Z_]+)*)$",
        RegexOptions.CultureInvariant);

    private const int MaxLengthSentinel = -1;

    public static string Normalize(
        string typeName,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            throw new SqlCompilationException("CAST target type cannot be empty.");

        var normalized = Regex.Replace(typeName.Trim(), @"\s+", " ").ToUpperInvariant();
        var match = TypePattern.Match(normalized);
        if (!match.Success)
            throw new SqlCompilationException($"CAST type '{typeName}' is not a safe modeled type shape.");

        var name = CombineTypeName(match.Groups["name"].Value, match.Groups["suffix"].Value);
        var precision = ParsePrecision(match.Groups["p"]);
        var scale = ParseOptionalInt(match.Groups["s"]);
        ValidateMaxShape(name, precision, scale, sourceDialect);

        // Do not shrink native SQL coverage just because a vendor extension has no portable
        // semantic mapping yet. Lowering still applies the safe CAST-type grammar.
        if (sourceDialect == targetProvider)
            return normalized;

        var spec = Classify(name, precision, scale, sourceDialect);
        return Render(spec, targetProvider, normalized);
    }

    private static string CombineTypeName(string name, string suffix)
    {
        var tail = suffix.Trim();
        return string.IsNullOrEmpty(tail) ? name.Trim() : $"{name.Trim()} {tail}";
    }

    private static TypeSpec Classify(string name, int? p, int? s, SqlAgentToolType source)
    {
        var kind = name switch
        {
            "BOOL" or "BOOLEAN" => TypeKind.Boolean,
            "BIT" when source == SqlAgentToolType.MsSqlServer => TypeKind.Boolean,

            "TINYINT" or "SMALLINT" or "INT2" => TypeKind.SmallInteger,
            "INT" or "INTEGER" or "INT4" or "MEDIUMINT" => TypeKind.Integer,
            "BIGINT" or "INT8" => TypeKind.BigInteger,
            "SIGNED" or "SIGNED INTEGER" when source == SqlAgentToolType.MySQL => TypeKind.BigInteger,
            "UNSIGNED" or "UNSIGNED INTEGER" when source == SqlAgentToolType.MySQL => TypeKind.UnsignedBigInteger,

            "DEC" or "DECIMAL" or "NUMERIC" => TypeKind.Decimal,
            "NUMBER" when source == SqlAgentToolType.Oracle => TypeKind.Decimal,
            "REAL" or "FLOAT4" or "BINARY_FLOAT" => TypeKind.Real,
            "DOUBLE" or "DOUBLE PRECISION" or "FLOAT8" or "BINARY_DOUBLE" => TypeKind.Double,
            "FLOAT" when source == SqlAgentToolType.MySQL => TypeKind.Real,
            "FLOAT" => TypeKind.Double,

            "CHAR" or "CHARACTER" or "NCHAR" => TypeKind.FixedString,
            "VARCHAR" or "CHAR VARYING" or "CHARACTER VARYING" or "VARCHAR2" or
            "NVARCHAR" or "NVARCHAR2" => TypeKind.VariableString,
            "TEXT" or "NTEXT" or "CLOB" or "NCLOB" => TypeKind.Text,

            "BINARY" => TypeKind.FixedBinary,
            "VARBINARY" or "BYTEA" or "RAW" => TypeKind.VariableBinary,
            "BLOB" or "IMAGE" => TypeKind.BinaryLargeObject,

            // Oracle DATE includes a time-of-day component; treating it as canonical DATE would
            // silently discard semantics when translating to providers with a date-only type.
            "DATE" when source == SqlAgentToolType.Oracle => TypeKind.Timestamp,
            "DATE" => TypeKind.Date,
            "TIME" or "TIME WITHOUT TIME ZONE" => TypeKind.Time,
            "TIME WITH TIME ZONE" or "TIMETZ" => TypeKind.TimeWithZone,

            // T-SQL timestamp is the deprecated spelling of rowversion, not a temporal type.
            "TIMESTAMP" or "ROWVERSION" when source == SqlAgentToolType.MsSqlServer => TypeKind.RowVersion,
            "TIMESTAMP" or "TIMESTAMP WITHOUT TIME ZONE" or "DATETIME" or "DATETIME2" or
            "SMALLDATETIME" => TypeKind.Timestamp,
            "TIMESTAMP WITH TIME ZONE" or "TIMESTAMPTZ" or "DATETIMEOFFSET" => TypeKind.TimestampWithZone,

            "UUID" or "UNIQUEIDENTIFIER" => TypeKind.Uuid,
            "JSON" or "JSONB" => TypeKind.Json,
            _ => throw new SqlCompilationException(
                $"CAST type '{name}' from source dialect {source} has no cross-dialect Core semantic mapping yet.")
        };

        if (p == MaxLengthSentinel)
        {
            return kind switch
            {
                TypeKind.VariableString => new TypeSpec(TypeKind.Text, null, null),
                TypeKind.VariableBinary => new TypeSpec(TypeKind.BinaryLargeObject, null, null),
                _ => throw new SqlCompilationException(
                    $"CAST type '{name}(MAX)' is not a modeled large-value type.")
            };
        }

        ValidateArguments(kind, p, s, name);
        return new TypeSpec(kind, p, s);
    }

    private static void ValidateMaxShape(string name, int? p, int? s, SqlAgentToolType source)
    {
        if (p != MaxLengthSentinel) return;
        if (source != SqlAgentToolType.MsSqlServer)
            throw new SqlCompilationException("CAST type length MAX is supported only for SQL Server source syntax.");
        if (s is not null)
            throw new SqlCompilationException("CAST type MAX does not accept a scale.");
        if (name is not ("VARCHAR" or "NVARCHAR" or "VARBINARY"))
            throw new SqlCompilationException(
                $"SQL Server CAST type '{name}' does not support the MAX length marker in the Core model.");
    }

    private static void ValidateArguments(TypeKind kind, int? p, int? s, string name)
    {
        if (s is not null && p is null)
            throw new SqlCompilationException($"CAST type '{name}' scale requires a precision.");

        if (kind == TypeKind.Decimal)
        {
            if (p is 0) throw new SqlCompilationException("DECIMAL/NUMERIC precision must be positive.");
            if (s is not null && p is not null && s > p)
                throw new SqlCompilationException("DECIMAL/NUMERIC scale cannot exceed precision.");
            return;
        }

        if (kind is TypeKind.FixedString or TypeKind.VariableString
            or TypeKind.FixedBinary or TypeKind.VariableBinary)
        {
            if (p is 0) throw new SqlCompilationException($"CAST type '{name}' length must be positive.");
            if (s is not null) throw new SqlCompilationException($"CAST type '{name}' does not accept a scale.");
            return;
        }

        if (kind is TypeKind.Time or TypeKind.TimeWithZone or TypeKind.Timestamp or TypeKind.TimestampWithZone)
        {
            if (s is not null)
                throw new SqlCompilationException($"Temporal CAST type '{name}' accepts at most one precision argument.");
            return;
        }

        if (p is not null || s is not null)
            throw new SqlCompilationException($"CAST type '{name}' does not accept precision/scale in the Core type model.");
    }

    private static string Render(TypeSpec type, SqlAgentToolType target, string source) => type.Kind switch
    {
        TypeKind.Boolean => target switch
        {
            SqlAgentToolType.Postgres => "BOOLEAN",
            SqlAgentToolType.MySQL => "SIGNED",
            SqlAgentToolType.Sqlite => "INTEGER",
            SqlAgentToolType.MsSqlServer => "BIT",
            SqlAgentToolType.Oracle => "NUMBER(1)",
            SqlAgentToolType.Firebird => "BOOLEAN",
            _ => Unsupported(target, source)
        },
        TypeKind.SmallInteger => RenderInteger(target, "SMALLINT", "NUMBER(5)", source),
        TypeKind.Integer => RenderInteger(target, "INTEGER", "NUMBER(10)", source),
        TypeKind.BigInteger => RenderInteger(target, "BIGINT", "NUMBER(19)", source),
        TypeKind.UnsignedBigInteger => target switch
        {
            SqlAgentToolType.Postgres => "NUMERIC(20,0)",
            SqlAgentToolType.MySQL => "UNSIGNED",
            SqlAgentToolType.Sqlite => "NUMERIC",
            SqlAgentToolType.MsSqlServer => "DECIMAL(20,0)",
            SqlAgentToolType.Oracle => "NUMBER(20,0)",
            SqlAgentToolType.Firebird => "DECIMAL(20,0)",
            _ => Unsupported(target, source)
        },
        TypeKind.Decimal => RenderDecimal(type, target, source),
        TypeKind.Real => target switch
        {
            SqlAgentToolType.Postgres or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird => "REAL",
            SqlAgentToolType.MySQL => "FLOAT",
            SqlAgentToolType.Sqlite => "REAL",
            SqlAgentToolType.Oracle => "BINARY_FLOAT",
            _ => Unsupported(target, source)
        },
        TypeKind.Double => target switch
        {
            SqlAgentToolType.Postgres or SqlAgentToolType.Firebird => "DOUBLE PRECISION",
            SqlAgentToolType.MySQL => "DOUBLE",
            SqlAgentToolType.Sqlite => "REAL",
            SqlAgentToolType.MsSqlServer => "FLOAT",
            SqlAgentToolType.Oracle => "BINARY_DOUBLE",
            _ => Unsupported(target, source)
        },
        TypeKind.FixedString => RenderString(type, target, fixedWidth: true, source),
        TypeKind.VariableString => RenderString(type, target, fixedWidth: false, source),
        TypeKind.Text => RenderText(target, source),
        TypeKind.FixedBinary => RenderBinary(type, target, source),
        TypeKind.VariableBinary => RenderBinary(type, target, source),
        TypeKind.BinaryLargeObject => RenderBinaryLargeObject(target, source),
        TypeKind.Date => target switch
        {
            SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer
                or SqlAgentToolType.Oracle or SqlAgentToolType.Firebird => "DATE",
            SqlAgentToolType.Sqlite => "TEXT",
            _ => Unsupported(target, source)
        },
        TypeKind.Time => RenderTime(type, target, withZone: false, source),
        TypeKind.TimeWithZone => RenderTime(type, target, withZone: true, source),
        TypeKind.Timestamp => RenderTimestamp(type, target, withZone: false, source),
        TypeKind.TimestampWithZone => RenderTimestamp(type, target, withZone: true, source),
        TypeKind.RowVersion => target switch
        {
            SqlAgentToolType.Postgres => "BYTEA",
            SqlAgentToolType.MySQL => "BINARY(8)",
            SqlAgentToolType.Sqlite => "BLOB",
            SqlAgentToolType.MsSqlServer => "BINARY(8)",
            SqlAgentToolType.Oracle => "RAW(8)",
            SqlAgentToolType.Firebird => throw new SqlCompilationException(
                "SQL Server rowversion/timestamp has no lossless Firebird CAST target in the current Core model."),
            _ => Unsupported(target, source)
        },
        TypeKind.Uuid => target switch
        {
            SqlAgentToolType.Postgres => "UUID",
            SqlAgentToolType.MySQL => "CHAR(36)",
            SqlAgentToolType.Sqlite => "TEXT",
            SqlAgentToolType.MsSqlServer => "UNIQUEIDENTIFIER",
            SqlAgentToolType.Oracle => "VARCHAR2(36)",
            SqlAgentToolType.Firebird => "CHAR(36)",
            _ => Unsupported(target, source)
        },
        TypeKind.Json => target switch
        {
            SqlAgentToolType.Postgres => "JSONB",
            SqlAgentToolType.MySQL => "JSON",
            SqlAgentToolType.Sqlite => "TEXT",
            SqlAgentToolType.Oracle => "JSON",
            SqlAgentToolType.MsSqlServer => throw new SqlCompilationException(
                "JSON has no version-independent dedicated SQL Server CAST target in the current Core capability profile."),
            SqlAgentToolType.Firebird => throw new SqlCompilationException(
                "JSON has no dedicated Firebird CAST target in the current Core model."),
            _ => Unsupported(target, source)
        },
        _ => Unsupported(target, source)
    };

    private static string RenderInteger(
        SqlAgentToolType target,
        string portableName,
        string oracleName,
        string source) => target switch
    {
        SqlAgentToolType.Postgres or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Firebird => portableName,
        // MySQL CAST supports SIGNED, not SMALLINT/INT/BIGINT as target spellings.
        SqlAgentToolType.MySQL => "SIGNED",
        SqlAgentToolType.Sqlite => "INTEGER",
        SqlAgentToolType.Oracle => oracleName,
        _ => Unsupported(target, source)
    };

    private static string RenderDecimal(TypeSpec type, SqlAgentToolType target, string source)
    {
        if (type.Precision is null)
        {
            return target switch
            {
                SqlAgentToolType.Postgres => "NUMERIC",
                SqlAgentToolType.MySQL => "DECIMAL",
                SqlAgentToolType.Sqlite => "NUMERIC",
                _ => throw new SqlCompilationException(
                    $"Unbounded exact numeric CAST '{source}' cannot be preserved losslessly by target provider {target}; specify precision and scale.")
            };
        }

        var suffix = type.Scale is null
            ? $"({type.Precision.Value.ToString(CultureInfo.InvariantCulture)})"
            : $"({type.Precision.Value.ToString(CultureInfo.InvariantCulture)},{type.Scale.Value.ToString(CultureInfo.InvariantCulture)})";
        var name = target switch
        {
            SqlAgentToolType.Oracle => "NUMBER",
            SqlAgentToolType.Sqlite => "NUMERIC",
            _ => "DECIMAL"
        };
        return name + suffix;
    }

    private static string RenderString(TypeSpec type, SqlAgentToolType target, bool fixedWidth, string source)
    {
        if (!fixedWidth && type.Precision is null)
        {
            return target switch
            {
                SqlAgentToolType.Postgres => "VARCHAR",
                SqlAgentToolType.MySQL => "CHAR",
                SqlAgentToolType.Sqlite => "TEXT",
                _ => throw new SqlCompilationException(
                    $"Unbounded variable string CAST '{source}' has no lossless target spelling for provider {target}; specify a length or use an explicit large-object type.")
            };
        }

        var length = type.Precision ?? 1;
        return target switch
        {
            SqlAgentToolType.Postgres => $"{(fixedWidth ? "CHAR" : "VARCHAR")}({length})",
            SqlAgentToolType.MySQL => $"CHAR({length})",
            SqlAgentToolType.Sqlite => "TEXT",
            SqlAgentToolType.MsSqlServer => $"{(fixedWidth ? "NCHAR" : "NVARCHAR")}({length})",
            SqlAgentToolType.Oracle => $"{(fixedWidth ? "NCHAR" : "NVARCHAR2")}({length})",
            SqlAgentToolType.Firebird => $"{(fixedWidth ? "CHAR" : "VARCHAR")}({length})",
            _ => Unsupported(target, source)
        };
    }

    private static string RenderText(SqlAgentToolType target, string source) => target switch
    {
        SqlAgentToolType.Postgres => "TEXT",
        SqlAgentToolType.MySQL => "CHAR",
        SqlAgentToolType.Sqlite => "TEXT",
        SqlAgentToolType.Oracle => "CLOB",
        SqlAgentToolType.MsSqlServer => "NVARCHAR(MAX)",
        SqlAgentToolType.Firebird => throw new SqlCompilationException(
            "Text BLOB subtype CAST is not represented by the current Core CAST grammar for Firebird."),
        _ => Unsupported(target, source)
    };

    private static string RenderBinary(TypeSpec type, SqlAgentToolType target, string source)
    {
        if (type.Precision is null)
        {
            return target switch
            {
                SqlAgentToolType.Postgres => "BYTEA",
                SqlAgentToolType.MySQL => "BINARY",
                SqlAgentToolType.Sqlite => "BLOB",
                _ => throw new SqlCompilationException(
                    $"Unbounded binary CAST '{source}' has no lossless target spelling for provider {target}; specify a length or a binary large-object type.")
            };
        }

        var length = type.Precision.Value;
        return target switch
        {
            SqlAgentToolType.Postgres => "BYTEA",
            SqlAgentToolType.MySQL => $"BINARY({length})",
            SqlAgentToolType.Sqlite => "BLOB",
            SqlAgentToolType.MsSqlServer => $"VARBINARY({length})",
            SqlAgentToolType.Oracle => $"RAW({length})",
            SqlAgentToolType.Firebird => throw new SqlCompilationException(
                "Binary string CAST requires Firebird OCTETS character-set semantics, which are not modeled yet."),
            _ => Unsupported(target, source)
        };
    }

    private static string RenderBinaryLargeObject(SqlAgentToolType target, string source) => target switch
    {
        SqlAgentToolType.Postgres => "BYTEA",
        SqlAgentToolType.MySQL => "BINARY",
        SqlAgentToolType.Sqlite => "BLOB",
        SqlAgentToolType.Oracle or SqlAgentToolType.Firebird => "BLOB",
        SqlAgentToolType.MsSqlServer => "VARBINARY(MAX)",
        _ => Unsupported(target, source)
    };

    private static string RenderTime(TypeSpec type, SqlAgentToolType target, bool withZone, string source)
    {
        if (withZone)
        {
            return target switch
            {
                SqlAgentToolType.Postgres => TemporalWithZone(
                    "TIME",
                    BoundedPrecision(type.Precision, 6, target)),
                SqlAgentToolType.Firebird => FirebirdTemporal(
                    "TIME WITH TIME ZONE",
                    type.Precision),
                _ => throw new SqlCompilationException(
                    $"TIME WITH TIME ZONE CAST '{source}' has no lossless target mapping for {target}.")
            };
        }

        return target switch
        {
            SqlAgentToolType.Postgres => Temporal("TIME", BoundedPrecision(type.Precision, 6, target)),
            SqlAgentToolType.MySQL => Temporal("TIME", BoundedPrecision(type.Precision, 6, target)),
            SqlAgentToolType.Sqlite => "TEXT",
            SqlAgentToolType.MsSqlServer => Temporal("TIME", BoundedPrecision(type.Precision, 7, target)),
            SqlAgentToolType.Oracle => throw new SqlCompilationException("Oracle has no standalone TIME data type."),
            SqlAgentToolType.Firebird => FirebirdTemporal("TIME", type.Precision),
            _ => Unsupported(target, source)
        };
    }

    private static string RenderTimestamp(TypeSpec type, SqlAgentToolType target, bool withZone, string source)
    {
        if (withZone)
        {
            return target switch
            {
                SqlAgentToolType.Postgres => TemporalWithZone(
                    "TIMESTAMP",
                    BoundedPrecision(type.Precision, 6, target)),
                SqlAgentToolType.MySQL => throw new SqlCompilationException(
                    "MySQL CAST has no target type that preserves an explicit UTC offset."),
                SqlAgentToolType.Sqlite => "TEXT",
                SqlAgentToolType.MsSqlServer => Temporal(
                    "DATETIMEOFFSET",
                    BoundedPrecision(type.Precision, 7, target)),
                SqlAgentToolType.Oracle => TemporalWithZone(
                    "TIMESTAMP",
                    BoundedPrecision(type.Precision, 9, target)),
                SqlAgentToolType.Firebird => FirebirdTemporal(
                    "TIMESTAMP WITH TIME ZONE",
                    type.Precision),
                _ => Unsupported(target, source)
            };
        }

        return target switch
        {
            SqlAgentToolType.Postgres => Temporal("TIMESTAMP", BoundedPrecision(type.Precision, 6, target)),
            SqlAgentToolType.MySQL => Temporal("DATETIME", BoundedPrecision(type.Precision, 6, target)),
            SqlAgentToolType.Sqlite => "TEXT",
            SqlAgentToolType.MsSqlServer => Temporal("DATETIME2", BoundedPrecision(type.Precision, 7, target)),
            SqlAgentToolType.Oracle => Temporal("TIMESTAMP", BoundedPrecision(type.Precision, 9, target)),
            SqlAgentToolType.Firebird => FirebirdTemporal("TIMESTAMP", type.Precision),
            _ => Unsupported(target, source)
        };
    }

    private static int? BoundedPrecision(int? precision, int max, SqlAgentToolType target)
    {
        if (precision is null) return null;
        if (precision > max)
            throw new SqlCompilationException(
                $"Temporal precision {precision} exceeds target provider {target} maximum {max} for a lossless CAST.");
        return precision;
    }

    private static string FirebirdTemporal(string name, int? precision)
    {
        if (precision is > 4)
            throw new SqlCompilationException(
                $"Temporal precision {precision} exceeds Firebird's four fractional-second digits for a lossless CAST.");
        // Firebird TIME/TIMESTAMP data type syntax has fixed storage precision and does not accept
        // a type-level (p) precision argument.
        return name;
    }

    private static string Temporal(string name, int? precision) =>
        precision is null ? name : $"{name}({precision.Value.ToString(CultureInfo.InvariantCulture)})";

    private static string TemporalWithZone(string name, int? precision) =>
        precision is null
            ? $"{name} WITH TIME ZONE"
            : $"{name}({precision.Value.ToString(CultureInfo.InvariantCulture)}) WITH TIME ZONE";

    private static int? ParsePrecision(Group group)
    {
        if (!group.Success) return null;
        if (group.Value.Equals("MAX", StringComparison.OrdinalIgnoreCase)) return MaxLengthSentinel;
        return int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SqlCompilationException($"CAST type argument '{group.Value}' is outside the supported integer range.");
    }

    private static int? ParseOptionalInt(Group group) =>
        !group.Success
            ? null
            : int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new SqlCompilationException($"CAST type argument '{group.Value}' is outside the supported integer range.");

    private static string Unsupported(SqlAgentToolType target, string source) =>
        throw new SqlCompilationException($"CAST type '{source}' has no Core target mapping for provider {target}.");

    private sealed record TypeSpec(TypeKind Kind, int? Precision, int? Scale);

    private enum TypeKind
    {
        Boolean,
        SmallInteger,
        Integer,
        BigInteger,
        UnsignedBigInteger,
        Decimal,
        Real,
        Double,
        FixedString,
        VariableString,
        Text,
        FixedBinary,
        VariableBinary,
        BinaryLargeObject,
        Date,
        Time,
        TimeWithZone,
        Timestamp,
        TimestampWithZone,
        RowVersion,
        Uuid,
        Json
    }
}
