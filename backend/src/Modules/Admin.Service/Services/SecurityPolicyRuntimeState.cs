using Admin.Service.Interfaces;
using Admin.Service.Models;

namespace Admin.Service.Services;

public class SecurityPolicyRuntimeState : ISecurityPolicyRuntimeState
{
#if NET9_0_OR_GREATER
    private readonly Lock _sync = new();
#else
    private readonly object _sync = new();
#endif
    private SecurityPolicyModel _current = new();

    public SecurityPolicyModel GetCurrent()
    {
        lock (_sync)
        {
            return _current.Clone();
        }
    }

    public void SetCurrent(SecurityPolicyModel policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_sync)
        {
            _current = policy.Clone();
        }
    }
}
