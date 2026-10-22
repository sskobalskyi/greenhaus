using System.Net;
using System.Text.Json.Nodes;
using Greenhaus.Fulfilment.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Greenhaus.Fulfilment.Tests;

[Collection("fulfilment")]
public class PulseSyncTests(FulfilmentFixture f)
{
    private async Task<(Guid Order, IReadOnlyList<AllocationEntity> Rows)> Allocated(params (string Sku, int Quantity)[] lines)
    {
        var order = Guid.NewGuid();
        await f.Confirmed(order, "customer-sync", lines);
        return (order, await f.WaitForAllocations(order, lines.Sum(l => l.Quantity)));
    }

    private Task<AllocationEntity> Row(string deviceId) => f.WithDb(db => db.Allocations.AsNoTracking().SingleAsync(a => a.DeviceId == deviceId));

    [Fact]
    public async Task A_device_pulse_has_not_registered_yet_is_tried_again_with_a_growing_wait_until_pulse_knows_it()
    {
        var (order, rows) = await Allocated(("GH-POT-KIT-01", 1));
        var device = rows[0].DeviceId;
        await f.Shipped(order, "customer-sync");
        await f.WaitForState(order, "ship_requested", 1);

        // Pulse registers the device when it consumes device.allocated.v1, which can be after the order shipped: a 404 is not final.
        await f.SyncPulse();
        var first = await Row(device);
        Assert.Equal("ship_requested", first.State);
        Assert.Equal(1, first.PulseAttempts);
        Assert.Equal("not_found (404)", first.LastError);
        Assert.True(first.NextAttemptAt > f.Clock.GetUtcNow());

        // Before the wait is over nothing is asked again.
        var calls = f.Pulse.Calls.Count;
        await f.SyncPulse();
        Assert.Equal(calls, f.Pulse.Calls.Count);

        // The wait doubles: 10 s after the first failure, 20 s after the second.
        f.Clock.Advance(TimeSpan.FromSeconds(11));
        await f.SyncPulse();
        var second = await Row(device);
        Assert.Equal(2, second.PulseAttempts);
        Assert.True(second.NextAttemptAt >= f.Clock.GetUtcNow().AddSeconds(19));

        f.Pulse.Register(device);
        f.Clock.Advance(TimeSpan.FromSeconds(30));
        await f.SyncPulse();
        var done = await Row(device);
        Assert.Equal("shipped", done.State);
        Assert.Equal("shipped", f.Pulse.StateOf(device));
        Assert.Null(done.ClaimToken);
        Assert.Null(done.LastError);
    }

    [Fact]
    public async Task A_device_pulse_never_accepts_is_shown_as_failed_and_comes_back_when_someone_asks_again()
    {
        var (order, rows) = await Allocated(("GH-STARTER-01", 1));
        var device = rows[0].DeviceId;
        await f.Shipped(order, "customer-sync");
        await f.WaitForState(order, "ship_requested", 1);

        f.Pulse.Down = true;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await f.SyncPulse();
                f.Clock.Advance(TimeSpan.FromMinutes(10));
            }
        }
        finally { f.Pulse.Down = false; }

        var failed = await Row(device);
        Assert.Equal("pulse_failed", failed.State);
        Assert.Equal("pulse_unavailable (503)", failed.LastError);

        // The listing says so, in a documented shape.
        var listed = await (await f.Call("GET", "/orders/{orderId}/allocations", order)).Content.ReadAsStringAsync();
        Assert.Contains("pulse_failed", listed);

        // Pulse is back and knows the device. Asking again sends it through.
        f.Pulse.Register(device);
        var retried = await f.Call("POST", "/orders/{orderId}/allocations/retry", order);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("ship_requested", (await f.Node(retried))[0]!["state"]!.GetValue<string>());
        await f.SyncPulse();
        Assert.Equal("shipped", (await Row(device)).State);
    }

    [Fact]
    public async Task A_refusal_that_will_not_change_fails_at_once_without_retrying()
    {
        var (order, rows) = await Allocated(("GH-STARTER-01", 1));
        var device = rows[0].DeviceId;
        f.Pulse.Register(device);
        await f.Shipped(order, "customer-sync");
        await f.WaitForState(order, "ship_requested", 1);

        f.Pulse.Refuse = (409, "invalid_state");
        try { await f.SyncPulse(); }
        finally { f.Pulse.Refuse = null; }

        var row = await Row(device);
        Assert.Equal("pulse_failed", row.State);
        Assert.Equal("invalid_state (409)", row.LastError);
        Assert.Equal(1, row.PulseAttempts);
    }

    [Fact]
    public async Task Devices_that_never_shipped_are_retired_on_a_return_or_a_refund_and_their_secrets_go()
    {
        foreach (var cancel in new[] { "returned", "refunded" })
        {
            var (order, rows) = await Allocated(("GH-POT-KIT-01", 2));
            foreach (var r in rows) f.Pulse.Register(r.DeviceId);

            if (cancel == "returned") await f.Returned(order, "customer-sync"); else await f.Refunded(order, "customer-sync");
            var requested = await f.WaitForState(order, "retire_requested", 2);
            Assert.All(requested, r => { Assert.Null(r.ClaimToken); Assert.Null(r.Credential); });

            await f.SyncPulse();
            Assert.Equal(2, (await f.WaitForState(order, "retired", 2)).Count);
            Assert.All(rows, r => Assert.Equal("retired", f.Pulse.StateOf(r.DeviceId)));
        }
    }

    [Fact]
    public async Task Devices_that_already_shipped_are_left_alone_by_a_later_return_or_refund()
    {
        var (order, rows) = await Allocated(("GH-POT-KIT-01", 1));
        f.Pulse.Register(rows[0].DeviceId);
        await f.Shipped(order, "customer-sync");
        await f.WaitForState(order, "ship_requested", 1);
        await f.SyncPulse();
        await f.WaitForState(order, "shipped", 1);

        await f.Returned(order, "customer-sync");
        await f.Refunded(order, "customer-sync");
        // A marker order proves both were handled by the time it is seen.
        var marker = Guid.NewGuid();
        await f.Confirmed(marker, "customer-sync", ("GH-STARTER-01", 1));
        await f.WaitForAllocations(marker, 1);
        await Task.Delay(500);

        Assert.Equal("shipped", (await Row(rows[0].DeviceId)).State);
        Assert.Equal("shipped", f.Pulse.StateOf(rows[0].DeviceId));
    }
}