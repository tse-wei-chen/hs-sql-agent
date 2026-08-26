using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using HsSqlAgent.SqlCore.Enums;
using SqlAgent.Service.Factories;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CustomSqlToolController(ICustomSqlToolService toolService, IAuditService auditService) : ControllerBase
{
    [HttpGet]
    [HasPermission("/runtime/custom-tools", "view")]
    public async Task<IActionResult> GetAllTools()
        => Ok(await toolService.GetAllToolsAsync());

    [HttpGet("{id}")]
    [HasPermission("/runtime/custom-tools", "view")]
    public async Task<IActionResult> GetTool(int id)
    {
        var tool = await toolService.GetToolByIdAsync(id);
        return tool == null ? NotFound() : Ok(tool);
    }

    [HttpPost]
    [HasPermission("/runtime/custom-tools", "create")]
    public async Task<IActionResult> CreateTool([FromBody] CustomSqlTool tool)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (tool.DbManagementId is null) return BadRequest(new { error = "A target database is required." });
        var validationError = ValidateDraft(tool);
        if (validationError != null) return BadRequest(validationError);

        var created = await toolService.CreateToolAsync(tool);
        await auditService.WriteLogAsync("tool.custom.created", created.Id.ToString(), "success", $"Name: {created.Name}");
        return CreatedAtAction(nameof(GetTool), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    [HasPermission("/runtime/custom-tools", "edit")]
    public async Task<IActionResult> UpdateTool(int id, [FromBody] CustomSqlTool tool)
    {
        if (id != tool.Id) return BadRequest("ID mismatch");
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (tool.DbManagementId is null) return BadRequest(new { error = "A target database is required." });
        if (await toolService.GetToolByIdAsync(id) == null) return NotFound();
        var validationError = ValidateDraft(tool);
        if (validationError != null) return BadRequest(validationError);

        var updated = await toolService.UpdateToolAsync(tool);
        await auditService.WriteLogAsync("tool.custom.updated", id.ToString(), "success", $"Name: {updated.Name}");
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    [HasPermission("/runtime/custom-tools", "delete")]
    public async Task<IActionResult> DeleteTool(int id)
    {
        var deleted = await toolService.DeleteToolAsync(id);
        if (!deleted) return NotFound();
        await auditService.WriteLogAsync("tool.custom.deleted", id.ToString(), "success");
        return NoContent();
    }

    [HttpGet("{id}/revisions")]
    [HasPermission("/runtime/custom-tools", "view")]
    public async Task<IActionResult> GetRevisions(int id, CancellationToken cancellationToken)
        => await toolService.GetToolByIdAsync(id) == null
            ? NotFound()
            : Ok(await toolService.GetRevisionsAsync(id, cancellationToken));

    [HttpGet("{id}/impact")]
    [HasPermission("/runtime/custom-tools", "view")]
    public async Task<IActionResult> GetImpact(int id, CancellationToken cancellationToken)
    {
        var impact = await toolService.GetImpactAsync(id, cancellationToken);
        return impact == null ? NotFound() : Ok(impact);
    }

    [HttpPost("{id}/publish")]
    [HasPermission("/runtime/custom-tools", "edit")]
    public async Task<IActionResult> Publish(
        int id,
        [FromServices] ISecurityPolicyRuntimeState securityPolicyRuntimeState,
        [FromServices] IDbManagementService dbManagementService,
        CancellationToken cancellationToken)
    {
        var tool = await toolService.GetToolByIdAsync(id);
        if (tool == null) return NotFound();
        var validationError = await ValidateDefinitionAsync(
            tool,
            securityPolicyRuntimeState.GetCurrent(),
            dbManagementService,
            cancellationToken);
        if (validationError != null) return BadRequest(validationError);

        try
        {
            var published = await toolService.PublishAsync(id, CurrentActor(), cancellationToken);
            await auditService.WriteLogAsync("tool.custom.published", id.ToString(), "success",
                $"Name: {tool.Name}; DB: {tool.DbManagementId}");
            return Ok(published);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (DbUpdateException)
        {
            return Conflict(new { error = "Another tool with the same published database/name identity already exists." });
        }
    }

    [HttpPost("{id}/disable")]
    [HasPermission("/runtime/custom-tools", "edit")]
    public async Task<IActionResult> Disable(int id, CancellationToken cancellationToken)
    {
        var disabled = await toolService.DisableAsync(id, cancellationToken);
        if (disabled == null) return NotFound();
        await auditService.WriteLogAsync("tool.custom.disabled", id.ToString(), "success", $"Name: {disabled.Name}");
        return Ok(disabled);
    }

    [HttpPost("{id}/rollback/{revisionId}")]
    [HasPermission("/runtime/custom-tools", "edit")]
    public async Task<IActionResult> Rollback(
        int id,
        int revisionId,
        [FromServices] ISecurityPolicyRuntimeState securityPolicyRuntimeState,
        [FromServices] IDbManagementService dbManagementService,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await toolService.GetToolByIdAsync(id);
            if (current == null) return NotFound();
            var target = (await toolService.GetRevisionsAsync(id, cancellationToken))
                .FirstOrDefault(x => x.Id == revisionId);
            if (target == null) return NotFound(new { error = "The requested revision does not belong to this tool." });
            var validationError = await ValidateDefinitionAsync(new CustomSqlTool
            {
                Name = target.Name,
                SqlTemplate = target.SqlTemplate,
                Type = target.Type,
                ParametersJson = target.ParametersJson,
                DbManagementId = current.DbManagementId
            }, securityPolicyRuntimeState.GetCurrent(), dbManagementService, cancellationToken);
            if (validationError != null) return BadRequest(validationError);

            var rolledBack = await toolService.RollbackAsync(id, revisionId, CurrentActor(), cancellationToken);
            if (rolledBack == null) return NotFound();
            await auditService.WriteLogAsync("tool.custom.rolled-back", id.ToString(), "success",
                $"Source revision: {revisionId}");
            return Ok(rolledBack);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (DbUpdateException)
        {
            return Conflict(new { error = "Another tool with the same published database/name identity already exists." });
        }
    }

    private static async Task<object?> ValidateDefinitionAsync(
        CustomSqlTool tool,
        SecurityPolicyModel? policy,
        IDbManagementService dbManagementService,
        CancellationToken cancellationToken)
    {
        var draftError = ValidateDraft(tool);
        if (draftError != null) return draftError;
        if (tool.DbManagementId is null)
            return new { error = "A target database is required for SQL validation." };

        var db = await dbManagementService.GetDbByIdAsync(tool.DbManagementId.Value, false, cancellationToken);
        if (db == null)
            return new { error = $"Database with ID {tool.DbManagementId} not found." };
        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var dbType))
            return new { error = $"Invalid SQL provider '{db.SqlProvider}'." };

        try
        {
            var sql = CustomToolSqlTemplate.RenderForValidation(tool.SqlTemplate, tool.ParametersJson);
            if (IsDml(tool))
            {
                var parsedDml = CoreSqlTextParser.ParseDml(sql, dbType);
                TypedDmlRuntime.EnsureSupportedStatement(parsedDml.Statement);

                _ = CoreDmlCompiler.CreateDefault().Compile(
                    parsedDml,
                    dbType,
                    new SqlPlanValidationContext("custom-tool-definition-validation"),
                    new DmlCompilationPolicy(
                        policy?.RequireWhereForUpdate ?? true,
                        policy?.RequireWhereForDelete ?? true,
                        policy?.AllowFullTableUpdate ?? false,
                        policy?.AllowFullTableDelete ?? false));
                return null;
            }

            var parsed = CoreSqlTextParser.ParseQuery(sql, dbType);
            _ = CoreSqlCompiler.CreateDefault().Compile(
                parsed,
                dbType,
                new SqlPlanValidationContext("custom-tool-definition-validation"),
                new SqlExecutionPlanPolicy(policy?.QueryMaxRows ?? 0));
            return null;
        }
        catch (Exception ex) when (ex is SqlParseException or InvalidOperationException or JsonException or NotSupportedException)
        {
            return new { error = "SQL template validation failed.", detail = ex.Message };
        }
    }

    private static object? ValidateDraft(CustomSqlTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name)
            || !System.Text.RegularExpressions.Regex.IsMatch(tool.Name, @"^[A-Za-z][A-Za-z0-9_-]{0,99}$"))
            return new { error = "Tool name must start with a letter and contain only letters, numbers, underscores, or hyphens." };
        if (!string.Equals(tool.Type, "Query", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase))
            return new { error = "Type must be 'Query' or 'DML'." };
        if (string.IsNullOrWhiteSpace(tool.SqlTemplate))
            return new { error = "SqlTemplate must not be empty." };

        try
        {
            CustomToolSqlTemplate.ParseParameters(tool.ParametersJson);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return new { error = "Parameter schema validation failed.", detail = ex.Message };
        }
    }

    private static bool IsDml(CustomSqlTool tool)
        => string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase);

    [HttpPost("test-execute")]
    [HasPermission("/runtime/custom-tools", "edit")]
    public async Task<IActionResult> TestExecute(
        [FromBody] CustomToolTestExecuteRequest request,
        [FromServices] ISqlProviderFactory providerFactory,
        [FromServices] ISqlConnectionStringFactory connectionStringFactory,
        [FromServices] IDbManagementService dbManagementService,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<McpKeySettings> mcpKeySettings,
        [FromServices] ISecurityPolicyRuntimeState securityPolicyRuntimeState,
        [FromServices] ISqlExecutionConcurrencyLimiter sqlConcurrencyLimiter,
        [FromServices] ITypedQueryRuntime typedQueryRuntime,
        CancellationToken cancellationToken)
    {
        var tool = await toolService.GetToolByIdAsync(request.ToolId);
        if (tool == null) return NotFound(new { error = $"Custom tool {request.ToolId} was not found." });
        if (tool.DbManagementId is null) return BadRequest(new { error = "The custom tool is not bound to a database." });

        var isQuery = string.Equals(tool.Type, "Query", StringComparison.OrdinalIgnoreCase);
        var isDml = string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase);
        if (!isQuery && !isDml)
            return BadRequest(new { error = "Type must be 'Query' or 'DML'." });

        var db = await dbManagementService.GetDbByIdAsync(tool.DbManagementId.Value, true, cancellationToken);
        if (db is not DbManagementPwdVM dbPwd)
            return NotFound(new { error = $"Database with ID {tool.DbManagementId} not found." });
        if (!Enum.TryParse<SqlAgentToolType>(dbPwd.SqlProvider, true, out var dbType))
            return BadRequest(new { error = $"Invalid SQL provider '{dbPwd.SqlProvider}'." });

        var definitionError = await ValidateDefinitionAsync(
            tool,
            securityPolicyRuntimeState.GetCurrent(),
            dbManagementService,
            cancellationToken);
        if (definitionError != null) return BadRequest(definitionError);

        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
        var password = cryptoService.DecryptText(dbPwd.PasswordHash, hmacSecret);
        var provider = providerFactory.GetProvider(dbType);
        var connectionString = connectionStringFactory.BuildConnectionString(dbType, new BuildDbConnectionModelBase
        {
            Host = dbPwd.Host,
            Port = dbPwd.Port,
            Username = dbPwd.Username,
            Password = password,
            Database = dbPwd.Database,
            ExtraSettings = dbPwd.ExtraSettings
        });
        var runtimePolicy = securityPolicyRuntimeState.GetCurrent();

        try
        {
            var sql = CustomToolSqlTemplate.Render(
                tool.SqlTemplate,
                tool.ParametersJson,
                request.Parameters ?? new Dictionary<string, object?>());
            await using var lease = await sqlConcurrencyLimiter.TryAcquireAsync(cancellationToken);
            if (lease is null)
            {
                return StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    new { success = false, error = "Maximum concurrent SQL operations reached." });
            }

            string result;
            if (isQuery)
            {
                var parsed = CoreSqlTextParser.ParseQuery(sql, dbType);
                var execution = await typedQueryRuntime.ExecuteAsync(
                    provider,
                    connectionString,
                    parsed,
                    runtimePolicy,
                    allowedTables: null,
                    cancellationToken);
                result = JsonSerializer.Serialize(execution.Rows);
            }
            else
            {
                var parsedDml = CoreSqlTextParser.ParseDml(sql, dbType);
                TypedDmlRuntime.EnsureSupportedStatement(parsedDml.Statement);

                var session = await new TypedDmlRuntime().PreviewAsync(
                    provider,
                    connectionString,
                    parsedDml,
                    runtimePolicy,
                    allowedTables: null,
                    cancellationToken);
                result = JsonSerializer.Serialize(new
                {
                    operation = session.Plan.Operation.ToString(),
                    table = session.Plan.TableName,
                    affectedRows = session.Preview.AffectedRows,
                    preview = session.Preview.Rows,
                    committed = false
                });
            }

            await auditService.WriteLogAsync(
                "tool.custom.test-executed",
                tool.Id.ToString(),
                "success",
                $"Type: {tool.Type}; DB: {tool.DbManagementId}; Commit: never",
                cancellationToken);
            return Ok(new { success = true, data = result });
        }
        catch (Exception ex)
        {
            await auditService.WriteLogAsync(
                "tool.custom.test-executed",
                tool.Id.ToString(),
                "failed",
                $"Type: {tool.Type}; DB: {tool.DbManagementId}; Error: {ex.GetType().Name}",
                cancellationToken);
            return Ok(new { success = false, error = ex.Message });
        }
    }

    private string? CurrentActor()
        => User.FindFirstValue(JwtRegisteredClaimNames.Sub)
           ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? User.Identity?.Name;
}
