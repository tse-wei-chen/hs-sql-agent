using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IRateLimitingRuntimeState
{
    RateLimitingSettings GetCurrent();
    void SetCurrent(RateLimitingSettings settings);
}
