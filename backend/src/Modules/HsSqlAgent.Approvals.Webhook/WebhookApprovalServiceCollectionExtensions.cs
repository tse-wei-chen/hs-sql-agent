using Microsoft.Extensions.DependencyInjection;

namespace HsSqlAgent.Approvals.Webhook;

public static class WebhookApprovalServiceCollectionExtensions
{
    public static IServiceCollection AddHsSqlAgentWebhookApproval(
        this IServiceCollection services,
        Action<WebhookApprovalOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IDmlApprovalProvider)))
            throw new InvalidOperationException("A DML approval provider is already registered for this host.");

        services.Configure(configure);
        services.AddHttpClient<WebhookDmlApprovalProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            });
        services.AddTransient<IDmlApprovalProvider>(sp => sp.GetRequiredService<WebhookDmlApprovalProvider>());
        return services;
    }

    internal static void ValidateOptions(WebhookApprovalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateHttpUri(options.Endpoint, nameof(options.Endpoint), options.RequireHttps);
        ValidateHttpUri(options.CallbackUrl, nameof(options.CallbackUrl), options.RequireHttps);
        if (string.IsNullOrWhiteSpace(options.SigningSecret)
            || System.Text.Encoding.UTF8.GetByteCount(options.SigningSecret) < 32)
            throw new InvalidOperationException("Webhook approval SigningSecret must contain at least 32 UTF-8 bytes.");
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Webhook approval RequestTimeout must be greater than zero and at most two minutes.");
        if (options.CallbackTimestampTolerance <= TimeSpan.Zero || options.CallbackTimestampTolerance > TimeSpan.FromHours(1))
            throw new InvalidOperationException("Webhook callback timestamp tolerance must be greater than zero and at most one hour.");
        if (options.MaxCallbackBodyBytes is < 1024 or > 1024 * 1024)
            throw new InvalidOperationException("Webhook callback body limit must be between 1 KiB and 1 MiB.");
    }

    private static void ValidateHttpUri(Uri? uri, string name, bool requireHttps)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            throw new InvalidOperationException($"Webhook approval {name} must be an absolute URI.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Webhook approval {name} must use HTTP or HTTPS.");
        if (requireHttps && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Webhook approval {name} must use HTTPS unless RequireHttps is explicitly disabled.");
    }
}
