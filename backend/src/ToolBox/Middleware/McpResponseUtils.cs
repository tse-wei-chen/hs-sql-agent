using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolBox.Middleware;

public static class McpResponseUtils
{
    public static string ProcessMcpResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                ProcessSingleResponse(obj);
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject itemObj)
                    {
                        ProcessSingleResponse(itemObj);
                    }
                }
            }
            return node?.ToJsonString() ?? json;
        }
        catch
        {
            return json;
        }
    }

    private static void ProcessSingleResponse(JsonObject response)
    {
        if (response.ContainsKey("result") && response["result"] is JsonObject result &&
            result.ContainsKey("content") && result["content"] is JsonArray content)
        {
            foreach (var item in content)
            {
                if (item is JsonObject contentItem &&
                    contentItem.ContainsKey("type") && contentItem["type"]?.GetValue<string>() == "text" &&
                    contentItem.ContainsKey("text"))
                {
                    var innerText = contentItem["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(innerText))
                    {
                        if (innerText.TrimStart().StartsWith("{") && innerText.Contains("\"Success\""))
                        {
                            try
                            {
                                var innerNode = JsonNode.Parse(innerText);
                                if (innerNode is JsonObject innerObj && innerObj.ContainsKey("Success"))
                                {
                                    bool success = innerObj["Success"]?.GetValue<bool>() ?? false;

                                    if (success)
                                    {
                                        if (innerObj.ContainsKey("Result"))
                                        {
                                            var resultVal = innerObj["Result"];
                                            var options = new JsonSerializerOptions
                                            {
                                                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                                                WriteIndented = false
                                            };

                                            string flattened;
                                            if (resultVal is JsonValue)
                                            {
                                                flattened = resultVal.ToString();
                                            }
                                            else
                                            {
                                                flattened = resultVal?.ToJsonString(options) ?? "";
                                            }

                                            contentItem["text"] = flattened;
                                        }
                                    }
                                    else
                                    {
                                        string msg = innerObj["Message"]?.GetValue<string>() ?? "Unknown Error";
                                        contentItem["text"] = $"Error: {msg}";
                                        result["isError"] = true;
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
        }
    }
}
