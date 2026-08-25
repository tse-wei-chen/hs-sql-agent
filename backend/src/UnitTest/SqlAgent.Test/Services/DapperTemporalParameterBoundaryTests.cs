using FirebirdSql.Data.FirebirdClient;
using MySql.Data.MySqlClient;
using Npgsql;
using NpgsqlTypes;
using Oracle.ManagedDataAccess.Client;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class DapperTemporalParameterBoundaryTests
{
    [Fact]
    public void MySql_LocalTimestamp_UsesNativeDateTimeParameterWithoutServiceDriverReference()
    {
        var parameter = new MySqlParameter();
        var value = new SqlLocalDateTimeValue(new DateTime(2026, 8, 25, 12, 34, 56, DateTimeKind.Utc));

        DapperTemporalTypeHandlerRegistry.ConfigureLocalDateTimeParameter(parameter, value);

        Assert.Equal(MySqlDbType.DateTime, parameter.MySqlDbType);
        var bound = Assert.IsType<DateTime>(parameter.Value);
        Assert.Equal(DateTimeKind.Unspecified, bound.Kind);
        Assert.Equal(value.Value, bound);
    }

    [Fact]
    public void PostgreSql_OffsetTimestamp_UsesTimestampTzAndNormalizesToUtc()
    {
        var parameter = new NpgsqlParameter();
        var value = new SqlOffsetDateTimeValue(
            new DateTimeOffset(2026, 8, 25, 20, 30, 0, TimeSpan.FromHours(8)));

        DapperTemporalTypeHandlerRegistry.ConfigureOffsetDateTimeParameter(parameter, value);

        Assert.Equal(NpgsqlDbType.TimestampTz, parameter.NpgsqlDbType);
        var bound = Assert.IsType<DateTimeOffset>(parameter.Value);
        Assert.Equal(TimeSpan.Zero, bound.Offset);
        Assert.Equal(value.Value.UtcDateTime, bound.UtcDateTime);
    }

    [Fact]
    public void Oracle_LocalAndOffsetTimestamp_UseNativeTimestampKinds()
    {
        var localParameter = new OracleParameter();
        var local = new SqlLocalDateTimeValue(new DateTime(2026, 8, 25, 12, 0, 0));
        DapperTemporalTypeHandlerRegistry.ConfigureLocalDateTimeParameter(localParameter, local);
        Assert.Equal(OracleDbType.TimeStamp, localParameter.OracleDbType);
        Assert.Equal(DateTimeKind.Unspecified, Assert.IsType<DateTime>(localParameter.Value).Kind);

        var offsetParameter = new OracleParameter();
        var offset = new SqlOffsetDateTimeValue(
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.FromHours(5.5)));
        DapperTemporalTypeHandlerRegistry.ConfigureOffsetDateTimeParameter(offsetParameter, offset);
        Assert.Equal(OracleDbType.TimeStampTZ, offsetParameter.OracleDbType);
        Assert.Equal(offset.Value, Assert.IsType<DateTimeOffset>(offsetParameter.Value));
    }

    [Fact]
    public void Firebird_LocalAndOffsetTimestamp_UseNativeTimestampKinds()
    {
        var localParameter = new FbParameter();
        var local = new SqlLocalDateTimeValue(new DateTime(2026, 8, 25, 12, 0, 0));
        DapperTemporalTypeHandlerRegistry.ConfigureLocalDateTimeParameter(localParameter, local);
        Assert.Equal(FbDbType.TimeStamp, localParameter.FbDbType);
        Assert.Equal(DateTimeKind.Unspecified, Assert.IsType<DateTime>(localParameter.Value).Kind);

        var offsetParameter = new FbParameter();
        var offset = new SqlOffsetDateTimeValue(
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.FromHours(8)));
        DapperTemporalTypeHandlerRegistry.ConfigureOffsetDateTimeParameter(offsetParameter, offset);
        Assert.Equal(FbDbType.TimeStampTZ, offsetParameter.FbDbType);
        Assert.NotNull(offsetParameter.Value);
        Assert.Equal("FirebirdSql.Data.Types.FbZonedDateTime", offsetParameter.Value.GetType().FullName);
    }

    [Fact]
    public void MySql_OffsetTimestamp_RemainsFailClosed()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            DapperTemporalTypeHandlerRegistry.ConfigureOffsetDateTimeParameter(
                new MySqlParameter(),
                new SqlOffsetDateTimeValue(DateTimeOffset.UtcNow)));

        Assert.Contains("preserves a UTC offset", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_StandaloneTime_RemainsFailClosed()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            DapperTemporalTypeHandlerRegistry.ConfigureTimeParameter(
                new OracleParameter(),
                new SqlTimeValue(new TimeOnly(12, 34, 56))));

        Assert.Contains("no standalone TIME", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
