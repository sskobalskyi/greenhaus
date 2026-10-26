using System.Text.Json;
using System.Text.Json.Serialization;
using Greenhaus.Fulfilment.Auth;
using Greenhaus.Fulfilment.Domain;
using Greenhaus.Fulfilment.Features;
using Greenhaus.Fulfilment.Infrastructure;
using Greenhaus.Fulfilment.Messaging;
using Greenhaus.Fulfilment.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Greenhaus.Fulfilment;

public partial class Program
{
    public static void Main(string[] args)
    {
        var app = Build(args);
        app.Run();
    }

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var configuration = builder.Configuration;

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        builder.Services.AddDbContext<FulfilmentDbContext>(o => o
            .UseNpgsql(configuration.GetConnectionString("Fulfilment"), n => n.MigrationsHistoryTable("__migrations", FulfilmentDbContext.Schema)));

        builder.Services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        var auth = configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        if (string.Equals(auth.Mode, "Development", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddAuthentication(DevelopmentAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(DevelopmentAuthHandler.SchemeName, _ => { });
        }
        else
        {
            builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(o =>
                {
                    o.Authority = auth.Authority;
                    o.RequireHttpsMetadata = auth.RequireHttpsMetadata;
                    o.TokenValidationParameters.ValidateAudience = auth.Audience is not null;
                    o.TokenValidationParameters.ValidAudience = auth.Audience;
                    if (!string.IsNullOrEmpty(auth.Issuer)) o.TokenValidationParameters.ValidIssuer = auth.Issuer;
                    o.MapInboundClaims = false;
                });
        }
        builder.Services.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        builder.Services.Configure<FulfilmentOptions>(configuration.GetSection("Fulfilment"));
        builder.Services.Configure<WebOptions>(configuration.GetSection("Web"));
        builder.Services.Configure<PulseOptions>(configuration.GetSection("Pulse"));
        builder.Services.Configure<RabbitOptions>(configuration.GetSection("RabbitMq"));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_ => KitCatalog.Load(configuration["Fulfilment:KitsPath"] ?? "data/kit-capabilities.json"));
        builder.Services.AddScoped<EventPublisher>();
        // Packing secrets are sealed before they reach the database. Outside development the service does not start without a key.
        var secretBox = SecretBox.Create(configuration["Fulfilment:SecretsKey"], string.Equals(auth.Mode, "Development", StringComparison.OrdinalIgnoreCase));
        builder.Services.AddSingleton(secretBox);

        builder.Services.AddSingleton<PulseTokens>();
        builder.Services.AddTransient<PulseTokenHandler>();
        builder.Services.AddHttpClient("pulse-token");
        builder.Services.AddHttpClient<IPulseDevices, HttpPulseDevices>(client =>
            {
                var baseUrl = configuration["Pulse:BaseUrl"] ?? "http://pulse.invalid/";
                client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .AddHttpMessageHandler<PulseTokenHandler>();

        // The worker is also a singleton so tests can run a pass on demand. The timer can be turned off for them.
        builder.Services.AddSingleton<PulseSyncWorker>();
        if (configuration.GetValue("Fulfilment:RunSyncWorker", true)) builder.Services.AddHostedService(sp => sp.GetRequiredService<PulseSyncWorker>());
        if (!string.IsNullOrEmpty(configuration["RabbitMq:Host"]))
        {
            builder.Services.AddHostedService<OutboxRelay>();
            builder.Services.AddHostedService<EventConsumer>();
        }

        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

        var otlp = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrEmpty(otlp))
        {
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService("greenhaus-fulfilment"))
                .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource("Npgsql", FulfilmentTelemetry.Name).AddOtlpExporter())
                .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddMeter(FulfilmentTelemetry.Name).AddOtlpExporter());
        }

        var app = builder.Build();

        if (configuration.GetValue("Database:MigrateOnStartup", true))
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FulfilmentDbContext>();
            db.Database.Migrate();
            var sealedLegacy = SecretBox.SealLegacyAsync(db, secretBox).GetAwaiter().GetResult();
            if (sealedLegacy > 0) app.Logger.LogWarning("Sealed the packing secrets of {Count} allocation(s) that an earlier version stored in the clear", sealedLegacy);
        }
        if (secretBox.Ephemeral)
            app.Logger.LogWarning("Fulfilment:SecretsKey is not set. A key was made up for this run, so packing secrets sealed now cannot be read after a restart. Set the key.");

        if (string.Equals(auth.Mode, "Development", StringComparison.OrdinalIgnoreCase))
            app.Logger.LogWarning("Auth:Mode is Development. Callers are trusted by the X-User-Id header and are fulfilment admins unless X-Roles says otherwise. Never use this outside a local machine.");

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health").AllowAnonymous();

        app.MapGroup("/api/fulfilment/v1").MapFulfilment();

        return app;
    }
}