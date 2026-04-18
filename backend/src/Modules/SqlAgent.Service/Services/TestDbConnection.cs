using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using MySql.Data.MySqlClient;
using Microsoft.Data.Sqlite;
using Oracle.ManagedDataAccess.Client;
using FirebirdSql.Data.FirebirdClient;
using SqlAgent.Service.Models;
using SqlAgent.Service.Enums;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Interfaces;

namespace SqlAgent.Service.Services
{
    public class TestDbConnection(IConfiguration configuration) : ITestDbConnection
    {
        private readonly IConfiguration _configuration = configuration;
        public async Task<TestDbConnectionVM> TestDbConnectionAsync(TestDbConnectionRequest request, CancellationToken cancellationToken = default)
        {
            DbConnection? connection = null;

            try
            {
                var provider = request.SqlProvider;
                var connString = request.ConnectionString;
                if (provider == SqlAgentToolType.Global)
                {
                    if (!string.IsNullOrWhiteSpace(connString))
                    {
                        return new TestDbConnectionVM { IsSuccess = false, ErrorMessage = "Provider is set to Global but connection string is provided in request." };
                    }
                    var section = _configuration.GetSection("SqlConfig");
                    provider = Enum.Parse<SqlAgentToolType>(section["Provider"] ?? "MsSqlServer");
                    connString = section["ConnectionString"];
                    if (string.IsNullOrWhiteSpace(connString))
                    {
                        return new TestDbConnectionVM { IsSuccess = false, ErrorMessage = "Global connection string is not configured." };
                    }
                }
                else if (string.IsNullOrWhiteSpace(connString))
                {
                    return new TestDbConnectionVM { IsSuccess = false, ErrorMessage = "Connection string is empty." };
                }
                connection = CreateConnection(provider, connString);
                await connection.OpenAsync(cancellationToken);
                await connection.CloseAsync();
                return new TestDbConnectionVM { IsSuccess = true };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB Test Error] Provider: {request.SqlProvider}, Message: {ex.Message}");
                return new TestDbConnectionVM { IsSuccess = false, ErrorMessage = ex.Message };
            }
            finally
            {
                if (connection != null)
                {
                    await connection.DisposeAsync();
                }
            }
        }

        static DbConnection CreateConnection(SqlAgentToolType? provider, string? connectionString)
        {
            return provider switch
            {
                SqlAgentToolType.MsSqlServer => new SqlConnection(connectionString),
                SqlAgentToolType.Postgres => new NpgsqlConnection(connectionString),
                SqlAgentToolType.MySQL => new MySqlConnection(connectionString),
                SqlAgentToolType.Sqlite => new SqliteConnection(connectionString),
                SqlAgentToolType.Oracle => new OracleConnection(connectionString),
                SqlAgentToolType.Firebird => new FbConnection(connectionString),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), $"not supported db type: {provider}")
            };
        }
    }
}