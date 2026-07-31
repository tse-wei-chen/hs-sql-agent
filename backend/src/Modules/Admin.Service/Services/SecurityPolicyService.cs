using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.EntityFrameworkCore;

namespace Admin.Service.Services;

public class SecurityPolicyService(
    IAdminContext context,
    ISecurityPolicyRuntimeState runtimeState,
    ISecurityPolicyChangePublisher changePublisher) : ISecurityPolicyService
{
    private readonly IAdminContext _context = context;
    private readonly ISecurityPolicyRuntimeState _runtimeState = runtimeState;
    private readonly ISecurityPolicyChangePublisher _changePublisher = changePublisher;

    public async Task<SecurityPolicyModel> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await _context.SecurityPolicySettings
            .AsNoTracking()
            .SingleAsync(x => x.Id == SecurityPolicySettings.SingletonId, cancellationToken);
        var model = SecurityPolicyModel.FromEntity(entity);
        _runtimeState.SetCurrent(model);
        return model;
    }

    public async Task<SecurityPolicyModel> UpdateAsync(
        SecurityPolicyModel request,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        var entity = await _context.SecurityPolicySettings
            .SingleAsync(x => x.Id == SecurityPolicySettings.SingletonId, cancellationToken);

        entity.QueryMaxRows = request.QueryMaxRows;
        entity.QueryTimeoutSeconds = request.QueryTimeoutSeconds;
        entity.RequireWhereForUpdate = request.RequireWhereForUpdate;
        entity.RequireWhereForDelete = request.RequireWhereForDelete;
        entity.AllowFullTableUpdate = request.AllowFullTableUpdate;
        entity.AllowFullTableDelete = request.AllowFullTableDelete;
        entity.DmlMaxAffectedRows = request.DmlMaxAffectedRows;
        entity.KeyPermitLimit = request.KeyPermitLimit;
        entity.KeyWindowSeconds = request.KeyWindowSeconds;
        entity.MaxConcurrentSql = request.MaxConcurrentSql;
        entity.UpdatedAt = DateTime.UtcNow;
        entity.UpdatedBy = actorId;

        await _context.SaveChangesAsync(cancellationToken);

        var model = SecurityPolicyModel.FromEntity(entity);
        _runtimeState.SetCurrent(model);
        await _changePublisher.PublishAsync(model, cancellationToken);
        return model;
    }

    private static void Validate(SecurityPolicyModel request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.QueryMaxRows is < 1 or > 100_000)
            throw new ArgumentException("QueryMaxRows must be between 1 and 100000.");
        if (request.QueryTimeoutSeconds is < 1 or > 600)
            throw new ArgumentException("QueryTimeoutSeconds must be between 1 and 600.");
        if (request.DmlMaxAffectedRows is < 1 or > 1_000_000)
            throw new ArgumentException("DmlMaxAffectedRows must be between 1 and 1000000.");
        if (request.KeyPermitLimit is < 1 or > 1_000_000)
            throw new ArgumentException("KeyPermitLimit must be between 1 and 1000000.");
        if (request.KeyWindowSeconds is < 1 or > 86_400)
            throw new ArgumentException("KeyWindowSeconds must be between 1 and 86400.");
        if (request.MaxConcurrentSql is < 1 or > 10_000)
            throw new ArgumentException("MaxConcurrentSql must be between 1 and 10000.");
    }
}
