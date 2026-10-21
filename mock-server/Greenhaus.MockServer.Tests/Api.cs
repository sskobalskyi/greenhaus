using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Greenhaus.MockServer.Tests;

/// <summary>A running mock with a contract checker. Every call is verified against the OpenAPI document.</summary>
public sealed class Api : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public OpenApiContract Contract { get; }

    public Api()
    {
        var root = FindRepositoryRoot();
        Contract = new OpenApiContract(Path.Combine(root, "contracts", "openapi", "greenhaus-api.yaml"));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Species:Path", Path.Combine(root, "data", "species.json")));
        _client = _factory.CreateClient();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts", "openapi", "greenhaus-api.yaml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    /// <summary>Calls an operation by its path template, fills the route values and checks the response.</summary>
    public async Task<HttpResponseMessage> Call(string method, string template, object? route = null, object? body = null, string? tier = null, string? query = null, string? scenario = null)
    {
        var path = template;
        if (route is not null)
        {
            foreach (var property in route.GetType().GetProperties())
                path = path.Replace("{" + property.Name + "}", Uri.EscapeDataString(property.GetValue(route)?.ToString() ?? ""));
        }

        var request = new HttpRequestMessage(new HttpMethod(method), Contract.Url(path) + (query is null ? "" : "?" + query));
        if (tier is not null) request.Headers.Add("X-Mock-Tier", tier);
        if (scenario is not null) request.Headers.Add("X-Mock-Scenario", scenario);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);
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

    public static string Id(JsonNode node) => node["id"]!.GetValue<string>();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
