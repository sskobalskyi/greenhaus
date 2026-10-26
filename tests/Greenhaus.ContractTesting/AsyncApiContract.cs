using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace Greenhaus.ContractTesting;

/// <summary>Checks integration events against an AsyncAPI document: the payload against the message schema, and the headers every event carries.</summary>
public sealed class AsyncApiContract
{
    private const string BaseUri = "https://contract.test/asyncapi";
    private const string SchemaPrefix = "#/components/schemas/";
    private readonly JsonObject _document;
    private readonly SchemaRegistry _registry = new();
    private readonly BuildOptions _options;

    public AsyncApiContract(string yamlPath)
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(yamlPath)) yaml.Load(reader);
        _document = (JsonObject)OpenApiContract.ToJson(yaml.Documents[0].RootNode)!;
        _options = new BuildOptions { SchemaRegistry = _registry };

        var definitions = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = BaseUri,
            // The device topics document has messages but no shared schemas, so the definitions may be absent.
            ["$defs"] = _document["components"]!["schemas"]?.DeepClone() ?? new JsonObject(),
        };
        JsonSchema.FromText(Rewrite(definitions).ToJsonString(), _options, new Uri(BaseUri));
    }

    /// <summary>The name of the message published on a routing key, for example order.confirmed.v1.</summary>
    public bool Knows(string routingKey) => Message(routingKey) is not null;

    public void VerifyPayload(string routingKey, string json)
    {
        var message = Message(routingKey) ?? throw new Xunit.Sdk.XunitException($"{routingKey} is not in the contract");
        Validate(routingKey, message["payload"]!, json);
    }

    public void VerifyHeaders(string routingKey, IDictionary<string, string?> headers)
    {
        var node = new JsonObject();
        foreach (var (key, value) in headers) if (value is not null) node[key] = value;
        Validate(routingKey + " headers", _document["components"]!["schemas"]!["Headers"]!, node.ToJsonString());
    }

    private JsonNode? Message(string routingKey) =>
        _document["components"]!["messages"]!.AsObject().Select(m => m.Value!).FirstOrDefault(m => m["name"]?.GetValue<string>() == routingKey);

    private void Validate(string what, JsonNode schemaNode, string json)
    {
        var schema = JsonSchema.FromText(Rewrite(schemaNode.DeepClone()).ToJsonString(), _options);
        using var instance = JsonDocument.Parse(json);
        var result = schema.Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (result.IsValid) return;
        var errors = result.Details.Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")).Distinct();
        throw new Xunit.Sdk.XunitException($"{what} does not match the contract:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}{Environment.NewLine}Body: {json}");
    }

    private static JsonNode Rewrite(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (property.Key == "$ref" && property.Value is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith(SchemaPrefix, StringComparison.Ordinal))
                        obj[property.Key] = BaseUri + "#/$defs/" + text[SchemaPrefix.Length..];
                    else if (property.Value is not null) Rewrite(property.Value);
                }
                break;
            case JsonArray array:
                foreach (var item in array) if (item is not null) Rewrite(item);
                break;
        }
        return node;
    }
}