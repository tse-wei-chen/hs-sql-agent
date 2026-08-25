using System.Data;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Dapper;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Services;

/// <summary>
/// Registers provider-neutral temporal AST values with Dapper. Provider-specific ADO.NET parameter
/// types are recognized by their runtime contract instead of compile-time driver references so the
/// application runtime does not pull every database driver into its own assembly graph.
/// </summary>
internal static class DapperTemporalTypeHandlerRegistry
{
    private const string FirebirdParameterType = "FirebirdSql.Data.FirebirdClient.FbParameter";
    private const string FirebirdZonedDateTimeType = "FirebirdSql.Data.Types.FbZonedDateTime";
    private const string MySqlParameterType = "MySql.Data.MySqlClient.MySqlParameter";
    private const string NpgsqlParameterType = "Npgsql.NpgsqlParameter";
    private const string OracleParameterType = "Oracle.ManagedDataAccess.Client.OracleParameter";

    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
            return;

        SqlMapper.AddTypeHandler(new SqlDateValueTypeHandler());
        SqlMapper.AddTypeHandler(new SqlTimeValueTypeHandler());
        SqlMapper.AddTypeHandler(new SqlLocalDateTimeValueTypeHandler());
        SqlMapper.AddTypeHandler(new SqlOffsetDateTimeValueTypeHandler());
    }

    internal static void ConfigureDateParameter(IDbDataParameter parameter, SqlDateValue? value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value is null
            ? DBNull.Value
            : value.Value.ToDateTime(TimeOnly.MinValue);
    }

    internal static void ConfigureTimeParameter(IDbDataParameter parameter, SqlTimeValue? value)
    {
        if (IsProviderParameter(parameter, OracleParameterType))
        {
            throw new NotSupportedException(
                "Oracle has no standalone TIME data type; use a DATE or TIMESTAMP value with an explicit date.");
        }

        parameter.DbType = DbType.Time;
        parameter.Value = value is null ? DBNull.Value : value.Value.ToTimeSpan();
    }

    internal static void ConfigureLocalDateTimeParameter(
        IDbDataParameter parameter,
        SqlLocalDateTimeValue? value)
    {
        if (IsProviderParameter(parameter, FirebirdParameterType))
        {
            SetProviderEnum(parameter, "FbDbType", "TimeStamp");
            parameter.Value = value is null
                ? DBNull.Value
                : DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
            return;
        }

        if (IsProviderParameter(parameter, MySqlParameterType))
        {
            SetProviderEnum(parameter, "MySqlDbType", "DateTime");
            parameter.Value = value is null
                ? DBNull.Value
                : DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
            return;
        }

        if (IsProviderParameter(parameter, OracleParameterType))
        {
            SetProviderEnum(parameter, "OracleDbType", "TimeStamp");
            parameter.Value = value is null
                ? DBNull.Value
                : DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
            return;
        }

        parameter.DbType = DbType.DateTime2;
        parameter.Value = value is null
            ? DBNull.Value
            : DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
    }

    internal static void ConfigureOffsetDateTimeParameter(
        IDbDataParameter parameter,
        SqlOffsetDateTimeValue? value)
    {
        if (IsProviderParameter(parameter, FirebirdParameterType))
        {
            SetProviderEnum(parameter, "FbDbType", "TimeStampTZ");
            parameter.Value = value is null
                ? DBNull.Value
                : CreateFirebirdZonedDateTime(parameter, value.Value);
            return;
        }

        if (IsProviderParameter(parameter, MySqlParameterType))
        {
            throw new NotSupportedException(
                "MySQL has no native timestamp type that preserves a UTC offset; " +
                "use a UTC local timestamp or store the offset separately.");
        }

        if (IsProviderParameter(parameter, NpgsqlParameterType))
        {
            // PostgreSQL timestamptz stores an instant, not the original offset.
            // Npgsql requires DateTimeOffset values to have Offset == 00:00.
            SetProviderEnum(parameter, "NpgsqlDbType", "TimestampTz");
            parameter.Value = value is null
                ? DBNull.Value
                : value.Value.ToUniversalTime();
            return;
        }

        if (IsProviderParameter(parameter, OracleParameterType))
        {
            SetProviderEnum(parameter, "OracleDbType", "TimeStampTZ");
            parameter.Value = value is null ? DBNull.Value : value.Value;
            return;
        }

        parameter.DbType = DbType.DateTimeOffset;
        parameter.Value = value is null ? DBNull.Value : value.Value;
    }

    private sealed class SqlDateValueTypeHandler : SqlMapper.TypeHandler<SqlDateValue>
    {
        public override void SetValue(IDbDataParameter parameter, SqlDateValue? value) =>
            ConfigureDateParameter(parameter, value);

        public override SqlDateValue Parse(object value) => value switch
        {
            DateOnly date => new SqlDateValue(date),
            DateTime dateTime => new SqlDateValue(DateOnly.FromDateTime(dateTime)),
            _ => new SqlDateValue(DateOnly.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture))
        };
    }

    private sealed class SqlTimeValueTypeHandler : SqlMapper.TypeHandler<SqlTimeValue>
    {
        public override void SetValue(IDbDataParameter parameter, SqlTimeValue? value) =>
            ConfigureTimeParameter(parameter, value);

        public override SqlTimeValue Parse(object value) => value switch
        {
            TimeOnly time => new SqlTimeValue(time),
            TimeSpan duration => new SqlTimeValue(TimeOnly.FromTimeSpan(duration)),
            DateTime dateTime => new SqlTimeValue(TimeOnly.FromDateTime(dateTime)),
            _ => new SqlTimeValue(TimeOnly.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture))
        };
    }

    private sealed class SqlLocalDateTimeValueTypeHandler : SqlMapper.TypeHandler<SqlLocalDateTimeValue>
    {
        public override void SetValue(IDbDataParameter parameter, SqlLocalDateTimeValue? value) =>
            ConfigureLocalDateTimeParameter(parameter, value);

        public override SqlLocalDateTimeValue Parse(object value) => value switch
        {
            DateTime dateTime => new SqlLocalDateTimeValue(dateTime),
            _ => new SqlLocalDateTimeValue(DateTime.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None))
        };
    }

    private sealed class SqlOffsetDateTimeValueTypeHandler : SqlMapper.TypeHandler<SqlOffsetDateTimeValue>
    {
        public override void SetValue(IDbDataParameter parameter, SqlOffsetDateTimeValue? value) =>
            ConfigureOffsetDateTimeParameter(parameter, value);

        public override SqlOffsetDateTimeValue Parse(object value) => value switch
        {
            DateTimeOffset dateTimeOffset => new SqlOffsetDateTimeValue(dateTimeOffset),
            _ => new SqlOffsetDateTimeValue(DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None))
        };
    }

    internal static bool IsProviderParameter(IDbDataParameter parameter, string expectedFullName)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFullName);

        for (var type = parameter.GetType(); type is not null; type = type.BaseType)
        {
            if (string.Equals(type.FullName, expectedFullName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    internal static void SetProviderEnum(object parameter, string propertyName, string enumName)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var property = parameter.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"Provider parameter type '{parameter.GetType().FullName}' does not expose '{propertyName}'.");
        if (!property.CanWrite || !property.PropertyType.IsEnum)
        {
            throw new InvalidOperationException(
                $"Provider parameter property '{parameter.GetType().FullName}.{propertyName}' is not a writable enum.");
        }

        property.SetValue(parameter, Enum.Parse(property.PropertyType, enumName, ignoreCase: false));
    }

    internal static object CreateFirebirdZonedDateTime(
        IDbDataParameter parameter,
        DateTimeOffset value)
    {
        var type = parameter.GetType().Assembly.GetType(
            FirebirdZonedDateTimeType,
            throwOnError: true,
            ignoreCase: false)!;
        return Activator.CreateInstance(
                   type,
                   value.UtcDateTime,
                   "UTC")
               ?? throw new InvalidOperationException(
                   "Firebird zoned timestamp value could not be constructed.");
    }
}
