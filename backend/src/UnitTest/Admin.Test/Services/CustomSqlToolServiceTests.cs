using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace Admin.Test.Services;

public class CustomSqlToolServiceTests
{
    private readonly Mock<IAdminContext> _contextMock;
    private readonly Mock<IConfiguration> _configurationMock;
    private readonly CustomSqlToolService _service;

    public CustomSqlToolServiceTests()
    {
        _contextMock = new Mock<IAdminContext>();
        _configurationMock = new Mock<IConfiguration>();

        _service = new CustomSqlToolService(_contextMock.Object, _configurationMock.Object);
    }

    [Fact]
    public async Task GetAllToolsAsync_ShouldReturnEmptyList_WhenNoToolsExist()
    {
        // Arrange
        _contextMock.Setup(c => c.CustomSqlTools).ReturnsDbSet(new List<CustomSqlTool>());

        // Act
        var result = await _service.GetAllToolsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllToolsAsync_ShouldReturnAllTools_WhenToolsExist()
    {
        // Arrange
        var tools = new List<CustomSqlTool>
        {
            new() { Id = 1, Name = "Tool1", Description = "Desc1", DefinitionJson = "{}" },
            new() { Id = 2, Name = "Tool2", Description = "Desc2", DefinitionJson = "{}" }
        };
        _contextMock.Setup(c => c.CustomSqlTools).ReturnsDbSet(tools);

        // Act
        var result = await _service.GetAllToolsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == 1 && r.Name == "Tool1");
        Assert.Contains(result, r => r.Id == 2 && r.Name == "Tool2");
    }

    [Fact]
    public async Task GetToolByIdAsync_ShouldReturnTool_WhenIdExists()
    {
        // Arrange
        var toolId = 1;
        var tool = new CustomSqlTool { Id = toolId, Name = "Tool1" };
        
        var mockDbSet = new Mock<Microsoft.EntityFrameworkCore.DbSet<CustomSqlTool>>();
        mockDbSet.Setup(x => x.FindAsync(It.IsAny<object[]>())).ReturnsAsync(tool);
        _contextMock.Setup(c => c.CustomSqlTools).Returns(mockDbSet.Object);

        // Act
        var result = await _service.GetToolByIdAsync(toolId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(toolId, result.Id);
        Assert.Equal("Tool1", result.Name);
    }

    [Fact]
    public async Task GetToolByIdAsync_ShouldReturnNull_WhenIdDoesNotExist()
    {
        // Arrange
        var nonExistentId = 999;
        var mockDbSet = new Mock<Microsoft.EntityFrameworkCore.DbSet<CustomSqlTool>>();
        mockDbSet.Setup(x => x.FindAsync(It.IsAny<object[]>())).ReturnsAsync((CustomSqlTool?)null);
        _contextMock.Setup(c => c.CustomSqlTools).Returns(mockDbSet.Object);

        // Act
        var result = await _service.GetToolByIdAsync(nonExistentId);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetToolByNameAsync_ShouldReturnTool_WhenNameExists()
    {
        // Arrange
        var toolName = "ExistingTool";
        var tools = new List<CustomSqlTool>
        {
            new() { Id = 1, Name = "OtherTool" },
            new() { Id = 2, Name = toolName }
        };
        _contextMock.Setup(c => c.CustomSqlTools).ReturnsDbSet(tools);

        // Act
        var result = await _service.GetToolByNameAsync(toolName);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Id);
        Assert.Equal(toolName, result.Name);
    }

    [Fact]
    public async Task GetToolByNameAsync_ShouldReturnNull_WhenNameDoesNotExist()
    {
        // Arrange
        var tools = new List<CustomSqlTool>
        {
            new() { Id = 1, Name = "OtherTool" }
        };
        _contextMock.Setup(c => c.CustomSqlTools).ReturnsDbSet(tools);

        // Act
        var result = await _service.GetToolByNameAsync("NonExistentTool");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateToolAsync_ShouldAddToolAndSaveChanges()
    {
        // Arrange
        var newTool = new CustomSqlTool
        {
            Name = "NewTool",
            Description = "New tool desc",
            DefinitionJson = "{}"
        };
        
        var mockDbSet = new Mock<Microsoft.EntityFrameworkCore.DbSet<CustomSqlTool>>();
        _contextMock.Setup(c => c.CustomSqlTools).Returns(mockDbSet.Object);

        // Act
        var result = await _service.CreateToolAsync(newTool);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(newTool.Name, result.Name);
        
        mockDbSet.Verify(m => m.Add(It.Is<CustomSqlTool>(t => t.Name == "NewTool")), Times.Once);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateToolAsync_ShouldUpdateLastModifiedAtAndSaveChanges()
    {
        // Arrange
        var existingTool = new CustomSqlTool
        {
            Id = 1,
            Name = "UpdatedTool",
            LastModifiedAt = DateTime.UtcNow.AddDays(-1)
        };
        
        var mockDbSet = new Mock<Microsoft.EntityFrameworkCore.DbSet<CustomSqlTool>>();
        _contextMock.Setup(c => c.CustomSqlTools).Returns(mockDbSet.Object);

        var beforeUpdate = DateTime.UtcNow;

        // Act
        var result = await _service.UpdateToolAsync(existingTool);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.LastModifiedAt >= beforeUpdate);
        
        mockDbSet.Verify(m => m.Update(It.Is<CustomSqlTool>(t => t.Id == 1)), Times.Once);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteToolAsync_ShouldRemoveToolAndReturnTrue_WhenIdExists()
    {
        // Arrange
        var toolId = 1;
        var existingTool = new CustomSqlTool { Id = toolId };
        
        var mockDbSet = new Mock<Microsoft.EntityFrameworkCore.DbSet<CustomSqlTool>>();
        mockDbSet.Setup(x => x.FindAsync(It.IsAny<object[]>())).ReturnsAsync(existingTool);
        _contextMock.Setup(c => c.CustomSqlTools).Returns(mockDbSet.Object);

        // Act
        var result = await _service.DeleteToolAsync(toolId);

        // Assert
        Assert.True(result);
        mockDbSet.Verify(m => m.Remove(existingTool), Times.Once);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteToolAsync_ShouldReturnFalse_WhenIdDoesNotExist()
    {
        // Arrange
        var nonExistentId = 999;
        
        var mockDbSet = new Mock<Microsoft.EntityFrameworkCore.DbSet<CustomSqlTool>>();
        mockDbSet.Setup(x => x.FindAsync(It.IsAny<object[]>())).ReturnsAsync((CustomSqlTool?)null);
        _contextMock.Setup(c => c.CustomSqlTools).Returns(mockDbSet.Object);

        // Act
        var result = await _service.DeleteToolAsync(nonExistentId);

        // Assert
        Assert.False(result);
        mockDbSet.Verify(m => m.Remove(It.IsAny<CustomSqlTool>()), Times.Never);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
