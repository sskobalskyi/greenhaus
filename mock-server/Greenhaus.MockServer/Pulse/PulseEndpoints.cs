using Microsoft.Extensions.Primitives;

namespace Greenhaus.MockServer.Pulse;

public static class PulseEndpoints
{
    private static IResult ServiceOnly() => Problems.Fail(403, "forbidden", "This operation is for service callers only");

    public static void MapPulseApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/pulse/v2");

        api.MapGet("/devices", (string? state, string? ownerId, HttpContext http, PulseStore pulse) =>
        {
            if (state is not null && !PulseStore.States.Contains(state))
                return Problems.Fail(400, "invalid_state_filter", "Unknown lifecycle state", $"state must be one of: {string.Join(", ", PulseStore.States)}");
            return Results.Ok(pulse.List(Caller.From(http), state, ownerId));
        });

        api.MapPost("/devices", (DeviceRegistration body, HttpContext http, PulseStore pulse) =>
        {
            if (!Caller.From(http).IsService) return ServiceOnly();
            var (device, created, problems) = pulse.Register(body);
            if (problems.Count > 0) return Problems.Fail(400, "validation_failed", "The registration is not valid", string.Join("; ", problems));
            return created ? Results.Json(device, statusCode: 201) : Results.Ok(device);
        });

        api.MapGet("/devices/{deviceId}", (string deviceId, HttpContext http, PulseStore pulse) =>
            pulse.Find(deviceId, Caller.From(http)) is { } state ? Results.Ok(state.Device) : Problems.NotFound("Unknown device"));

        api.MapPost("/devices/{deviceId}/ship", (string deviceId, HttpContext http, PulseStore pulse) =>
        {
            var caller = Caller.From(http);
            if (!caller.IsService) return ServiceOnly();
            if (pulse.Find(deviceId, caller) is not { } state) return Problems.NotFound("Unknown device");
            var (device, error) = pulse.Ship(state);
            return error is null ? Results.Ok(device) : Problems.Fail(409, error, $"A device in state '{state.Device.State}' cannot be shipped");
        });

        api.MapPost("/devices/{deviceId}/claim", (string deviceId, ClaimRequest body, HttpContext http, PulseStore pulse) =>
        {
            var caller = Caller.From(http);
            if (!caller.IsService) return ServiceOnly();
            if (string.IsNullOrEmpty(body.Token) || string.IsNullOrEmpty(body.OwnerId))
                return Problems.Fail(400, "validation_failed", "token and ownerId are required");
            if (pulse.Find(deviceId, caller) is not { } state) return Problems.NotFound("Unknown device");
            var (device, error, retryAfter) = pulse.Claim(state, body, caller);
            return error switch
            {
                null => Results.Ok(device),
                "rate_limited" => Problems.Fail(429, "rate_limited", "Too many claim attempts", retryAfterSeconds: retryAfter),
                "invalid_state" => Problems.Fail(409, "invalid_state", $"A device in state '{state.Device.State}' cannot be claimed"),
                "token_invalid" => Problems.Fail(409, "token_invalid", "The token does not match"),
                _ => Problems.Fail(409, error, "The token has expired"),
            };
        });

        api.MapPost("/devices/{deviceId}/retire", (string deviceId, HttpContext http, PulseStore pulse) =>
        {
            var caller = Caller.From(http);
            if (!caller.IsService) return ServiceOnly();
            return pulse.Find(deviceId, caller) is { } state ? Results.Ok(pulse.Retire(state)) : Problems.NotFound("Unknown device");
        });

        api.MapPut("/devices/{deviceId}/channels/{channelId}/calibration", (string deviceId, string channelId, Calibration body, HttpContext http, PulseStore pulse) =>
        {
            if (pulse.Find(deviceId, Caller.From(http)) is not { } state) return Problems.NotFound("Unknown device");
            var (channel, problem) = pulse.Calibrate(state, channelId, body);
            return problem switch
            {
                null => Results.Ok(channel),
                "unknown_channel" => Problems.NotFound("Unknown channel"),
                _ => Problems.Fail(400, "validation_failed", "dryRaw and wetRaw must be finite numbers and must differ"),
            };
        });

        api.MapGet("/devices/{deviceId}/rules", (string deviceId, HttpContext http, PulseStore pulse) =>
            pulse.Find(deviceId, Caller.From(http)) is { } state ? Results.Ok(state.Rules) : Problems.NotFound("Unknown device"));

        api.MapPut("/devices/{deviceId}/rules", (string deviceId, List<Rule> body, HttpContext http, PulseStore pulse) =>
        {
            if (pulse.Find(deviceId, Caller.From(http)) is not { } state) return Problems.NotFound("Unknown device");
            var problems = pulse.SetRules(state, body);
            return problems.Count > 0 ? Problems.Fail(400, "validation_failed", "The rules are not valid", string.Join("; ", problems)) : Results.Ok(state.Rules);
        });

        api.MapPost("/devices/{deviceId}/commands", (string deviceId, CommandRequest body, HttpContext http, PulseStore pulse) =>
        {
            if (pulse.Find(deviceId, Caller.From(http)) is not { } state) return Problems.NotFound("Unknown device");
            var (command, code, detail, status) = pulse.SendCommand(state, body);
            return command is not null ? Results.Json(command, statusCode: 202) : Problems.Fail(status, code!, detail!);
        });

        api.MapGet("/devices/{deviceId}/commands", (string deviceId, int? limit, HttpContext http, PulseStore pulse) =>
            pulse.Find(deviceId, Caller.From(http)) is { } state ? Results.Ok(pulse.Commands(state, Math.Clamp(limit ?? 50, 1, 200))) : Problems.NotFound("Unknown device"));

        api.MapGet("/devices/{deviceId}/commands/{commandId:guid}", (string deviceId, Guid commandId, HttpContext http, PulseStore pulse) =>
        {
            if (pulse.Find(deviceId, Caller.From(http)) is not { } state) return Problems.NotFound("Unknown device");
            return pulse.FindCommand(state, commandId) is { } command ? Results.Ok(command) : Problems.NotFound("Unknown command");
        });

        api.MapGet("/devices/{deviceId}/channels/{channelId}/series", (string deviceId, string channelId, string? from, string? to, string? resolution, HttpContext http, PulseStore pulse) =>
        {
            if (pulse.Find(deviceId, Caller.From(http)) is not { } state) return Problems.NotFound("Unknown device");
            var (points, truncated, code, detail, status) = pulse.Series(state, channelId, from, to, resolution);
            if (points is null) return Problems.Fail(status, code!, detail!);
            http.Response.Headers["X-Pulse-Truncated"] = truncated ? "true" : StringValues.Empty;
            return Results.Ok(points);
        });

        // ---- mock-only controls, not part of the contract -----------------------------------------------------
        // A real device reports over MQTT or HTTP. These stand in for it.
        var mock = app.MapGroup("/mock/pulse");
        mock.MapPost("/devices/{deviceId}/reading", (string deviceId, PulseStore pulse) =>
            pulse.SimulateReading(deviceId) is { } device ? Results.Ok(device) : Problems.NotFound("Unknown or retired device"));
        mock.MapPost("/devices/{deviceId}/silence", (string deviceId, PulseStore pulse) =>
            pulse.SimulateSilence(deviceId) is { } device ? Results.Ok(device) : Problems.NotFound("Unknown device"));
    }
}
