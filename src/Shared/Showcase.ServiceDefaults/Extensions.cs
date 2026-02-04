using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using StackExchange.Redis.Configuration;

namespace Showcase.ServiceDefaults;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder, Action<ResourceBuilder>? configureOtel = null) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry(configureOtel);

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder, Action<ResourceBuilder>? configureOtel = null) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddMeter("*") // Our custom meter
                    .AddMeter("*Microsoft.Agents.AI") // Agent Framework metrics
                    
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource("*")
                    .AddSource("*Microsoft.Agents.AI")
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Don't trace requests to the health endpoint to avoid filling the dashboard with noise
                        tracing.Filter = httpContext =>
                            !(httpContext.Request.Path.StartsWithSegments(HealthEndpointPath)
                              || httpContext.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                    )
                    .AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters(configureOtel);

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder, Action<ResourceBuilder>? configureOtel = null) where TBuilder : IHostApplicationBuilder
    {

        var otelBuilder = builder.Services.AddOpenTelemetry();
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otelBuilder.UseOtlpExporter();
        }
        otelBuilder.UseAzureMonitor();
        if (configureOtel != null)
        {
            otelBuilder.ConfigureResource(configureOtel);
        }
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    otelBuilder.UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddRequestTimeouts(
            configure: static timeouts =>
            timeouts.AddPolicy("HealthChecks", TimeSpan.FromSeconds(5)));

        builder.Services.AddOutputCache(
            configureOptions: static caching =>
                caching.AddPolicy("HealthChecks",
                build: static policy => policy.Expire(TimeSpan.FromSeconds(10))));

        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/dotnet/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }
        else
        {
            var healthChecks = app.MapGroup("");
            healthChecks
                .CacheOutput("HealthChecks")
                .WithRequestTimeout("HealthChecks");
            // All health checks must pass for app to be
            // considered ready to accept traffic after starting
            healthChecks.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag
            // must pass for app to be considered alive
            healthChecks.MapHealthChecks(AlivenessEndpointPath, new()
            {
                Predicate = static r => r.Tags.Contains("live")
            });
        }

        return app;
    }

    /// <summary>
    /// Azure Managed Redis requires additional configuration to work with Azure Identity.
    /// </summary>
    /// <typeparam name="TBuilder"></typeparam>
    /// <param name="builder"></param>
    /// <param name="connectionName"></param>
    /// <param name="addDistributedCache"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public async static Task AddAzureRedisClient<TBuilder>(this TBuilder builder, string connectionName, bool addDistributedCache = true) where TBuilder : IHostApplicationBuilder
    {
        if (builder.Configuration.GetConnectionString(connectionName) is not string connectionString) 
        {
            throw new ArgumentException($"Connection string '{connectionName}' not found in configuration.");
        }
        var azureOptionsProvider = new AzureOptionsProvider();

        var configurationOptions = ConfigurationOptions.Parse(connectionString);

        if (configurationOptions.EndPoints.Any(azureOptionsProvider.IsMatch))
        {
            await configurationOptions.ConfigureForAzureWithTokenCredentialAsync(new DefaultAzureCredential());
        }

        builder.AddRedisClient(connectionName, configureOptions: options =>
        {
            options.Defaults = configurationOptions.Defaults;
        });


        if(addDistributedCache)
        {
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.ConfigurationOptions = configurationOptions;
            });

            builder.Services.AddOptions<RedisCacheOptions>().Configure<IServiceProvider>((opt, sp) =>
            {
                opt.ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>());
            });
        }
    }


}
