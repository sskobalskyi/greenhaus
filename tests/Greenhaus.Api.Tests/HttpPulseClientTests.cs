using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Greenhaus.Api.Infrastructure;

namespace Greenhaus.Api.Tests;

public class HttpPulseClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string type = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, type) };

    private const string DeviceJson = """
        {"id":"kit-1","model":"pot","state":"claimed","ownerId":"u1","connection":"online","lastSeenAt":"2026-10-21T10:00:00Z","firmware":"1.2.0",
         "capabilities":{"channels":[{"id":"soil","metric":"soil_moisture","unit":"%"}],
                         "actuators":[{"id":"pump","kind":"pump","hardLimits":{"maxRunSeconds":60,"minIntervalSeconds":600,"dailyCapSeconds":300}},
                                      {"id":"heater","kind":"heater","thermalCutoff":true,"hardLimits":{"maxRunSeconds":1800,"minIntervalSeconds":300,"dailyCapSeconds":14400}}]}}
        """;

    private static (HttpPulseClient Client, Stub Stub) Make(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var stub = new Stub(respond);
        return (new HttpPulseClient(new HttpClient(stub) { BaseAddress = new Uri("http://pulse/api/pulse/v2/") }), stub);
    }

    [Fact]
    public async Task A_claim_sends_the_token_and_owner_and_reads_the_device_with_its_capabilities()
    {
        var (client, stub) = Make((_, _) => Json(HttpStatusCode.OK, DeviceJson));
        var device = await client.ClaimAsync("kit-1", "secret", "u1", default);

        var (request, body) = stub.Seen.Single();
        Assert.Equal("/api/pulse/v2/devices/kit-1/claim", request.RequestUri!.AbsolutePath);
        Assert.Equal("secret", JsonNode.Parse(body!)!["token"]!.GetValue<string>());
        Assert.Equal("u1", JsonNode.Parse(body!)!["ownerId"]!.GetValue<string>());
        Assert.Equal("claimed", device.State);
        Assert.Equal(60, device.Actuators[0].HardLimits.MaxRunSeconds);
        Assert.True(device.Actuators[1].ThermalCutoff);
    }

    [Fact]
    public async Task A_refusal_keeps_the_code_pulse_gave()
    {
        var (client, _) = Make((_, _) => Json(HttpStatusCode.Conflict, """{"title":"No","status":409,"code":"token_invalid"}""", "application/problem+json"));
        var e = await Assert.ThrowsAsync<PulseException>(() => client.ClaimAsync("kit-1", "wrong", "u1", default));
        Assert.Equal(409, e.Status);
        Assert.Equal("token_invalid", e.Code);
    }

    [Fact]
    public async Task An_unknown_device_is_null_and_an_unreachable_pulse_is_unavailable()
    {
        var (client, _) = Make((_, _) => Json(HttpStatusCode.NotFound, """{"title":"x","status":404,"code":"not_found"}""", "application/problem+json"));
        Assert.Null(await client.GetDeviceAsync("nobody", default));

        var down = new HttpPulseClient(new HttpClient(new Stub((_, _) => throw new HttpRequestException("refused"))) { BaseAddress = new Uri("http://pulse/") });
        var e = await Assert.ThrowsAsync<PulseException>(() => down.GetDeviceAsync("x", default));
        Assert.Equal("pulse_unavailable", e.Code);
    }

    [Fact]
    public async Task A_route_pulse_does_not_have_is_not_supported_rather_than_an_unknown_device_or_a_crash()
    {
        var (client, _) = Make((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var e = await Assert.ThrowsAsync<PulseException>(() => client.SetRulesAsync("kit-1", [], default));
        Assert.Equal("pulse_not_supported", e.Code);

        var (broken, _) = Make((_, _) => Json(HttpStatusCode.InternalServerError, "<html>oops</html>", "text/html"));
        Assert.Equal("pulse_error", (await Assert.ThrowsAsync<PulseException>(() => broken.ClaimAsync("kit-1", "t", "u", default))).Code);
    }

    [Fact]
    public async Task A_run_command_uses_the_contracts_names_and_an_expiry()
    {
        var id = Guid.NewGuid();
        var (client, stub) = Make((_, _) => Json(HttpStatusCode.Accepted, $$"""{"commandId":"{{id}}","deviceId":"kit-1","type":"run_actuator","status":"queued","createdAt":"2026-10-21T10:00:00Z"}"""));
        var expires = DateTimeOffset.Parse("2026-10-21T10:02:00Z");
        var command = await client.RunActuatorAsync("kit-1", "pump", 15, id, expires, default);

        var body = JsonNode.Parse(stub.Seen.Single().Body!)!;
        Assert.Equal("run_actuator", body["type"]!.GetValue<string>());
        Assert.Equal("pump", body["actuatorId"]!.GetValue<string>());
        Assert.Equal(15, body["durationSeconds"]!.GetValue<int>());
        Assert.Equal(expires, body["expiresAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("queued", command.Status);
    }

    [Fact]
    public async Task The_latest_reading_is_the_last_point_of_a_raw_series()
    {
        var (client, stub) = Make((_, _) => Json(HttpStatusCode.OK, """[{"ts":"2026-10-21T10:00:00Z","value":40.5},{"ts":"2026-10-21T10:01:00Z","value":41.5}]"""));
        var latest = await client.LatestAsync("kit-1", "soil", DateTimeOffset.Parse("2026-10-21T09:45:00Z"), default);

        Assert.Equal(41.5, latest!.Value.Value);
        var uri = stub.Seen.Single().Request.RequestUri!;
        Assert.Equal("/api/pulse/v2/devices/kit-1/channels/soil/series", uri.AbsolutePath);
        Assert.Contains("resolution=raw", uri.Query);
        Assert.Contains("from=2026-10-21T09%3A45", uri.Query);
    }

    [Fact]
    public void Rules_follow_the_requirements_of_active_channels_only()
    {
        var channels = new[]
        {
            new Greenhaus.Api.Persistence.ChannelEntity { PotId = Guid.NewGuid(), DeviceId = "d", ChannelId = "soil", Metric = Greenhaus.Domain.Metric.SoilMoisture, Active = true },
            new Greenhaus.Api.Persistence.ChannelEntity { PotId = Guid.NewGuid(), DeviceId = "d", ChannelId = "soil2", Metric = Greenhaus.Domain.Metric.SoilMoisture, Active = false },
        };
        var requirements = new Dictionary<Greenhaus.Domain.Metric, Greenhaus.Api.Persistence.RequirementEntity>
        {
            [Greenhaus.Domain.Metric.SoilMoisture] = new() { PotId = Guid.NewGuid(), Metric = Greenhaus.Domain.Metric.SoilMoisture, Enabled = true, Min = 40, Max = 70, Margin = 2 },
        };

        var rules = RuleBuilder.For(channels, requirements);
        Assert.Equal(["threshold", "no_data"], rules.Select(r => r.Kind));
        Assert.All(rules, r => Assert.Equal("soil", r.ChannelId));
    }
}