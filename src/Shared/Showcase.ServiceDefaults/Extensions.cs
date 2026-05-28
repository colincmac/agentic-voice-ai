using Azure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenTelemetry;
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

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

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

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.UseMicrosoftOpenTelemetry(opt => opt.Exporters = ExportTarget.AzureMonitor | ExportTarget.Console);

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
