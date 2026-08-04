namespace HsSqlAgent.Server.Models;

public record CustomToolTestExecuteRequest(
    int ToolId,
    Dictionary<string, object?>? Parameters = null
);
