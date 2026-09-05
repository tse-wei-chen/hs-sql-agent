using Admin.Service.Interfaces;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Services;
using ModelContextProtocol.Server;

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
    TypedDmlRuntime? typedDmlRuntime = null,
    IDmlApprovalProvider? dmlApprovalProvider = null,
    IDmlApprovalCompletionSink? dmlApprovalCompletionSink = null)
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ISqlProviderFactory _sqlProviderFactory = sqlProviderFactory;
    private readonly IAuditService _auditService = auditService;
    private readonly IDbSemanticService _semanticService = semanticService;
    private readonly ISecurityPolicyRuntimeState _securityPolicyRuntimeState = securityPolicyRuntimeState;
    private readonly ISqlExecutionConcurrencyLimiter _sqlConcurrencyLimiter = sqlConcurrencyLimiter;
    private readonly ITypedQueryRuntime _typedQueryRuntime = typedQueryRuntime ?? new TypedQueryRuntime();
    private readonly TypedDmlRuntime _typedDmlRuntime = typedDmlRuntime ?? new TypedDmlRuntime();
    private readonly IDmlApprovalProvider? _dmlApprovalProvider = dmlApprovalProvider;
    private readonly IDmlApprovalCompletionSink? _dmlApprovalCompletionSink = dmlApprovalCompletionSink;
}
