using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.Extensions.Configuration;

namespace Admin.Service.Services;

public class RateLimitingRuntimeState(IConfiguration configuration) : IRateLimitingRuntimeState
{
#if NET9_0_OR_GREATER
    private readonly Lock _sync = new();
#else
    private readonly object _sync = new();
#endif
    private RateLimitingSettings _current = new()
    {
        PermitLimit = ParseInt(configuration["RateLimiting:PermitLimit"], 0),
        WindowSeconds = ParseInt(configuration["RateLimiting:WindowSeconds"], 0)
    };

    public RateLimitingSettings GetCurrent()
    {
        lock (_sync)
        {
            return new RateLimitingSettings
            {
                PermitLimit = _current.PermitLimit,
                WindowSeconds = _current.WindowSeconds
            };
        }
    }

    public void SetCurrent(RateLimitingSettings settings)
    {
        lock (_sync)
        {
            _current = new RateLimitingSettings
            {
                PermitLimit = settings.PermitLimit,
                WindowSeconds = settings.WindowSeconds
            };
        }
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
