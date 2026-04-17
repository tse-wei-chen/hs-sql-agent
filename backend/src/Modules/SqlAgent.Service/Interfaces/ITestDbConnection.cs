
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Interfaces;

public interface ITestDbConnection
{
    Task<TestDbConnectionVM> TestDbConnectionAsync(TestDbConnectionRequest request, CancellationToken cancellationToken = default);
}