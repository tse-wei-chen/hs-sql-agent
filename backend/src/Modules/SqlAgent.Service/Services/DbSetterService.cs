using System.Data.Common;
using SqlAgent.Service.Models;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Factories;

namespace SqlAgent.Service.Services
{
    public class DbSetterService(ISqlStrategyFactory strategyFactory) : IDbSetterService
    {
        private readonly ISqlStrategyFactory _strategyFactory = strategyFactory;
        public async Task<TestDbConnectionVM> TestDbConnectionAsync(TestDbConnectionBase request, CancellationToken cancellationToken = default)
        {
            DbConnection? connection = null;

            try
            {
                var provider = request.SqlProvider;
                if (provider == null)
                {
                    return new TestDbConnectionVM { IsSuccess = false, ErrorMessage = "Provider is null." };
                }
                var strategy = _strategyFactory.GetStrategy(provider.Value);
                var connString = strategy.BuildConnectionString(new BuildDbConnectionModelBase
                {
                    Host = request.Host,
                    Port = request.Port,
                    Username = request.Username,
                    Password = request.Password,
                    Database = request.Database,
                    ExtraSettings = request.ExtraSettings
                });
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

        public async Task<string?> BuildDbConnectionAsync(BuildDbConnectionModel model, CancellationToken cancellationToken = default)
        {
            var provider = Enum.Parse<SqlAgentToolType>(model.Provider);
            var strategy = _strategyFactory.GetStrategy(provider);
            return strategy.BuildConnectionString(model);
        }
    }
}
