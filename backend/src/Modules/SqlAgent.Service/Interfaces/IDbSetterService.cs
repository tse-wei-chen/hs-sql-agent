namespace SqlAgent.Service.Interfaces;

public interface IDbSetterService
{
    Task<TestDbConnectionVM> TestDbConnectionAsync(TestDbConnectionBase request, CancellationToken cancellationToken = default);
    Task<string?> BuildDbConnectionAsync(BuildDbConnectionModel model, CancellationToken cancellationToken = default);
}
