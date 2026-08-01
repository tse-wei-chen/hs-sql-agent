using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class SecurityPolicySyncTests
{
    [Fact]
    public async Task DatabaseSynchronizer_ShouldApplyNewerPolicy()
    {
        var updatedAt = DateTime.UtcNow;
        var context = new Mock<IAdminContext>();
        context.Setup(x => x.SecurityPolicySettings).ReturnsDbSet(
        [
            new SecurityPolicySettings
            {
                Id = SecurityPolicySettings.SingletonId,
                QueryMaxRows = 500,
                UpdatedAt = updatedAt
            }
        ]);
        var runtimeState = new Mock<ISecurityPolicyRuntimeState>();
        runtimeState.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            QueryMaxRows = 100,
            UpdatedAt = updatedAt.AddSeconds(-1)
        });
        var synchronizer = new SecurityPolicyDatabaseSynchronizer(
            context.Object,
            runtimeState.Object);

        await synchronizer.RefreshAsync(TestContext.Current.CancellationToken);

        runtimeState.Verify(
            x => x.SetCurrent(It.Is<SecurityPolicyModel>(policy =>
                policy.QueryMaxRows == 500 && policy.UpdatedAt == updatedAt)),
            Times.Once);
    }

    [Fact]
    public async Task DatabaseSynchronizer_ShouldNotReplaceNewerRuntimePolicy()
    {
        var updatedAt = DateTime.UtcNow;
        var context = new Mock<IAdminContext>();
        context.Setup(x => x.SecurityPolicySettings).ReturnsDbSet(
        [
            new SecurityPolicySettings
            {
                Id = SecurityPolicySettings.SingletonId,
                UpdatedAt = updatedAt
            }
        ]);
        var runtimeState = new Mock<ISecurityPolicyRuntimeState>();
        runtimeState.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            UpdatedAt = updatedAt.AddSeconds(1)
        });
        var synchronizer = new SecurityPolicyDatabaseSynchronizer(
            context.Object,
            runtimeState.Object);

        await synchronizer.RefreshAsync(TestContext.Current.CancellationToken);

        runtimeState.Verify(
            x => x.SetCurrent(It.IsAny<SecurityPolicyModel>()),
            Times.Never);
    }
}
