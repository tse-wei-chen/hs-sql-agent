using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using HsSqlAgent.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Controllers;

public class CustomSqlToolControllerTests
{
    private readonly Mock<ICustomSqlToolService> _toolServiceMock = new();
    private readonly Mock<IAuditService> _auditServiceMock = new();

    private CustomSqlToolController CreateController()
        => new(_toolServiceMock.Object, _auditServiceMock.Object);

    [Fact]
    public async Task CreateTool_ShouldRejectInvalidQueryDefinition()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Name = "bad_query",
            Description = "Bad query",
            Type = "Query",
            DefinitionJson = "{}"
        };

        var result = await controller.CreateTool(tool);

        Assert.IsType<BadRequestObjectResult>(result);
        _toolServiceMock.Verify(s => s.CreateToolAsync(It.IsAny<CustomSqlTool>()), Times.Never);
        _auditServiceMock.Verify(
            s => s.WriteLogAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateTool_ShouldRejectInvalidDmlDefinition()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Id = 7,
            Name = "bad_dml",
            Description = "Bad DML",
            Type = "DML",
            DefinitionJson = """{ "operation": "delete" }"""
        };

        var result = await controller.UpdateTool(tool.Id, tool);

        Assert.IsType<BadRequestObjectResult>(result);
        _toolServiceMock.Verify(s => s.UpdateToolAsync(It.IsAny<CustomSqlTool>()), Times.Never);
        _auditServiceMock.Verify(
            s => s.WriteLogAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateTool_ShouldSaveValidQueryDefinition()
    {
        var controller = CreateController();
        var tool = new CustomSqlTool
        {
            Name = "good_query",
            Description = "Good query",
            Type = "Query",
            DefinitionJson = """{ "tableName": "users" }"""
        };
        _toolServiceMock
            .Setup(s => s.CreateToolAsync(tool))
            .ReturnsAsync(tool);

        var result = await controller.CreateTool(tool);

        Assert.IsType<CreatedAtActionResult>(result);
        _toolServiceMock.Verify(s => s.CreateToolAsync(tool), Times.Once);
    }
}
