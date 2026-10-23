using System.Data.Common;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Factories;

namespace SqlAgent.Service.Services
{
    public class DbSetterService(
        ISqlProviderFactory providerFactory,
        ISqlConnectionStringFactory connectionStringFactory) : IDbSetterService
    {
        private readonly ISqlProviderFactory _providerFactory = providerFactory;
        private readonly ISqlConnectionStringFactory _connectionStringFactory = connectionStringFactory;

        public async Task<TestDbConnectionVM> TestDbConnectionAsync(
            TestDbConnectionBase request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var provider = request.SqlProvider;
                if (provider == null)
                    return new TestDbConnectionVM { IsSuccess = false, ErrorMessage = DatabaseErrorMessages.ProviderRequired };

                var connString = _connectionStringFactory.BuildConnectionString(
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
                await using var connection = _providerFactory.GetProvider(provider.Value).Connections.Create(connString);
                await connection.OpenAsync(cancellationToken);
                await connection.CloseAsync();
                return new TestDbConnectionVM { IsSuccess = true };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Driver exceptions can contain passwords, hostnames and complete connection strings.
                // Never emit ex.Message (or ex itself) to client responses or application logs.
                return new TestDbConnectionVM
                {
                    IsSuccess = false,
                    ErrorMessage = DatabaseErrorMessages.ForException(ex)
                };
            }
        }

        public Task<string?> BuildDbConnectionAsync(
            BuildDbConnectionModel model,
            CancellationToken cancellationToken = default)
        {
            var provider = Enum.Parse<SqlAgentToolType>(model.Provider);
            return Task.FromResult<string?>(_connectionStringFactory.BuildConnectionString(provider, model));
        }
    }
}
