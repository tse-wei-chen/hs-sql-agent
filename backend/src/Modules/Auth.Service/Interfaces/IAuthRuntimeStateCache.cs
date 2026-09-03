using Auth.Service.Data;
using Auth.Service.Models;

namespace Auth.Service.Interfaces;

public interface IAuthRuntimeStateCache
{
    Task<AuthRuntimeState> GetOrLoadAsync(
        IAuthContext context,
        int memberId,
        CancellationToken cancellationToken = default);

    Task RunWithBarrierAsync(
        int memberId,
        string reason,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default);

    Task RunWithBarriersAsync(
        IReadOnlyCollection<int> memberIds,
        string reason,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default);

    Task InvalidateAsync(
        int memberId,
        CancellationToken cancellationToken = default);
}
