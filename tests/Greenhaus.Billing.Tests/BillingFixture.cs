using Greenhaus.Billing.Persistence;
using Greenhaus.ContractTesting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Greenhaus.Billing.Tests;

/// <summary>One real PostgreSQL and one running billing service. Tests isolate themselves with their own user and move a fake clock.</summary>
public sealed class BillingFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder().WithImage("rabbitmq:4-alpine").Build();
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    public OpenApiContract Contract { get; private set; } = null!;
    public ContractClient Client { get; private set; } = null!;
    public AsyncApiContract Events { get; private set; } = null!;
    public string RabbitHost => _rabbit.Hostname;
    public int RabbitPort => _rabbit.GetMappedPublicPort(5672);

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        var root = FindRepositoryRoot();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Billing", _postgres.GetConnectionString());
            b.UseSetting("Auth:Mode", "Development");
            b.UseSetting("RabbitMq:Host", _rabbit.Hostname);
            b.UseSetting("RabbitMq:Port", _rabbit.GetMappedPublicPort(5672).ToString());
            b.UseSetting("RabbitMq:User", RabbitMqBuilder.DefaultUsername);
            b.UseSetting("RabbitMq:Password", RabbitMqBuilder.DefaultPassword);
            // Tests move the clock and ask for the answer, so the background job would only get in the way.
            b.UseSetting("Billing:RunTicker", "false");
            b.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
        });
        _client = _factory.CreateClient();
        Contract = new OpenApiContract(Path.Combine(root, "contracts", "openapi", "billing-api.yaml"));
        Client = new ContractClient(_client, Contract);
        Events = new AsyncApiContract(Path.Combine(root, "contracts", "asyncapi", "integration-events.yaml"));
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    public static string NewUser() => "user-" + Guid.NewGuid().ToString("N")[..12];

    public static Dictionary<string, string> As(string user) => new() { ["X-User-Id"] = user };

    public async Task<T> WithDb<T>(Func<BillingDbContext, Task<T>> action)
    {
        using var scope = _factory!.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<BillingDbContext>());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts", "openapi", "billing-api.yaml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}

[CollectionDefinition("billing")]
public sealed class BillingCollection : ICollectionFixture<BillingFixture>;