using Greenhaus.Api.Persistence;
using Greenhaus.ContractTesting;
using Greenhaus.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Greenhaus.Api.Tests;

/// <summary>One real PostgreSQL and one running API for the whole test run. Tests isolate themselves with their own user.</summary>
public sealed class GreenhausFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public OpenApiContract Contract { get; private set; } = null!;
    public ContractClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var root = FindRepositoryRoot();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Greenhaus", _postgres.GetConnectionString());
            b.UseSetting("Auth:Mode", "Development");
            b.UseSetting("Species:Path", Path.Combine(root, "data", "species.json"));
        });
        _client = _factory.CreateClient();
        Contract = new OpenApiContract(Path.Combine(root, "contracts", "openapi", "greenhaus-api.yaml"));
        Client = new ContractClient(_client, Contract);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
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