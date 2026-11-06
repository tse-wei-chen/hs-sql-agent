using HsSqlAgent.Server.Tools;
using Xunit;

namespace HsSqlAgent.Server.Test.Compatibility;

public sealed class SqlAgentToolConstructorCompatibilityTests
{
    [Fact]
    public void V300PublicConstructor_StillHasItsExactTenParameterClrSignature()
    {
        // Optional parameters are encoded at the caller's compilation site.
        // Changing the primary constructor from 10 to 17 arguments removes the
        // CLR entry point and breaks previously compiled consuming assemblies.
        var legacy = Assert.Single(typeof(SqlAgentTool).GetConstructors()
            .Where(constructor => constructor.GetParameters().Length == 10));

        string[] v300ParameterTypes =
        [
            "Microsoft.AspNetCore.Http.IHttpContextAccessor",
            "HsSqlAgent.Provider.Abstractions.ISqlProviderFactory",
            "Admin.Service.Interfaces.IAuditService",
            "Admin.Service.Interfaces.IDbSemanticService",
            "Admin.Service.Interfaces.ISecurityPolicyRuntimeState",
            "HsSqlAgent.Server.Services.ISqlExecutionConcurrencyLimiter",
            "HsSqlAgent.Server.Services.ITypedQueryRuntime",
            "HsSqlAgent.Server.Services.TypedDmlRuntime",
            "HsSqlAgent.Approvals.IDmlApprovalProvider",
            "HsSqlAgent.Approvals.IDmlApprovalCompletionSink"
        ];

        Assert.Equal(v300ParameterTypes,
            legacy.GetParameters().Select(parameter => parameter.ParameterType.FullName).ToArray());
    }
}
