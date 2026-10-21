using Greenhaus.Api.Persistence;
using Greenhaus.ContractTesting;
using Greenhaus.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Greenhaus.Api.Tests;

/// <summary>One real PostgreSQL and one running API for the whole test run. Tests isolate themselves with their own user.</summary>
public sealed class GreenhausFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder().WithImage("rabbitmq:4-alpine").Build();
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public OpenApiContract Contract { get; private set; } = null!;
    public ContractClient Client { get; private set; } = null!;
    public FakePulse Pulse { get; } = new();
    public AsyncApiContract Events { get; private set; } = null!;
    public string RabbitHost => _rabbit.Hostname;
    public int RabbitPort => _rabbit.GetMappedPublicPort(5672);

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        var root = FindRepositoryRoot();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Greenhaus", _postgres.GetConnectionString());
            b.UseSetting("Auth:Mode", "Development");
            b.UseSetting("RabbitMq:Host", _rabbit.Hostname);
            b.UseSetting("RabbitMq:Port", _rabbit.GetMappedPublicPort(5672).ToString());
            b.UseSetting("RabbitMq:User", RabbitMqBuilder.DefaultUsername);
            b.UseSetting("RabbitMq:Password", RabbitMqBuilder.DefaultPassword);
            b.UseSetting("Species:Path", Path.Combine(root, "data", "species.json"));
            b.ConfigureTestServices(services => services.AddSingleton<Greenhaus.Api.Infrastructure.IPulseClient>(Pulse));
        });
        _client = _factory.CreateClient();
        Contract = new OpenApiContract(Path.Combine(root, "contracts", "openapi", "greenhaus-api.yaml"));
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

    public async Task<T> WithDb<T>(Func<GreenhausDbContext, Task<T>> action)
    {
        using var scope = _factory!.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<GreenhausDbContext>());
    }

    /// <summary>Puts a user on a tier the way billing's entitlement event would.</summary>
    public Task Grant(string user, Tier tier) => WithDb(async db =>
    {
        db.Entitlements.Add(new EntitlementEntity { UserId = user, Tier = tier, Source = "subscription", Status = "active", UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return 0;
    });

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts", "openapi", "greenhaus-api.yaml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<GreenhausFixture>;