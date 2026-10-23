namespace SqlAgent.Service.Services;

/// <summary>
/// Public-facing database connectivity diagnostics. Provider exception messages can
/// contain credentials or connection strings and must never be returned or persisted.
/// Only this finite set of constants may cross the Admin API / health-monitor boundary.
/// </summary>
public static class DatabaseErrorMessages
{
    public const string ProviderRequired = "Database provider is required.";
    public const string ConnectionFailed =
        "Database connection failed. Check the endpoint, credentials, TLS settings and server availability.";
    public const string ConnectionTimedOut = "Database connection timed out.";

    public static string ForException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is TimeoutException or OperationCanceledException
            ? ConnectionTimedOut
            : ConnectionFailed;
    }

    public static string ForHealthRecord(string? message) =>
        message switch
        {
            ProviderRequired => ProviderRequired,
            ConnectionFailed => ConnectionFailed,
            ConnectionTimedOut => ConnectionTimedOut,
            _ => ConnectionFailed
        };
}
