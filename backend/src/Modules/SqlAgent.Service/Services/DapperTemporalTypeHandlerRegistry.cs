using System.Data;
using System.Globalization;
using System.Threading;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using FirebirdSql.Data.Types;
using MySql.Data.MySqlClient;
using Npgsql;
using NpgsqlTypes;
using Oracle.ManagedDataAccess.Client;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Services;

/// <summary>
/// Registers the provider-neutral temporal AST values with Dapper. Handlers
/// emit typed ADO.NET parameters instead of provider-specific SQL literals.
/// </summary>
internal static class DapperTemporalTypeHandlerRegistry
{
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

    private sealed class SqlDateValueTypeHandler : SqlMapper.TypeHandler<SqlDateValue>
    {
        public override void SetValue(IDbDataParameter parameter, SqlDateValue? value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value is null
                ? DBNull.Value
                : value.Value.ToDateTime(TimeOnly.MinValue);
        }

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
        public override void SetValue(IDbDataParameter parameter, SqlTimeValue? value)
        {
            if (parameter is OracleParameter)
                throw new NotSupportedException(
                    "Oracle has no standalone TIME data type; use a DATE or TIMESTAMP value with an explicit date.");
            parameter.DbType = DbType.Time;
            parameter.Value = value is null ? DBNull.Value : value.Value.ToTimeSpan();
        }

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
        public override void SetValue(IDbDataParameter parameter, SqlLocalDateTimeValue? value)
        {
            if (parameter is FbParameter firebirdParameter)
            {
                firebirdParameter.FbDbType = FbDbType.TimeStamp;
                firebirdParameter.Value = value is null
                    ? DBNull.Value
                    : DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
                return;
            }
            if (parameter is MySqlParameter mySqlParameter)
            {
                mySqlParameter.MySqlDbType = MySqlDbType.DateTime;
                mySqlParameter.Value = value is null
                    ? DBNull.Value
                    : DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
                return;
            }
            if (parameter is OracleParameter oracleParameter)
            {
                oracleParameter.OracleDbType = OracleDbType.TimeStamp;
                oracleParameter.Value = value is null
                    ? DBNull.Value
                    : DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
                return;
            }
            parameter.DbType = DbType.DateTime2;
            parameter.Value = value is null
                ? DBNull.Value
                : DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        }

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
        public override void SetValue(IDbDataParameter parameter, SqlOffsetDateTimeValue? value)
        {
            if (parameter is FbParameter firebirdParameter)
            {
                firebirdParameter.FbDbType = FbDbType.TimeStampTZ;
                firebirdParameter.Value = value is null
                    ? DBNull.Value
                    : new FbZonedDateTime(
                        value.Value.UtcDateTime,
                        "UTC");
                return;
            }
            if (parameter is MySqlParameter)
                throw new NotSupportedException(
                    "MySQL has no native timestamp type that preserves a UTC offset; " +
                    "use a UTC local timestamp or store the offset separately.");
            if (parameter is NpgsqlParameter npgsqlParameter)
            {
                // PostgreSQL timestamptz stores an instant, not the original offset.
                // Npgsql requires DateTimeOffset values to have Offset == 00:00.
                npgsqlParameter.NpgsqlDbType = NpgsqlDbType.TimestampTz;
                npgsqlParameter.Value = value is null
                    ? DBNull.Value
                    : value.Value.ToUniversalTime();
                return;
            }
            if (parameter is OracleParameter oracleParameter)
            {
                oracleParameter.OracleDbType = OracleDbType.TimeStampTZ;
                oracleParameter.Value = value is null ? DBNull.Value : value.Value;
                return;
            }
            parameter.DbType = DbType.DateTimeOffset;
            parameter.Value = value is null ? DBNull.Value : value.Value;
        }

        public override SqlOffsetDateTimeValue Parse(object value) => value switch
        {
            DateTimeOffset dateTimeOffset => new SqlOffsetDateTimeValue(dateTimeOffset),
            _ => new SqlOffsetDateTimeValue(DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None))
        };
    }
}
