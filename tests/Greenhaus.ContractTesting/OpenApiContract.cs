using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Greenhaus.ContractTesting;

/// <summary>
/// Loads an OpenAPI 3.1 document and checks real HTTP responses against it: the status code must be one
/// the contract documents, and the body must match the documented schema. It also records which operations
/// were exercised, so a test can assert that the mock covers the whole contract.
/// </summary>
public sealed class OpenApiContract
{
    private const string BaseUri = "https://contract.test/openapi";
    private const string SchemaPrefix = "#/components/schemas/";
    private readonly JsonObject _document;
    private readonly SchemaRegistry _registry = new();
    private readonly BuildOptions _buildOptions;
    private readonly string _basePath;
    private readonly HashSet<string> _covered = [];

    public OpenApiContract(string yamlPath)
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(yamlPath)) yaml.Load(reader);
        _document = (JsonObject)ToJson(yaml.Documents[0].RootNode)!;

        _buildOptions = new BuildOptions { SchemaRegistry = _registry };
        // Register the component schemas as $defs of one document so '#/components/schemas/X' references resolve.
        var definitions = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = BaseUri,
            ["$defs"] = _document["components"]!["schemas"]!.DeepClone(),
        };
        JsonSchema.FromText(Rewrite(definitions).ToJsonString(), _buildOptions, new Uri(BaseUri));

        _basePath = new Uri(_document["servers"]![0]!["url"]!.GetValue<string>()).AbsolutePath.TrimEnd('/');
    }

    public IEnumerable<(string Method, string Path, string OperationId)> Operations() =>
        _document["paths"]!.AsObject().SelectMany(path =>
            path.Value!.AsObject()
                .Where(m => m.Key is "get" or "post" or "put" or "patch" or "delete")
                .Select(m => (m.Key.ToUpperInvariant(), path.Key, m.Value!["operationId"]!.GetValue<string>())));

    public IReadOnlyCollection<string> Covered => _covered;

    /// <summary>Asserts that the response is one the operation documents, and that its body fits the schema.</summary>
    public async Task Verify(HttpResponseMessage response, string method, string pathTemplate)
    {
        var operation = _document["paths"]![pathTemplate]?[method.ToLowerInvariant()]
            ?? throw new Xunit.Sdk.XunitException($"{method} {pathTemplate} is not in the contract");
        _covered.Add(operation["operationId"]!.GetValue<string>());

        var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var documented = operation["responses"]!.AsObject();
        var entry = documented[status] ?? documented["default"];
        // Authentication failures are described once, in the security scheme, not on every operation. A secured
        // operation may answer 401 or 403 with a problem document.
        if (entry is null && status is "401" or "403" && IsSecured(operation))
        {
            await VerifyProblem(response, method, pathTemplate, status);
            return;
        }
        if (entry is null)
        {
            var allowed = string.Join(", ", documented.Select(r => r.Key));
            throw new Xunit.Sdk.XunitException($"{method} {pathTemplate} returned {status}, the contract documents: {allowed}");
        }

        var resolved = Resolve(entry.AsObject());
        var content = resolved["content"]?.AsObject();
        var body = await response.Content.ReadAsStringAsync();
        if (content is null || content.Count == 0)
        {
            Xunit.Assert.True(string.IsNullOrEmpty(body), $"{method} {pathTemplate} {status} should have no body, got: {body}");
            return;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var media = content[mediaType]
            ?? throw new Xunit.Sdk.XunitException($"{method} {pathTemplate} {status} returned {mediaType}, the contract documents: {string.Join(", ", content.Select(c => c.Key))}");
        var schemaNode = media["schema"];
        if (schemaNode is null) return;

        var schema = JsonSchema.FromText(Rewrite(schemaNode.DeepClone()).ToJsonString(), _buildOptions);
        using var instance = JsonDocument.Parse(body);
        var result = schema.Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (!result.IsValid)
        {
            var errors = result.Details
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
                .Distinct();
            throw new Xunit.Sdk.XunitException(
                $"{method} {pathTemplate} {status} does not match the schema:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}{Environment.NewLine}Body: {body}");
        }
    }

    private bool IsSecured(JsonNode operation) =>
        (operation["security"] ?? _document["security"]) is JsonArray { Count: > 0 };

    private async Task VerifyProblem(HttpResponseMessage response, string method, string pathTemplate, string status)
    {
        var media = response.Content.Headers.ContentType?.MediaType;
        Xunit.Assert.True(media == "application/problem+json", $"{method} {pathTemplate} {status} should be a problem document, got {media}");
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Xunit.Assert.Equal(int.Parse(status), problem["status"]!.GetValue<int>());
        Xunit.Assert.False(string.IsNullOrEmpty(problem["code"]?.GetValue<string>()));
    }

    /// <summary>Joins the server base path of the contract to a path template, for building real URLs.</summary>
    public string Url(string path) => _basePath + path;

    // A response object may be a reference to #/components/responses/X.
    private JsonObject Resolve(JsonObject node)
    {
        if (node["$ref"] is JsonValue reference && reference.GetValue<string>() is { } pointer && pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            JsonNode? current = _document;
            foreach (var part in pointer[2..].Split('/')) current = current![part.Replace("~1", "/").Replace("~0", "~")];
            return current!.AsObject();
        }

        return node;
    }

    // Point '#/...' references at the registered document.
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

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(c => KeyValuePair.Create(((YamlScalarNode)c.Key).Value!, ToJson(c.Value)))),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToJson).ToArray()),
        YamlScalarNode scalar => ToScalar(scalar),
        _ => null,
    };

    // YAML plain scalars carry their type: true, 3, 1.5 and null are not strings, quoted values always are.
    private static JsonNode? ToScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(value);
        if (value is null or "" or "null" or "~") return null;
        if (value is "true") return JsonValue.Create(true);
        if (value is "false") return JsonValue.Create(false);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return JsonValue.Create(number);
        return JsonValue.Create(value);
    }
}
