using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface ISecurityPolicyRuntimeState
{
    SecurityPolicyModel GetCurrent();
    void SetCurrent(SecurityPolicyModel policy);
}
