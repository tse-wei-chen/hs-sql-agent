using Admin.Service.Interfaces;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using SqlAgent.Service.Core.Providers;

namespace HsSqlAgent.Server.Tools;

[McpServerToolType]
public partial class SqlAgentTool(
    IHttpContextAccessor httpContextAccessor,
    ISqlProviderFactory sqlProviderFactory,
    IAuditService auditService,
    IDbSemanticService semanticService,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter sqlConcurrencyLimiter,
    ITypedQueryRuntime? typedQueryRuntime = null,
    TypedDmlRuntime? typedDmlRuntime = null)
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ISqlProviderFactory _sqlProviderFactory = sqlProviderFactory;
    private readonly IAuditService _auditService = auditService;
    private readonly IDbSemanticService _semanticService = semanticService;
    private readonly ISecurityPolicyRuntimeState _securityPolicyRuntimeState = securityPolicyRuntimeState;
    private readonly ISqlExecutionConcurrencyLimiter _sqlConcurrencyLimiter = sqlConcurrencyLimiter;
    private readonly ITypedQueryRuntime _typedQueryRuntime = typedQueryRuntime ?? new TypedQueryRuntime();
    private readonly TypedDmlRuntime _typedDmlRuntime = typedDmlRuntime ?? new TypedDmlRuntime();
}
