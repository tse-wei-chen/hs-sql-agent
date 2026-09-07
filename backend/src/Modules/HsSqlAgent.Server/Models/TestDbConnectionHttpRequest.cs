using System.Text.Json;
using System.Text.Json.Serialization;
using HsSqlAgent.SqlCore.Enums;

namespace HsSqlAgent.Server.Models;

public sealed class TestDbConnectionHttpRequest
{
    public int DbSettingMode { get; init; }
    public int? DbManagementId { get; init; }

    [JsonConverter(typeof(NullableSqlAgentToolTypeJsonConverter))]
    public SqlAgentToolType? SqlProvider { get; init; }

    public string? Host { get; init; }
    public string? Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Database { get; init; }
    public string? ExtraSettings { get; init; }
}

public sealed class NullableSqlAgentToolTypeJsonConverter : JsonConverter<SqlAgentToolType?>
{
    public override SqlAgentToolType? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (!string.IsNullOrWhiteSpace(value)
                && Enum.TryParse<SqlAgentToolType>(value, true, out var parsed)
                && Enum.IsDefined(typeof(SqlAgentToolType), parsed))
            {
                return parsed;
            }

            throw new JsonException($"Unsupported SQL provider '{value}'.");
        }

        if (reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(SqlAgentToolType), numeric))
        {
            return (SqlAgentToolType)numeric;
        }

        throw new JsonException("SQL provider must be a supported provider name or numeric enum value.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        SqlAgentToolType? value,
        JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToString());
        else
            writer.WriteNullValue();
    }
}
