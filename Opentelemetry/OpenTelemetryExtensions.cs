using CFX.OpenTelemetry.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace CFX.OpenTelemetry
{
    public static class OpenTelemetryExtensions
    {
        private const string SAMPLER_ALWAYS_ON = "AlwaysOn";
        private const string SAMPLER_ALWAYS_OFF = "AlwaysOff";
        private const string SAMPLER_TRACE_ID_RATIO_BASED = "TraceIdRatioBased";

        public static string OpenTelemetrySettingsKey => "OpenTelemetry";

        public static ILoggingBuilder ConfigureOpenTelemetryLogging(this ILoggingBuilder logBuilder, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(logBuilder);
            ArgumentNullException.ThrowIfNull(configuration);

            logBuilder.ClearProviders();

            OpenTelemetrySettings otelSettings = GetOpenTelemetrySettings(configuration);

            logBuilder.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.ParseStateValues = true;

                ResourceBuilder resourceBuilder = CreateResourceBuilder(otelSettings);
                logging.SetResourceBuilder(resourceBuilder);

                //logging.AddConsoleExporter();

                if (!string.IsNullOrWhiteSpace(otelSettings.OtelExporterOtlpEndpoint))
                {
                    logging.AddOtlpExporter(opt =>
                    {
                        opt.Endpoint = new Uri(otelSettings.OtelExporterOtlpEndpoint);
                        opt.Headers = otelSettings.OtelExporterOtlpHeaders;
                        opt.Protocol = OtlpExportProtocol.Grpc;
                    });
                }
            });

            return logBuilder;
        }

        public static string GetApplicationNamespace(string applicationName)
        {
            string[] chunks = applicationName.Split(".");
            if (chunks.Length < 2)
            {
                return applicationName;
            }
            return string.Join(".", chunks.AsEnumerable().Skip(1));
        }

        public static IServiceCollection AddApplicationOpenTelemetry(this IServiceCollection services,
                                                                     IConfiguration configuration,
                                                                     bool explicitLoggingRegistration = false,
                                                                     string? instanceIdKey = null,
                                                                     Action<MeterProviderBuilder>? configureMeterProviderAction = null,
                                                                     params string[] additionalActivitySources)
        {
            ArgumentNullException.ThrowIfNull(configuration, nameof(configuration));
            OpenTelemetrySettings options = GetOpenTelemetrySettings(configuration);

                IReadOnlyList<string> allActivitySources = options.Instrumentation.ActivitySources
                  .Union(additionalActivitySources)
                  .ToList();


            bool instrumentHttpClient = options.Instrumentation.HttpClientEnabled;
            bool instrumentAspNetCore = options.Instrumentation.AspNetCoreEnabled;
            bool instrumentRedis = options.Instrumentation.RedisEnabled;
            bool instrumentTickerQ = options.Instrumentation.TickerQEnabled;

            AssemblyName? assemblyName = Assembly.GetEntryAssembly()?.GetName();

            string applicationName = options.OtelServiceName ?? throw new ArgumentNullException(nameof(options.OtelServiceName));
            string version = assemblyName?.Version?.ToString() ?? "unknown";
            string applicationNamespace = GetApplicationNamespace(applicationName);
            string instanceId = GetInstanceId(configuration, instanceIdKey);
            Sampler? selectedSampler = GetSampler(configuration);

            services.Configure<OpenTelemetrySettings>(configuration.GetSection(OpenTelemetrySettingsKey));

            OpenTelemetryBuilder builder = services.AddOpenTelemetry();
            builder.ConfigureResource(resourceBuilder =>
                    {
                        resourceBuilder.AddService(serviceName: applicationName!,
                                                   serviceNamespace: applicationNamespace,
                                                   serviceVersion: version,
                                                   serviceInstanceId: instanceId);
                    })
                   .WithTracing(builder =>
                    {
                        builder = builder.ConfigureResource((resourceBuilder) =>
                                                            {
                                                                resourceBuilder.AddService(serviceName: applicationName!,
                                                                                            serviceNamespace: applicationNamespace,
                                                                                            serviceVersion: version,
                                                                                            serviceInstanceId: instanceId,
                                                                                            autoGenerateServiceInstanceId: false);
                                                            });
                        if (selectedSampler != null)
                        {
                            builder.SetSampler(selectedSampler);
                        }
                        if (instrumentAspNetCore)
                        {
                            builder.AddAspNetCoreInstrumentation();
                        }
                        if (instrumentHttpClient)
                        {
                            builder.AddHttpClientInstrumentation();
                        }
                        if (instrumentRedis)
                        {
                            builder.AddRedisInstrumentation();
                        }
                        if (instrumentTickerQ)
                        {
                            builder.AddSource("TickerQ");
                        }
                        foreach (string source in allActivitySources)
                        {
                            builder.AddSource(source);
                        }
                        builder.AddNpgsql()
                               .AddEntityFrameworkCoreInstrumentation()
                               .AddOtlpExporter(opt =>
                               {
                                   opt.Endpoint = new Uri(options.OtelExporterOtlpEndpoint!);
                                   opt.Headers = options.OtelExporterOtlpHeaders;
                                   opt.Protocol = OtlpExportProtocol.Grpc;
                               });
                    })
                   .WithMetrics(builder =>
                    {
                        ResourceBuilder resourceBuilder = CreateResourceBuilder(options);
                        builder.SetResourceBuilder(resourceBuilder);
                        builder.ConfigureResource((resourceBuilder) =>
                                                  {
                                                      resourceBuilder.AddService(serviceName: applicationName!,
                                                                                 serviceNamespace: applicationNamespace,
                                                                                 serviceVersion: version,
                                                                                 serviceInstanceId: instanceId,
                                                                                 autoGenerateServiceInstanceId: false);
                                                  });

                        builder.AddRuntimeInstrumentation()
                               .AddNpgsqlInstrumentation();

                        if (instrumentAspNetCore)
                        {
                            builder.AddAspNetCoreInstrumentation();
                        }
                        if (instrumentHttpClient)
                        {
                            builder.AddHttpClientInstrumentation();
                        }
                        if (configureMeterProviderAction != null)
                        {
                            configureMeterProviderAction.Invoke(builder);
                        }

                        builder.AddOtlpExporter(opt =>
                                                {
                                                    opt.Endpoint = new Uri(options.OtelExporterOtlpEndpoint!);
                                                    opt.Headers = options.OtelExporterOtlpHeaders;
                                                    opt.Protocol = OtlpExportProtocol.Grpc;
                                                });

                        builder.AddView(instrument =>
                                        {
                                            return instrument.GetType().GetGenericTypeDefinition() == typeof(Histogram<>) ? new Base2ExponentialBucketHistogramConfiguration() : null;
                                        });
                    });
            if (explicitLoggingRegistration)
            {
                builder.WithLogging(logging =>
                        {
                            logging.ConfigureResource((resourceBuilder) =>
                            {
                                resourceBuilder.AddService(serviceName: applicationName!,
                                                           serviceNamespace: applicationNamespace,
                                                           serviceVersion: version,
                                                           serviceInstanceId: instanceId,
                                                           autoGenerateServiceInstanceId: false);
                            })
                            .AddOtlpExporter(opt =>
                            {
                                opt.Endpoint = new Uri(options.OtelExporterOtlpEndpoint!);
                                opt.Headers = options.OtelExporterOtlpHeaders;
                                opt.Protocol = OtlpExportProtocol.Grpc;
                            });
                        });
            }

            return services;
        }

        private static OpenTelemetrySettings GetOpenTelemetrySettings(IConfiguration configuration)
        {
            OpenTelemetrySettings otelSettings = configuration.GetSection(OpenTelemetrySettingsKey).Get<OpenTelemetrySettings>() ?? throw new InvalidOperationException("OpenTelemetry configuration is missing");

            if (string.IsNullOrEmpty(otelSettings.OtelServiceName))
            {
                throw new InvalidOperationException("OpenTelemetry service name is not configured");
            }

            if (string.IsNullOrEmpty(otelSettings.OtelExporterOtlpEndpoint))
            {
                throw new InvalidOperationException("OpenTelemetry OTLP Endpoint is not configured");
            }

            return otelSettings;
        }

        private static string GetInstanceId(IConfiguration configuration, string? key)
        {
            string machineName = Environment.MachineName;
            string? instanceId = configuration.GetValueByKey(key);
            return string.IsNullOrEmpty(instanceId) ? machineName : $"{machineName}.{instanceId}";
        }

        private static Sampler? GetSampler(IConfiguration configuration)
        {
            OpenTelemetrySettings? otelSettings = configuration.GetSection(OpenTelemetrySettingsKey).Get<OpenTelemetrySettings>();

            if (otelSettings == null ||
                otelSettings.OtelSampler == null)
            {
                return null;
            }

            string? samplerName = otelSettings.OtelSampler?.OtelSamplerName;
            Sampler? sampler = samplerName switch
            {
                SAMPLER_ALWAYS_ON => new AlwaysOnSampler(),
                SAMPLER_ALWAYS_OFF => new AlwaysOffSampler(),
                SAMPLER_TRACE_ID_RATIO_BASED => new TraceIdRatioBasedSampler(otelSettings.OtelSampler?.OtelSamplerRatio ?? 1.0),
                _ => string.IsNullOrEmpty(samplerName) ? new AlwaysOnSampler() : throw new InvalidOperationException($"Sampler {samplerName} is unknown or unsupported.")
            };

            return sampler;
        }

        private static ResourceBuilder CreateResourceBuilder(OpenTelemetrySettings otelSettings)
        {
            ResourceBuilder resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(serviceName: otelSettings.OtelServiceName!);

            if (!string.IsNullOrWhiteSpace(otelSettings.OtelResourceAttributes))
            {
                Dictionary<string, object> attributes = otelSettings.OtelResourceAttributes!
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                    .ToDictionary(parts => parts[0], parts => (object)parts[1]);

                if (attributes.Any())
                {
                    resourceBuilder.AddAttributes(attributes);
                }
            }

            return resourceBuilder;
        }
    }
}
