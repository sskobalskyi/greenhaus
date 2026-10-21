using System.Text.Json;
using System.Text.Json.Serialization;
using Greenhaus.Api.Auth;
using Greenhaus.Api.Features;
using Greenhaus.Api.Infrastructure;
using Greenhaus.Api.Persistence;
using Greenhaus.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Greenhaus.Api;

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
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        });

        builder.Services.AddDbContext<GreenhausDbContext>(o => o
            .UseNpgsql(configuration.GetConnectionString("Greenhaus"), n => n.MigrationsHistoryTable("__migrations", GreenhausDbContext.Schema)));

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
                    o.MapInboundClaims = false;
                });
        }
        builder.Services.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        builder.Services.Configure<EntitlementOptions>(configuration.GetSection("Entitlements"));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_ => SpeciesCatalog.Load(configuration["Species:Path"] ?? "data/species.json"));
        builder.Services.AddScoped<EntitlementService>();
        builder.Services.AddScoped<EventPublisher>();
        builder.Services.AddSingleton<IReadingSource, NoReadings>();
        builder.Services.AddSingleton<ICommandSender, NoCommands>();
        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

        var otlp = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrEmpty(otlp))
        {
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService("greenhaus-api"))
                .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
                .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddOtlpExporter());
        }

        var app = builder.Build();

        if (configuration.GetValue("Database:MigrateOnStartup", true))
        {
            using var scope = app.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<GreenhausDbContext>().Database.Migrate();
        }

        if (string.Equals(auth.Mode, "Development", StringComparison.OrdinalIgnoreCase))
            app.Logger.LogWarning("Auth:Mode is Development. Callers are trusted by the X-User-Id header. Never use this outside a local machine.");

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health").AllowAnonymous();

        var api = app.MapGroup("/api/greenhaus/v1");
        api.MapAccount();
        api.MapPots();
        api.MapRequirements();
        api.MapControls();
        api.MapCare();

        return app;
    }
}