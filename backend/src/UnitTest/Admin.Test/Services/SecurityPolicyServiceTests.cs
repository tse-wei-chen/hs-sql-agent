using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace Admin.Test.Services;

public class SecurityPolicyServiceTests
{
    [Fact]
    public async Task UpdateAsync_ShouldPersistAndRefreshRuntimeState()
    {
        var context = new Mock<IAdminContext>();
        var runtimeState = new Mock<ISecurityPolicyRuntimeState>();
        var entity = new SecurityPolicySettings { Id = SecurityPolicySettings.SingletonId };
        context.Setup(c => c.SecurityPolicySettings)
            .ReturnsDbSet(new List<SecurityPolicySettings> { entity });
        context.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        var service = new SecurityPolicyService(context.Object, runtimeState.Object);
        var request = new SecurityPolicyModel
        {
            QueryMaxRows = 250,
            QueryTimeoutSeconds = 15,
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true,
            DmlMaxAffectedRows = 25,
            IpPermitLimit = 30,
            IpWindowSeconds = 60,
            KeyPermitLimit = 50,
            KeyWindowSeconds = 60,
            MaxConcurrentSql = 8
        };

        var result = await service.UpdateAsync(
            request,
            "admin-user",
            TestContext.Current.CancellationToken);

        Assert.Equal(250, entity.QueryMaxRows);
        Assert.Equal("admin-user", entity.UpdatedBy);
        Assert.Equal(8, result.MaxConcurrentSql);
        context.Verify(c => c.SaveChangesAsync(TestContext.Current.CancellationToken), Times.Once);
        runtimeState.Verify(
            s => s.SetCurrent(It.Is<SecurityPolicyModel>(p =>
                p.QueryMaxRows == 250 && p.DmlMaxAffectedRows == 25)),
            Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectOutOfRangeValuesBeforeSaving()
    {
        var context = new Mock<IAdminContext>();
        var runtimeState = new Mock<ISecurityPolicyRuntimeState>();
        var service = new SecurityPolicyService(context.Object, runtimeState.Object);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(
                new SecurityPolicyModel { QueryMaxRows = 0 },
                "admin-user",
                TestContext.Current.CancellationToken));

        Assert.Contains("QueryMaxRows", exception.Message);
        context.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        runtimeState.Verify(s => s.SetCurrent(It.IsAny<SecurityPolicyModel>()), Times.Never);
    }
}
