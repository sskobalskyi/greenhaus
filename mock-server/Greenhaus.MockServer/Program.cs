using System.Text.Json;
using System.Text.Json.Serialization;
using Greenhaus.MockServer;
using Greenhaus.MockServer.Billing;
using Greenhaus.MockServer.Pulse;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var speciesPath = builder.Configuration["Species:Path"]
    ?? Path.Combine(AppContext.BaseDirectory, "data", "species.json");
builder.Services.AddSingleton<MockClock>();
builder.Services.AddSingleton<BillingStore>();
builder.Services.AddSingleton<PulseStore>();
builder.Services.AddSingleton(sp => new MockStore(speciesPath, sp.GetRequiredService<BillingStore>()));

var app = builder.Build();
app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapGreenhausApi();
app.MapBillingApi();
app.MapPulseApi();

// Test support: reseed the in-memory state. Not part of the contract.
app.MapPost("/mock/reset", (MockStore store, BillingStore billing, PulseStore pulse) =>
{
    store.Reset();
    billing.Reset();
    pulse.Reset();
    return Results.NoContent();
});

// Start with the seeded state.
app.Services.GetRequiredService<BillingStore>().Reset();
app.Services.GetRequiredService<PulseStore>().Reset();

// Serve the contracts so clients and tools can fetch them from the running mock.
var contractsDirectory = builder.Configuration["Contracts:Path"] ?? Path.Combine(AppContext.BaseDirectory, "contracts");
if (Directory.Exists(contractsDirectory))
{
    app.MapGet("/contracts/{name}", (string name) =>
    {
        var file = Path.Combine(contractsDirectory, Path.GetFileName(name));
        return File.Exists(file) ? Results.File(file, "application/yaml") : Results.NotFound();
    });
}

app.Run();

public partial class Program;
