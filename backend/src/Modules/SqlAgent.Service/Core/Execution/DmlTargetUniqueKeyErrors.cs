using System.Collections;
using System.Data.Common;
using System.Reflection;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Only vendor-specific UNIQUE/PRIMARY KEY errors are evidence of a physical
/// uniqueness constraint. Permission, network, FK, CHECK, trigger and generic
/// DbException failures MUST NOT pass target-readiness proof.
/// </summary>
internal static class DmlTargetUniqueKeyErrors
{
    internal static bool IsUniqueViolation(SqlAgentToolType provider, DbException error)
    {
        var type = error.GetType().FullName;
        return provider switch
        {
            SqlAgentToolType.Postgres =>
                type == "Npgsql.PostgresException" && error.SqlState == "23505",
            SqlAgentToolType.MySQL =>
                type == "MySql.Data.MySqlClient.MySqlException" && Numeric(error) == 1062,
            SqlAgentToolType.Sqlite =>
                type == "Microsoft.Data.Sqlite.SqliteException"
                && Numeric(error, "SqliteExtendedErrorCode") is 1555 or 2067,
            SqlAgentToolType.MsSqlServer =>
                type == "Microsoft.Data.SqlClient.SqlException"
                && Numeric(error) is 2627 or 2601,
            SqlAgentToolType.Oracle =>
                type == "Oracle.ManagedDataAccess.Client.OracleException"
                && Numeric(error) == 1,
            SqlAgentToolType.Firebird =>
                type == "FirebirdSql.Data.FirebirdClient.FbException"
                && (error.ErrorCode is 335544665 or 335544349 or -803
                    || HasFirebirdUniqueDetail(error)),
            _ => false
        };
    }

    private static int? Numeric(object source, string name = "Number")
    {
        try
        {
            var property = source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(source) is int number ? number : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasFirebirdUniqueDetail(DbException ex)
    {
        try
        {
            if (ex.GetType().GetProperty("Errors")?.GetValue(ex) is not IEnumerable errors)
                return false;
            foreach (var item in errors)
                if (item is not null
                    && (Numeric(item) is 335544665 or 335544349
                        || Numeric(item, "ErrorCode") is 335544665 or 335544349))
                    return true;
        }
        catch (Exception)
        {
            // Unknown driver details are never positive proof.
        }
        return false;
    }
}
