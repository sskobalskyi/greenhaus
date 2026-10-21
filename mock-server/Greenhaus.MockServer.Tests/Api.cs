using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Greenhaus.ContractTesting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Greenhaus.MockServer.Tests;

/// <summary>A running mock with a contract checker per API. Every call is verified against its OpenAPI document.</summary>
public sealed class Api : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ContractClient Greenhaus { get; }
    public ContractClient Billing { get; }
    public ContractClient Pulse { get; }

    /// <summary>The Greenhaus contract, kept as the default so the first tests read as before.</summary>
    public OpenApiContract Contract => Greenhaus.Contract;

    public Api()
    {
        var root = FindRepositoryRoot();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Species:Path", Path.Combine(root, "data", "species.json")));
        _client = _factory.CreateClient();

        ContractClient Open(string file) => new(_client, new OpenApiContract(Path.Combine(root, "contracts", "openapi", file)));
        Greenhaus = Open("greenhaus-api.yaml");
        Billing = Open("billing-api.yaml");
        Pulse = Open("pulse-device-api.yaml");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts", "openapi", "greenhaus-api.yaml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    public Task<HttpResponseMessage> Call(string method, string template, object? route = null, object? body = null, string? tier = null, string? query = null, string? scenario = null) =>
        Greenhaus.Call(method, template, route, body, query, Headers(("X-Mock-Tier", tier), ("X-Mock-Scenario", scenario)));

    public Task<JsonNode> Node(HttpResponseMessage response) => Greenhaus.Node(response);

    public async Task<JsonNode> PotNamed(string name)
    {
        var pots = await Greenhaus.Node(await Greenhaus.Call("GET", "/pots"));
        return pots.AsArray().First(p => p!["name"]!.GetValue<string>() == name)!;
    }

    public static string Id(JsonNode node) => node["id"]!.GetValue<string>();

    /// <summary>Calls a mock-only control (not part of any contract), for example to move the clock.</summary>
    public async Task<HttpResponseMessage> Mock(string method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }

    /// <summary>Moves the mock clock forward.</summary>
    public async Task Advance(double days) => (await Mock("POST", "/mock/clock/advance", new { days })).EnsureSuccessStatusCode();

    public static Dictionary<string, string> Headers(params (string Name, string? Value)[] pairs) =>
        pairs.Where(p => p.Value is not null).ToDictionary(p => p.Name, p => p.Value!);

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
