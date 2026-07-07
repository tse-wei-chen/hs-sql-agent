using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.Strategies;
using SqlAgent.Service.Validation;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CustomSqlToolController(ICustomSqlToolService toolService, IAuditService auditService) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

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
        var validationError = ValidateDefinition(tool);
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
        var validationError = ValidateDefinition(tool);
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

    private static object? ValidateDefinition(CustomSqlTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.DefinitionJson))
            return new { error = "DefinitionJson must not be empty." };

        try
        {
            // Sanitize {{placeholders}} → null so bare placeholders don't break JSON parsing
            var sanitized = System.Text.RegularExpressions.Regex.Replace(tool.DefinitionJson, @"\{\{[^}]*\}\}", "null");
            var errors = IsDml(tool)
                ? DefinitionValidator.Validate(JsonSerializer.Deserialize<DmlDefinition>(sanitized, JsonOptions))
                : DefinitionValidator.Validate(JsonSerializer.Deserialize<QueryDefinition>(sanitized, JsonOptions));

            return errors.Count == 0
                ? null
                : new { error = "Validation failed.", errors };
        }
        catch (JsonException ex)
        {
            return new { error = "DefinitionJson is invalid JSON.", detail = ex.Message };
        }
    }

    private static bool IsDml(CustomSqlTool tool)
        => string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase);

    [HttpPost("parse-sql")]
    [HasPermission("/runtime/custom-tools", "view")]
    public IActionResult ParseSql([FromBody] ParseSqlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
            return BadRequest(new { error = "SQL must not be empty." });

        try
        {
            var qd = SqlDefinitionParser.ParseQuery(request.Sql);
            var json = JsonSerializer.Serialize(qd, JsonOptions);
            return Ok(new { success = true, data = json });
        }
        catch (SqlParseException ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = $"Unexpected error: {ex.Message}" });
        }
    }

    [HttpPost("test-execute")]
    [HasPermission("/runtime/custom-tools", "view")]
    public async Task<IActionResult> TestExecute(
        [FromBody] CustomToolTestExecuteRequest request,
        [FromServices] ISqlStrategyFactory sqlStrategyFactory,
        [FromServices] IDbManagementService dbManagementService,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DefinitionJson))
            return BadRequest(new { error = "DefinitionJson is required." });

        var isQuery = string.Equals(request.Type, "Query", StringComparison.OrdinalIgnoreCase);
        var isDml = string.Equals(request.Type, "DML", StringComparison.OrdinalIgnoreCase);

        if (!isQuery && !isDml)
            return BadRequest(new { error = "Type must be 'Query' or 'DML'." });

        var db = await dbManagementService.GetDbByIdAsync(request.DbId, true, cancellationToken);
        if (db is not DbManagementPwdVM dbPwd)
            return NotFound(new { error = $"Database with ID {request.DbId} not found." });

        if (!Enum.TryParse<SqlAgentToolType>(dbPwd.SqlProvider, true, out var dbType))
            return BadRequest(new { error = $"Invalid SQL provider '{dbPwd.SqlProvider}'." });

        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
        var password = cryptoService.DecryptText(dbPwd.PasswordHash, hmacSecret);
        var strategy = sqlStrategyFactory.GetStrategy(dbType);
        var connectionString = strategy.BuildConnectionString(new BuildDbConnectionModelBase
        {
            Host = dbPwd.Host,
            Port = dbPwd.Port,
            Username = dbPwd.Username,
            Password = password,
            Database = dbPwd.Database,
            ExtraSettings = dbPwd.ExtraSettings
        });

        var definitionJson = ReplaceParametersInline(request.DefinitionJson, request.Parameters);

        try
        {
            string result;
            if (isQuery)
            {
                var queryDef = JsonSerializer.Deserialize<QueryDefinition>(definitionJson, JsonOptions);
                if (queryDef == null)
                    return BadRequest(new { error = "Failed to deserialize QueryDefinition." });

                var errors = DefinitionValidator.Validate(queryDef);
                if (errors.Count > 0)
                    return BadRequest(new { error = "Validation failed.", errors });

                result = await strategy.ExecuteQueryAsync(queryDef, connectionString, cancellationToken);
            }
            else
            {
                var dmlDef = JsonSerializer.Deserialize<DmlDefinition>(definitionJson, JsonOptions);
                if (dmlDef == null)
                    return BadRequest(new { error = "Failed to deserialize DmlDefinition." });

                var errors = DefinitionValidator.Validate(dmlDef);
                if (errors.Count > 0)
                    return BadRequest(new { error = "Validation failed.", errors });

                result = await strategy.ExecuteDmlAsync(connectionString, dmlDef, cancellationToken);
            }

            return Ok(new { success = true, data = result });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    private static string ReplaceParametersInline(string json, Dictionary<string, string>? parameters)
    {
        if (parameters == null || parameters.Count == 0) return json;
        foreach (var param in parameters)
        {
            var key = System.Text.RegularExpressions.Regex.Escape(param.Key);
            var valueStr = param.Value ?? "null";

            // "{{key}}" — placeholder inside a JSON string (lookbehind/lookahead verify quotes)
            var innerPattern = @"\{\{\s*" + key + @"\s*\}\}";
            var quotedPattern = @"(?<="")" + innerPattern + @"(?="")";
            json = System.Text.RegularExpressions.Regex.Replace(json, quotedPattern, valueStr.Replace("\"", "\\\""));

            // {{key}} — bare placeholder (e.g. "limit": {{limit}}): raw value, no extra quoting
            json = System.Text.RegularExpressions.Regex.Replace(json, innerPattern, valueStr);
        }
        return json;
    }
}
