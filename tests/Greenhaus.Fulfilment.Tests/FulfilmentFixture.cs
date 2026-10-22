using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Greenhaus.ContractTesting;
using Greenhaus.Fulfilment.Domain;
using Greenhaus.Fulfilment.Infrastructure;
using Greenhaus.Fulfilment.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Greenhaus.Fulfilment.Tests;

public sealed record Heard(string Key, string Body, Dictionary<string, string?> Headers);

/// <summary>
/// One real PostgreSQL, one real RabbitMQ and one running fulfilment service. A spy queue listens to what fulfilment publishes the way
/// another project would, and tests publish order events as Orderflow would. Pulse is a fake, and the clock is a fake so backoff can be skipped.
/// </summary>
public sealed class FulfilmentFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder().WithImage("rabbitmq:4-alpine").Build();
    private readonly ConcurrentQueue<Heard> _heard = new();
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private IConnection _connection = null!;
    private IChannel _channel = null!;

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public FakePulse Pulse { get; } = new();
    public OpenApiContract Contract { get; private set; } = null!;
    public AsyncApiContract Events { get; private set; } = null!;
    public ContractClient Client { get; private set; } = null!;
    public KitCatalog Kits { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        var root = FindRepositoryRoot();
        var kitsPath = Path.Combine(root, "data", "kit-capabilities.json");
        Kits = KitCatalog.Load(kitsPath);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Fulfilment", _postgres.GetConnectionString());
            b.UseSetting("Auth:Mode", "Development");
            b.UseSetting("RabbitMq:Host", _rabbit.Hostname);
            b.UseSetting("RabbitMq:Port", _rabbit.GetMappedPublicPort(5672).ToString());
            b.UseSetting("RabbitMq:User", RabbitMqBuilder.DefaultUsername);
            b.UseSetting("RabbitMq:Password", RabbitMqBuilder.DefaultPassword);
            b.UseSetting("Fulfilment:KitsPath", kitsPath);
            b.UseSetting("Web:BaseUrl", "http://localhost:5174");
            // Tests run the sync pass themselves, on a clock they move, so the timer would only get in the way.
            b.UseSetting("Fulfilment:RunSyncWorker", "false");
            b.UseSetting("Fulfilment:BaseBackoff", "00:00:10");
            b.UseSetting("Fulfilment:MaxAttempts", "3");
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<IPulseDevices>(Pulse);
            });
        });
        _client = _factory.CreateClient();
        Contract = new OpenApiContract(Path.Combine(root, "contracts", "openapi", "fulfilment-api.yaml"));
        Events = new AsyncApiContract(Path.Combine(root, "contracts", "asyncapi", "integration-events.yaml"));
        Client = new ContractClient(_client, Contract);

        _connection = await new ConnectionFactory { HostName = _rabbit.Hostname, Port = _rabbit.GetMappedPublicPort(5672), UserName = RabbitMqBuilder.DefaultUsername, Password = RabbitMqBuilder.DefaultPassword }.CreateConnectionAsync();
        _channel = await _connection.CreateChannelAsync();
        await _channel.ExchangeDeclareAsync("integration.events", ExchangeType.Topic, durable: true);
        var spy = (await _channel.QueueDeclareAsync()).QueueName;
        await _channel.QueueBindAsync(spy, "integration.events", "device.allocated.v1");
        var consumer = new RabbitMQ.Client.Events.AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, e) =>
        {
            var headers = (e.BasicProperties.Headers ?? new Dictionary<string, object?>())
                .ToDictionary(h => h.Key, h => (string?)(h.Value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : h.Value?.ToString()));
            _heard.Enqueue(new Heard(e.RoutingKey, Encoding.UTF8.GetString(e.Body.Span), headers));
            return Task.CompletedTask;
        };
        await _channel.BasicConsumeAsync(spy, autoAck: true, consumer);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    public async Task<T> WithDb<T>(Func<FulfilmentDbContext, Task<T>> action)
    {
        using var scope = _factory!.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<FulfilmentDbContext>());
    }

    /// <summary>One pass of the worker that tells Pulse about shipped and retired devices, at the fake clock's time.</summary>
    public Task<int> SyncPulse() => _factory!.Services.GetRequiredService<PulseSyncWorker>().SyncDueAsync(CancellationToken.None);

    /// <summary>Publishes an order event the way Orderflow does: raw JSON with the contract headers, after checking it against the contract.</summary>
    public async Task Publish(string routingKey, JsonObject payload, Guid? messageId = null, string? raw = null)
    {
        if (raw is null) Events.VerifyPayload(routingKey, payload.ToJsonString());
        var id = messageId ?? Guid.NewGuid();
        var occurred = DateTimeOffset.UtcNow.ToString("O");
        var properties = new BasicProperties
        {
            MessageId = id.ToString(), ContentType = "application/json", Persistent = true,
            Headers = new Dictionary<string, object?> { ["messageId"] = id.ToString(), ["occurredAt"] = occurred, ["schemaVersion"] = "1" },
        };
        await _channel.BasicPublishAsync("integration.events", routingKey, false, properties, Encoding.UTF8.GetBytes(raw ?? payload.ToJsonString()));
    }

    public Task Confirmed(Guid orderId, string customerId, params (string Sku, int Quantity)[] lines) =>
        Publish("order.confirmed.v1", new JsonObject
        {
            ["orderId"] = orderId.ToString(), ["customerId"] = customerId,
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject { ["sku"] = l.Sku, ["quantity"] = l.Quantity, ["unitPrice"] = 10 }).ToArray()),
            ["total"] = lines.Sum(l => l.Quantity * 10), ["currency"] = "USD", ["confirmedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        });

    public Task Shipped(Guid orderId, string customerId) =>
        Publish("order.shipped.v1", new JsonObject { ["orderId"] = orderId.ToString(), ["customerId"] = customerId, ["shippedAt"] = DateTimeOffset.UtcNow.ToString("O") });

    public Task Returned(Guid orderId, string customerId) =>
        Publish("order.returned.v1", new JsonObject
        {
            ["orderId"] = orderId.ToString(), ["customerId"] = customerId, ["returnedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["lines"] = new JsonArray(new JsonObject { ["sku"] = "GH-POT-KIT-01", ["quantity"] = 1, ["unitPrice"] = 80 }),
        });

    public Task Refunded(Guid orderId, string customerId) =>
        Publish("order.refunded.v1", new JsonObject
        {
            ["orderId"] = orderId.ToString(), ["customerId"] = customerId, ["amount"] = 80, ["currency"] = "USD", ["refundedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        });

    /// <summary>Everything fulfilment published as device.allocated.v1 so far.</summary>
    public IReadOnlyList<Heard> Allocated => _heard.ToArray();

    public async Task<T> Eventually<T>(Func<Task<T?>> probe, string what, int seconds = 20) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await probe() is { } found) return found;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for {what}");
    }

    public async Task<IReadOnlyList<AllocationEntity>> WaitForAllocations(Guid orderId, int count) =>
        await Eventually(async () =>
        {
            var rows = await WithDb(db => db.Allocations.AsNoTracking().Where(a => a.OrderId == orderId).OrderBy(a => a.Sku).ThenBy(a => a.Index).ToListAsync());
            return rows.Count >= count ? (IReadOnlyList<AllocationEntity>)rows : null;
        }, $"{count} allocations for order {orderId}");

    public async Task<IReadOnlyList<AllocationEntity>> WaitForState(Guid orderId, string state, int count) =>
        await Eventually(async () =>
        {
            var rows = await WithDb(db => db.Allocations.AsNoTracking().Where(a => a.OrderId == orderId && a.State == state).ToListAsync());
            return rows.Count >= count ? (IReadOnlyList<AllocationEntity>)rows : null;
        }, $"{count} allocations of order {orderId} in state {state}");

    public static Dictionary<string, string> As(string? roles = null) => roles is null ? [] : new() { ["X-Roles"] = roles };

    public Task<HttpResponseMessage> Call(string method, string template, Guid orderId, string? roles = null) =>
        Client.Call(method, template, new { orderId }, null, null, As(roles));

    public async Task<JsonNode> Node(HttpResponseMessage response) => await Client.Node(response);

    public async Task<IChannel> OpenChannel() => await _connection.CreateChannelAsync();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts", "openapi", "fulfilment-api.yaml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}

[CollectionDefinition("fulfilment")]
public sealed class FulfilmentCollection : ICollectionFixture<FulfilmentFixture>;