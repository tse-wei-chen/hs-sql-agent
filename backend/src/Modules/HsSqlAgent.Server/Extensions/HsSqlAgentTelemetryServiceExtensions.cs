using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentTelemetryServiceExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentTelemetry(this HsSqlAgentRegistrationBuilder builder)
    {
        builder.AddHsSqlAgentRuntime();
        if (!builder.TryRegister("telemetry")) return builder;

        var services = builder.Services;
        var options = builder.Options;
        if (options.Telemetry.PrometheusEnabled && options.Telemetry.PrometheusPort is < 1 or > 65535)
            throw new InvalidOperationException("Telemetry PrometheusPort must be between 1 and 65535.");
        if (options.Telemetry.PrometheusEnabled && string.IsNullOrWhiteSpace(options.Telemetry.PrometheusHost))
            throw new InvalidOperationException("Telemetry PrometheusHost is required when Prometheus is enabled.");
        if (string.IsNullOrWhiteSpace(options.Telemetry.ServiceName))
            throw new InvalidOperationException("Telemetry ServiceName is required.");
        if (!string.IsNullOrWhiteSpace(options.Telemetry.OtlpEndpoint) &&
            (!Uri.TryCreate(options.Telemetry.OtlpEndpoint, UriKind.Absolute, out var otlpUri) ||
             otlpUri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Telemetry OtlpEndpoint must be an absolute HTTP or HTTPS URL.");

        services.Configure<TelemetryOptions>(telemetry =>
        {
            telemetry.PrometheusEnabled = options.Telemetry.PrometheusEnabled;
            telemetry.PrometheusHost = options.Telemetry.PrometheusHost;
            telemetry.PrometheusPort = options.Telemetry.PrometheusPort;
            telemetry.OtlpEndpoint = options.Telemetry.OtlpEndpoint;
            telemetry.ServiceName = options.Telemetry.ServiceName;
        });

        if (options.Telemetry.PrometheusEnabled || !string.IsNullOrWhiteSpace(options.Telemetry.OtlpEndpoint))
        {
            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(options.Telemetry.ServiceName))
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
                    if (options.Telemetry.PrometheusEnabled)
                    {
                        metrics.AddPrometheusHttpListener(exporter =>
                        {
                            exporter.Host = options.Telemetry.PrometheusHost;
                            exporter.Port = options.Telemetry.PrometheusPort;
                            exporter.ScrapeEndpointPath = "/metrics";
                            exporter.ScopeInfoEnabled = false;
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(options.Telemetry.OtlpEndpoint))
                        metrics.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.Telemetry.OtlpEndpoint));
                });
        }

        if (!string.IsNullOrWhiteSpace(options.Telemetry.OtlpEndpoint))
        {
            var otlpEndpoint = new Uri(options.Telemetry.OtlpEndpoint);
            services.AddLogging(logging => logging.AddOpenTelemetry(logs =>
            {
                logs.IncludeScopes = true;
                logs.IncludeFormattedMessage = false;
                logs.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(options.Telemetry.ServiceName));
                logs.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint);
            }));
            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(options.Telemetry.ServiceName))
                .WithTracing(tracing => tracing
                    .AddSource(SqlCompileEvidenceObserver.ActivitySourceName)
                    .AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint));
        }

        return builder;
    }
}
