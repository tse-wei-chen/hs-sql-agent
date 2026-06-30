namespace HsSqlAgent.Server.Models;

public record CustomToolTestExecuteRequest(
    string DefinitionJson,
    string Type,
    int DbId,
    Dictionary<string, string>? Parameters = null
);
