using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.Extensions.Configuration;

namespace Admin.Service.Services;

public class RateLimitingRuntimeState(IConfiguration configuration) : IRateLimitingRuntimeState
{
    private readonly Lock _sync = new();
    private RateLimitingSettings _current = new()
    {
        PermitLimit = ParseInt(configuration["RateLimiting:PermitLimit"], 0),
        WindowSeconds = ParseInt(configuration["RateLimiting:WindowSeconds"], 0),
        QueueLimit = ParseInt(configuration["RateLimiting:QueueLimit"], 0)
    };

    public RateLimitingSettings GetCurrent()
    {
        lock (_sync)
        {
            return new RateLimitingSettings
            {
                PermitLimit = _current.PermitLimit,
                WindowSeconds = _current.WindowSeconds,
                QueueLimit = _current.QueueLimit
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
                WindowSeconds = settings.WindowSeconds,
                QueueLimit = settings.QueueLimit
            };
        }
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
