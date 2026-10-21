namespace Greenhaus.Api.Infrastructure;

/// <summary>Sends a run command to a controller. Pulse in production.</summary>
public interface ICommandSender
{
    Task<Guid> RunAsync(string deviceId, string actuatorId, int seconds, CancellationToken ct);
}

/// <summary>Used until a Pulse client is configured: the command is accepted and goes nowhere.</summary>
public sealed class NoCommands : ICommandSender
{
    public Task<Guid> RunAsync(string deviceId, string actuatorId, int seconds, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
}