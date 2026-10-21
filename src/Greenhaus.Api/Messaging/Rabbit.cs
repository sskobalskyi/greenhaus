using RabbitMQ.Client;

namespace Greenhaus.Api.Messaging;

public sealed class RabbitOptions
{
    /// <summary>Empty means messaging is off: events stay in the outbox and nothing is consumed.</summary>
    public string? Host { get; set; }
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string Exchange { get; set; } = "integration.events";
    public string Queue { get; set; } = "greenhaus.events";
}

public static class Rabbit
{
    public const string DeadLetterExchange = "integration.events.dlx";

    /// <summary>The events Greenhaus acts on.</summary>
    public static readonly string[] Consumed = ["entitlement.changed.v1", "device.allocated.v1", "device.retired.v1", "command.completed.v1"];

    public static Task<IConnection> ConnectAsync(RabbitOptions o, CancellationToken ct) =>
        new ConnectionFactory { HostName = o.Host!, Port = o.Port, UserName = o.User, Password = o.Password, VirtualHost = o.VirtualHost, AutomaticRecoveryEnabled = true }
            .CreateConnectionAsync(ct);

    public static async Task DeclareExchangeAsync(IChannel channel, RabbitOptions o, CancellationToken ct) =>
        await channel.ExchangeDeclareAsync(o.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);

    public static string? Text(IDictionary<string, object?>? headers, string name) => headers is not null && headers.TryGetValue(name, out var v)
        ? v switch { byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes), string s => s, _ => v?.ToString() }
        : null;
}