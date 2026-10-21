using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public Task<JsonNode> PotNamed(string name) => Greenhaus.PotNamed(name);

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

/// <summary>Talks to one API of the mock and checks every response against that API's contract.</summary>
public sealed class ContractClient(HttpClient client, OpenApiContract contract)
{
    public OpenApiContract Contract { get; } = contract;

    public async Task<HttpResponseMessage> Call(string method, string template, object? route = null, object? body = null, string? query = null, Dictionary<string, string>? headers = null)
    {
        var path = template;
        if (route is not null)
        {
            foreach (var property in route.GetType().GetProperties())
                path = path.Replace("{" + property.Name + "}", Uri.EscapeDataString(property.GetValue(route)?.ToString() ?? ""));
        }

        var request = new HttpRequestMessage(new HttpMethod(method), Contract.Url(path) + (query is null ? "" : "?" + query));
        foreach (var (name, value) in headers ?? []) request.Headers.Add(name, value);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);
        await Contract.Verify(response, method, template);
        return response;
    }

    public async Task<JsonNode> Node(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    public async Task<JsonNode> PotNamed(string name)
    {
        var pots = await Node(await Call("GET", "/pots"));
        return pots.AsArray().First(p => p!["name"]!.GetValue<string>() == name)!;
    }

    public void AssertCovers()
    {
        var missing = Contract.Operations().Select(o => o.OperationId).Except(Contract.Covered).ToList();
        Xunit.Assert.True(missing.Count == 0, "Operations in the contract that the mock walk did not exercise: " + string.Join(", ", missing));
    }
}
