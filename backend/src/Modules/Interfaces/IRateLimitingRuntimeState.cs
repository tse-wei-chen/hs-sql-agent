using Modules.Models;

namespace Modules.Interfaces;

public interface IRateLimitingRuntimeState
{
    RateLimitingSettings GetCurrent();
    void SetCurrent(RateLimitingSettings settings);
}
