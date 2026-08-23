using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;
using Npgsql;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Providers;

/// <summary>
/// Provider-owned execution error mapper. It normalizes database-specific exception details into
/// the stable <see cref="IProviderErrorMapper"/> contract without routing execution through the
/// historical strategy surface.
/// </summary>
public sealed class ProviderExecutionErrorMapper(SqlAgentToolType providerType) : IProviderErrorMapper
{
    private readonly SqlAgentToolType _providerType = providerType;

    public Exception Map(Exception exception, string operation)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var normalizedOperation = string.IsNullOrWhiteSpace(operation)
            ? "operation"
            : operation.Trim().ToLowerInvariant();
        var code = ExtractCode(exception) ?? "unknown";
        var message = ExtractMessage(exception);
        return new ProviderExecutionException(
            _providerType,
            normalizedOperation,
            code,
            message,
            exception);
    }

    private string? ExtractCode(Exception exception) => _providerType switch
    {
        SqlAgentToolType.Postgres => FindException<PostgresException>(exception)?.SqlState
            ?? ExtractPostgresSqlState(exception.ToString()),
        SqlAgentToolType.MySQL => FindException<MySqlException>(exception)?.Number.ToString()
            ?? ExtractMySqlCode(exception.Message),
        SqlAgentToolType.MsSqlServer => FindException<SqlException>(exception)?.Number.ToString()
            ?? ExtractMsSqlCode(exception.Message),
        SqlAgentToolType.Sqlite => FindException<SqliteException>(exception) is { } sqlite
            ? $"SQLITE_{sqlite.SqliteErrorCode}"
            : null,
        SqlAgentToolType.Oracle => ExtractOracleCode(exception.Message),
        SqlAgentToolType.Firebird => ExtractFirebirdCode(exception),
        _ => null
    };

    private string ExtractMessage(Exception exception)
    {
        if (_providerType == SqlAgentToolType.Postgres
            && FindException<PostgresException>(exception) is { } postgres)
            return postgres.MessageText;
        return exception.GetBaseException().Message;
    }

    private static string? ExtractPostgresSqlState(string message)
    {
        var match = Regex.Match(message ?? string.Empty, @"\b(?<code>[0-9A-Z]{5})\b");
        if (!match.Success) return null;
        var code = match.Groups["code"].Value;
        return code.Any(char.IsDigit) ? code : null;
    }

    private static string? ExtractMySqlCode(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var sqlState = Regex.Match(message, @"SQLSTATE\[(?<code>[0-9A-Z]{5})\]", RegexOptions.IgnoreCase);
        if (sqlState.Success) return sqlState.Groups["code"].Value.ToUpperInvariant();
        var mysqlCode = Regex.Match(message, @"\b(?<code>\d{4})\b");
        return mysqlCode.Success ? mysqlCode.Groups["code"].Value : null;
    }

    private static string? ExtractMsSqlCode(string message)
    {
        var match = Regex.Match(message ?? string.Empty, @"Error Number:\s*(?<code>\d+)");
        return match.Success ? match.Groups["code"].Value : null;
    }

    private static string? ExtractOracleCode(string message)
    {
        var match = Regex.Match(message ?? string.Empty, @"(ORA-\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractFirebirdCode(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        var sqlCode = Regex.Match(
            message,
            @"SQL\s+(?:error\s+)?[Cc]ode\s*=\s*(?<code>-?\d+)",
            RegexOptions.IgnoreCase);
        if (sqlCode.Success) return "FB_SQL_" + sqlCode.Groups["code"].Value;

        var gdsCode = Regex.Match(
            message,
            @".*gds\s+code\s*=\s*(?<code>\d+)",
            RegexOptions.IgnoreCase);
        if (gdsCode.Success) return "FB_GDS_" + gdsCode.Groups["code"].Value;

        return FindException<FbException>(exception)?.ErrorCode.ToString();
    }

    private static TException? FindException<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TException typed)
                return typed;
        }
        return null;
    }
}

public sealed class ProviderExecutionException(
    SqlAgentToolType providerType,
    string operation,
    string code,
    string providerMessage,
    Exception innerException)
    : Exception($"Error executing {operation} | code={code} | message={providerMessage}", innerException)
{
    public SqlAgentToolType ProviderType { get; } = providerType;
    public string Operation { get; } = operation;
    public string Code { get; } = code;
    public string ProviderMessage { get; } = providerMessage;
}
