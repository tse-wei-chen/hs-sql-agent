using System.Data.Common;
using SqlAgent.Service.Models;
using SqlAgent.Service.Enums;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Factories;

namespace SqlAgent.Service.Services
{
    public class DbSetterService(IConfiguration configuration, ISqlStrategyFactory strategyFactory) : IDbSetterService
    {
        private readonly IConfiguration _configuration = configuration;
        private readonly ISqlStrategyFactory _strategyFactory = strategyFactory;
        public async Task<TestDbConnectionVM> TestDbConnectionAsync(TestDbConnectionRequest request, CancellationToken cancellationToken = default)
        {
            DbConnection? connection = null;

            try
            {
                var provider = request.SqlProvider;
                var connString = string.Empty;
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
                if (provider == null)
                {
                    return new TestDbConnectionVM { IsSuccess = false, ErrorMessage = "Provider is null." };
                }
                var strategy = _strategyFactory.GetStrategy(provider.Value);
                if (string.IsNullOrWhiteSpace(connString))
                {
                    connString = strategy.BuildConnectionString(new BuildDbConnectionModelBase
                    {
                        Host = request.Host,
                        Port = request.Port,
                        Username = request.Username,
                        Password = request.Password,
                        Database = request.Database
                    });
                }
                connection = strategy.CreateConnection(connString);
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

        public async Task<string> BuildDbConnectionAsync(BuildDbConnectionModel model, CancellationToken cancellationToken = default)
        {
            var strategy = _strategyFactory.GetStrategy(Enum.Parse<SqlAgentToolType>(model.Provider));
            return strategy.BuildConnectionString(model);
        }
    }
}