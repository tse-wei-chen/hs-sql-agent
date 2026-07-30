using Admin.Service.Interfaces;
using Admin.Service.Models;

namespace Admin.Service.Services;

public class SecurityPolicyRuntimeState : ISecurityPolicyRuntimeState
{
    private readonly Lock _sync = new();
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
