using System.Text;
using System.Text.Json.Nodes;

namespace HsSqlAgent.Server.Middleware;

public class McpStringifiedArrayMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method == "POST")
        {
            context.Request.EnableBuffering();

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var bodyText = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                try
                {
                    var node = JsonNode.Parse(bodyText);
                    var modified = false;

                    if (node is JsonObject root &&
                        root.TryGetPropertyValue("method", out var methodNode) &&
                        methodNode?.ToString() == "tools/call" &&
                        root.TryGetPropertyValue("params", out var paramsNode) &&
                        paramsNode is JsonObject paramsObj &&
                        paramsObj.TryGetPropertyValue("arguments", out var argsNode) &&
                        argsNode is JsonObject argsObj)
                    {
                        var keysToUpdate = new Dictionary<string, JsonNode?>();

                        foreach (var prop in argsObj)
                        {
                            if (prop.Value != null && prop.Value.GetValueKind() == System.Text.Json.JsonValueKind.String)
                            {
                                var strValue = prop.Value.ToString().Trim();
                                if (strValue.StartsWith('[') && strValue.EndsWith(']'))
                                {
                                    try
                                    {
                                        var parsedArray = JsonNode.Parse(strValue);
                                        if (parsedArray is JsonArray)
                                        {
                                            keysToUpdate[prop.Key] = parsedArray;
                                            modified = true;
                                        }
                                    }
                                    catch
                                    {
                                        // Ignore parse errors, leave as string
                                    }
                                }
                            }
                        }

                        if (modified)
                        {
                            foreach (var kvp in keysToUpdate)
                            {
                                argsObj[kvp.Key] = kvp.Value;
                            }

                            var newBodyText = root.ToJsonString();
                            var bytes = Encoding.UTF8.GetBytes(newBodyText);
                            var stream = new MemoryStream(bytes);

                            context.Request.Body = stream;
                            context.Request.ContentLength = bytes.Length;
                        }
                    }
                }
                catch
                {
                    // If JSON is invalid, just pass it through
                    context.Request.Body.Position = 0;
                }
            }
        }

        await _next(context);
    }
}
