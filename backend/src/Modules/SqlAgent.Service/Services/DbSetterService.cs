using System.Data.Common;
using SqlAgent.Service.Models;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Factories;

namespace SqlAgent.Service.Services
{
    public class DbSetterService(ISqlStrategyFactory providerFactory) : IDbSetterService
    {
        private readonly ISqlStrategyFactory _providerFactory = providerFactory;

        public async Task<TestDbConnectionVM> TestDbConnectionAsync(
            TestDbConnectionBase request,
            CancellationToken cancellationToken = default)
        {
            DbConnection? connection = null;

            try
            {
                var provider = request.SqlProvider;
                if (provider == null)
                    return new TestDbConnectionVM { IsSuccess = false, ErrorMessage = "Provider is null." };

                var connString = _providerFactory.BuildConnectionString(
                    provider.Value,
                    new BuildDbConnectionModelBase
                    {
                        Host = request.Host,
                        Port = request.Port,
                        Username = request.Username,
                        Password = request.Password,
                        Database = request.Database,
                        ExtraSettings = request.ExtraSettings
                    });
                connection = _providerFactory.GetProvider(provider.Value).Connections.Create(connString);
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
                    await connection.DisposeAsync();
            }
        }

        public Task<string?> BuildDbConnectionAsync(
            BuildDbConnectionModel model,
            CancellationToken cancellationToken = default)
        {
            var provider = Enum.Parse<SqlAgentToolType>(model.Provider);
            return Task.FromResult<string?>(_providerFactory.BuildConnectionString(provider, model));
        }
    }
}
