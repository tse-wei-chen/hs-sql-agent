using Auth.Service.Data;
using Auth.Service.Interfaces;
using Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Auth.Service.Services;

public class TokenRevocationService(ICacheService cache, IAuthContext context) : ITokenRevocationService
{
    private readonly ICacheService _cache = cache;
    private readonly IAuthContext _context = context;

    public async Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"revoked:{jti}";

        var cached = await _cache.GetAsync<bool?>(cacheKey, cancellationToken);
        if (cached.HasValue)
            return cached.Value;

        var entry = await _context.TokenBlacklistEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Jti == jti, cancellationToken);

        if (entry is not null)
        {
            var ttl = entry.ExpiresAt - DateTime.UtcNow;
            if (ttl > TimeSpan.Zero)
                await _cache.SetAsync(cacheKey, true, ttl, cancellationToken);
            else
                await _cache.SetAsync(cacheKey, true, TimeSpan.FromMinutes(1), cancellationToken);

            return true;
        }

        await _cache.SetAsync(cacheKey, false, TimeSpan.FromMinutes(1), cancellationToken);
        return false;
    }

    public async Task RevokeAsync(string jti, DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        _context.TokenBlacklistEntries.Add(new Data.Entites.TokenBlacklistEntry
        {
            Jti = jti,
            ExpiresAt = expiresAt,
            RevokedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync(cancellationToken);

        var cacheKey = $"revoked:{jti}";
        var ttl = expiresAt - DateTime.UtcNow;
        if (ttl > TimeSpan.Zero)
            await _cache.SetAsync(cacheKey, true, ttl, cancellationToken);
        else
            await _cache.SetAsync(cacheKey, true, TimeSpan.FromMinutes(1), cancellationToken);
    }
}
