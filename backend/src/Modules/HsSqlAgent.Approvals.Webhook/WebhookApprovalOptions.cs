namespace HsSqlAgent.Approvals.Webhook;

public sealed class WebhookApprovalOptions
{
    public Uri? Endpoint { get; set; }
    public Uri? CallbackUrl { get; set; }
    public string SigningSecret { get; set; } = string.Empty;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan CallbackTimestampTolerance { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxCallbackBodyBytes { get; set; } = 64 * 1024;
    public bool RequireHttps { get; set; } = true;
}
