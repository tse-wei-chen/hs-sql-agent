using System.Text;
using System.Text.Json;
using HsSqlAgent.Approvals;
using ModelContextProtocol.Protocol;
using static ModelContextProtocol.Protocol.ElicitRequestParams;

namespace HsSqlAgent.Server.Tools;

internal static class DmlApprovalProviderResolver
{
    internal static IDmlApprovalProvider? Resolve(
        IDmlApprovalProvider? configuredProvider,
        IDmlApprovalClient? elicitationClient)
    {
        if (configuredProvider is not null) return configuredProvider;
        return elicitationClient?.SupportsElicitation == true
            ? new ElicitationDmlApprovalProvider(elicitationClient)
            : null;
    }
}

/// <summary>
/// Backward-compatible built-in approval provider. It is selected only when the host has not
/// registered an IDmlApprovalProvider, preserving MCP Elicitation as the default behavior.
/// </summary>
internal sealed class ElicitationDmlApprovalProvider(IDmlApprovalClient client) : IDmlApprovalProvider
{
    public async ValueTask<DmlApprovalResult> RequestApprovalAsync(
        DmlApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!client.SupportsElicitation)
        {
            return DmlApprovalResult.Reject(
                request,
                "Error: This MCP client does not support the interactive confirmation required for DML execution.");
        }

        var result = await client.ElicitAsync(new ElicitRequestParams
        {
            Message = BuildMessage(request),
            RequestedSchema = new RequestSchema
            {
                Properties =
                {
                    ["approve"] = new BooleanSchema
                    {
                        Title = request.IsTransaction
                            ? "Approve atomic transaction"
                            : "Approve execution",
                        Description = request.IsTransaction
                            ? $"Commit all {request.Statements.Count} statement(s) affecting {request.TotalAffectedRows} approved row(s), or commit none."
                            : $"This will **{request.Statements[0].Operation} {request.TotalAffectedRows} row(s)** in `{request.Statements[0].TableName}`."
                    }
                }
            }
        }, cancellationToken);

        if (result.Action == "accept"
            && result.Content?.TryGetValue("approve", out var approveElement) == true
            && approveElement.ValueKind == JsonValueKind.True)
        {
            return DmlApprovalResult.Approve(
                request,
                approverIdentity: request.RequesterIdentity);
        }

        return DmlApprovalResult.Reject(
            request,
            request.IsTransaction
                ? "DML transaction execution cancelled by user."
                : "DML execution cancelled by user.",
            approverIdentity: request.RequesterIdentity);
    }

    private static string BuildMessage(DmlApprovalRequest request)
    {
        if (!request.IsTransaction)
        {
            var statement = request.Statements[0];
            return
                $"## {request.Title}\n\n" +
                $"**{statement.Operation} on `{statement.TableName}` — {statement.AffectedRows} row(s) affected**\n\n" +
                $"### Impact preview\n\n{statement.PreviewJson}";
        }

        var message = new StringBuilder()
            .Append("## ").Append(request.Title).Append("\n\n")
            .Append("**Atomic transaction — ").Append(request.Statements.Count)
            .Append(" statement(s), ").Append(request.TotalAffectedRows)
            .Append(" total affected row(s)**\n\n")
            .Append("All statements commit together. Any revalidation or execution failure rolls back the entire transaction.\n");

        foreach (var statement in request.Statements)
        {
            message.Append("\n### ").Append(statement.Index).Append(". ")
                .Append(statement.Operation)
                .Append(" on `").Append(statement.TableName).Append("` — ")
                .Append(statement.AffectedRows).Append(" row(s)\n\n")
                .Append(statement.PreviewJson).Append("\n");
        }

        return message.ToString();
    }
}
