using System.Data.Common;

namespace SqlAgent.Service.Core.Execution;

public interface IDbConnectionFactory
{
    DbConnection Create(string connectionString);
}
