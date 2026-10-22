using System.Collections.Concurrent;
using Greenhaus.Fulfilment.Infrastructure;

namespace Greenhaus.Fulfilment.Tests;

/// <summary>
/// An in-memory Pulse that knows the rules fulfilment relies on: a device it has not registered answers 404, shipping and retiring
/// are idempotent, and it can be switched off. Pulse registers a device when it consumes device.allocated.v1, so tests register
/// a device by hand when they want Pulse to "have heard" of it.
/// </summary>
public sealed class FakePulse : IPulseDevices
{
    private readonly ConcurrentDictionary<string, string> _devices = new();

    public ConcurrentQueue<string> Calls { get; } = new();
    public bool Down { get; set; }
    /// <summary>When set, every call is refused with this status and code.</summary>
    public (int Status, string Code)? Refuse { get; set; }

    public void Register(string deviceId) => _devices.TryAdd(deviceId, "allocated");
    public string? StateOf(string deviceId) => _devices.GetValueOrDefault(deviceId);

    public Task ShipAsync(string deviceId, CancellationToken ct) => Act(deviceId, "ship", "shipped");
    public Task RetireAsync(string deviceId, CancellationToken ct) => Act(deviceId, "retire", "retired");

    private Task Act(string deviceId, string call, string state)
    {
        Calls.Enqueue($"{call} {deviceId}");
        if (Down) throw new PulseException(503, "pulse_unavailable", "down");
        if (Refuse is { } refuse) throw new PulseException(refuse.Status, refuse.Code, "refused");
        if (!_devices.ContainsKey(deviceId)) throw new PulseException(404, "not_found", "Not found");
        _devices[deviceId] = state;
        return Task.CompletedTask;
    }
}