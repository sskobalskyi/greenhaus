using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Greenhaus.ContractTesting;

/// <summary>Talks to one API and checks every response against that API's OpenAPI contract.</summary>
public sealed class ContractClient(HttpClient client, OpenApiContract contract)
{
    public OpenApiContract Contract { get; } = contract;
    public HttpClient Http { get; } = client;

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

    public void AssertCovers(params string[] exceptOperations)
    {
        var missing = Contract.Operations().Select(o => o.OperationId).Except(Contract.Covered).Except(exceptOperations).ToList();
        Xunit.Assert.True(missing.Count == 0, "Operations in the contract that the walk did not exercise: " + string.Join(", ", missing));
    }

    public static Dictionary<string, string> Headers(params (string Name, string? Value)[] pairs) =>
        pairs.Where(p => p.Value is not null).ToDictionary(p => p.Name, p => p.Value!);
}