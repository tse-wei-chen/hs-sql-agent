using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentTelemetryServiceExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentTelemetry(
        this HsSqlAgentRegistrationBuilder builder,
        Action<TelemetryOptions>? configure = null)
    {
        builder.AddHsSqlAgentRuntime();
        builder.ThrowIfAlreadyConfigured("telemetry", configure);
        if (builder.IsRegistered("telemetry")) return builder;

        var options = builder.GetOrCreateOptions(() => builder.LegacyOptions is { } legacy
            ? TelemetryOptions.FromLegacy(legacy)
            : new TelemetryOptions());
        configure?.Invoke(options);
        if (!builder.TryRegister("telemetry")) return builder;

        var services = builder.Services;
        if (options.PrometheusEnabled && options.PrometheusPort is < 1 or > 65535)
            throw new InvalidOperationException("Telemetry PrometheusPort must be between 1 and 65535.");
        if (options.PrometheusEnabled && string.IsNullOrWhiteSpace(options.PrometheusHost))
            throw new InvalidOperationException("Telemetry PrometheusHost is required when Prometheus is enabled.");
        if (string.IsNullOrWhiteSpace(options.ServiceName))
            throw new InvalidOperationException("Telemetry ServiceName is required.");
        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint) &&
            (!Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var otlpUri) ||
             otlpUri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Telemetry OtlpEndpoint must be an absolute HTTP or HTTPS URL.");

        services.Configure<TelemetryOptions>(telemetry =>
        {
            telemetry.PrometheusEnabled = options.PrometheusEnabled;
            telemetry.PrometheusHost = options.PrometheusHost;
            telemetry.PrometheusPort = options.PrometheusPort;
            telemetry.OtlpEndpoint = options.OtlpEndpoint;
            telemetry.ServiceName = options.ServiceName;
        });

        if (options.PrometheusEnabled || !string.IsNullOrWhiteSpace(options.OtlpEndpoint))
        {
            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(options.ServiceName))
                .WithMetrics(metrics =>
                {
                    metrics
                        .AddMeter(HsSqlAgentMetrics.MeterName)
                        .AddMeter("Microsoft.AspNetCore.Hosting")
                        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                        .AddMeter("System.Net.Http")
                        .AddMeter("System.Net.NameResolution")
                        .AddView("hsqlagent.mcp.request.duration", new ExplicitBucketHistogramConfiguration
                        {
                            Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60]
                        })
                        .AddView("hsqlagent.sql.execution.duration", new ExplicitBucketHistogramConfiguration
                        {
                            Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60]
                        })
                        .AddView("hsqlagent.sql.rows.returned", new ExplicitBucketHistogramConfiguration
                        {
                            Boundaries = [0, 1, 10, 100, 1_000, 10_000, 100_000]
                        })
                        .AddView("hsqlagent.sql.rows.affected", new ExplicitBucketHistogramConfiguration
                        {
                            Boundaries = [0, 1, 10, 100, 1_000, 10_000, 100_000]
                        });
                    if (options.PrometheusEnabled)
                    {
                        metrics.AddPrometheusHttpListener(exporter =>
                        {
                            exporter.Host = options.PrometheusHost;
                            exporter.Port = options.PrometheusPort;
                            exporter.ScrapeEndpointPath = "/metrics";
                            exporter.ScopeInfoEnabled = false;
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                        metrics.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
                });
        }

        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
        {
            var otlpEndpoint = new Uri(options.OtlpEndpoint);
            services.AddLogging(logging => logging.AddOpenTelemetry(logs =>
            {
                logs.IncludeScopes = true;
                logs.IncludeFormattedMessage = false;
                logs.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(options.ServiceName));
                logs.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
            }));
            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(options.ServiceName))
                .WithTracing(tracing => tracing
                    .AddSource(SqlCompileEvidenceObserver.ActivitySourceName)
                    .AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint));
        }

        return builder;
    }
}
