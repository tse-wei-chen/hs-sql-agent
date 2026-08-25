using System.Data.Common;

namespace HsSqlAgent.Provider.Abstractions;

public interface IDbConnectionFactory
{
    DbConnection Create(string connectionString);
}
