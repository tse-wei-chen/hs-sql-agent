using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Admin.Service.Models;

namespace Admin.Service.Services;

public class CustomSqlToolService(
    IAdminContext context,
    IConfiguration configuration) : ICustomSqlToolService
{
    private readonly IAdminContext _context = context;
    private readonly IConfiguration _configuration = configuration;

    public async Task<List<CustomSqlTool>> GetAllToolsAsync()
    {
        return await _context.CustomSqlTools.ToListAsync();
    }

    public async Task<CustomSqlTool?> GetToolByIdAsync(int id)
    {
        return await _context.CustomSqlTools.FindAsync(id);
    }

    public async Task<CustomSqlTool?> GetToolByNameAsync(string name)
    {
        return await _context.CustomSqlTools.FirstOrDefaultAsync(t => t.Name == name);
    }

    public async Task<CustomSqlTool> CreateToolAsync(CustomSqlTool tool)
    {
        _context.CustomSqlTools.Add(tool);
        await _context.SaveChangesAsync();
        return tool;
    }

    public async Task<CustomSqlTool> UpdateToolAsync(CustomSqlTool tool)
    {
        tool.LastModifiedAt = DateTime.UtcNow;
        _context.CustomSqlTools.Update(tool);
        await _context.SaveChangesAsync();
        return tool;
    }

    public async Task<bool> DeleteToolAsync(int id)
    {
        var tool = await _context.CustomSqlTools.FindAsync(id);
        if (tool == null) return false;

        _context.CustomSqlTools.Remove(tool);
        await _context.SaveChangesAsync();
        return true;
    }
}
