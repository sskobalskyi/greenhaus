using System.Text.Json;
using System.Text.Json.Serialization;
using Greenhaus.Billing.Auth;
using Greenhaus.Billing.Features;
using Greenhaus.Billing.Infrastructure;
using Greenhaus.Billing.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Greenhaus.Billing;

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

        builder.Services.AddDbContext<BillingDbContext>(o => o
            .UseNpgsql(configuration.GetConnectionString("Billing"), n => n.MigrationsHistoryTable("__migrations", BillingDbContext.Schema)));

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

        builder.Services.Configure<BillingOptions>(configuration.GetSection("Billing"));
        builder.Services.TryAddSingletonClock();
        builder.Services.AddScoped<BillingProcessor>();
        if (configuration.GetValue("Billing:RunTicker", true)) builder.Services.AddHostedService<BillingTicker>();

        // The browser app calls billing directly in local development. Tokens travel in a header, not cookies.
        var origins = configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173", "http://localhost:5174", "http://localhost:4173"];
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

        var otlp = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrEmpty(otlp))
        {
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService("greenhaus-billing"))
                .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource("Npgsql").AddOtlpExporter())
                .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddOtlpExporter());
        }

        var app = builder.Build();

        if (configuration.GetValue("Database:MigrateOnStartup", true))
        {
            using var scope = app.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.Migrate();
        }

        if (string.Equals(auth.Mode, "Development", StringComparison.OrdinalIgnoreCase))
            app.Logger.LogWarning("Auth:Mode is Development. Callers are trusted by the X-User-Id header. Never use this outside a local machine.");

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health").AllowAnonymous();

        app.MapGroup("/api/billing/v1").MapBilling();

        return app;
    }
}

public static class ClockRegistration
{
    /// <summary>The system clock, unless a test already registered its own.</summary>
    public static IServiceCollection TryAddSingletonClock(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}