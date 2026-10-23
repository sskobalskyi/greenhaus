namespace Greenhaus.MockServer;

public static class Endpoints
{
    private static IResult Fail(int status, string code, string title, string? detail = null, string? limitType = null, Tier? requiredTier = null) =>
        Results.Json(
            new Problem { Title = title, Status = status, Code = code, Detail = detail, LimitType = limitType, RequiredTier = requiredTier },
            contentType: "application/problem+json",
            statusCode: status);

    private static IResult NotFound(string what = "Not found") => Fail(404, "not_found", what);

    /// <summary>The mock reads the tier from X-Mock-Tier so clients can try every tier. Default is the demo user's tier.</summary>
    private static Tier TierOf(HttpContext ctx, MockStore store) =>
        Enum.TryParse<Tier>(ctx.Request.Headers["X-Mock-Tier"].FirstOrDefault(), ignoreCase: true, out var tier) ? tier : store.DefaultTier();

    private static Tier NextTier(Tier tier) => tier == Tier.Novice ? Tier.Gardener : Tier.Druid;

    private static void Audit(PotState pot, string actor, string action, string reason, string? actuatorId = null, string? outcome = null) =>
        pot.Audit.Insert(0, new AuditEntry
        {
            Id = Guid.NewGuid(), At = DateTimeOffset.UtcNow, Actor = actor, ActuatorId = actuatorId, Action = action, Reason = reason, Outcome = outcome,
        });

    public static void MapGreenhausApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/greenhaus/v1");

        // ---- profile and entitlements --------------------------------------------------------------
        api.MapGet("/me", (HttpContext ctx, MockStore s) =>
        {
            lock (s.Sync) return Results.Ok(new Profile
            {
                Id = s.User.Id, DisplayName = s.User.DisplayName, Email = s.User.Email, Locale = s.User.Locale,
                Tier = TierOf(ctx, s), CreatedAt = s.User.CreatedAt,
            });
        });

        api.MapPatch("/me", (ProfileUpdate body, HttpContext ctx, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (body.DisplayName is null && body.Locale is null) return Fail(400, "validation_failed", "Nothing to update");
                if (body.Locale is not null && body.Locale is not ("en" or "uk")) return Fail(400, "validation_failed", "Unknown locale");
                if (body.DisplayName is { Length: 0 or > 60 }) return Fail(400, "validation_failed", "Display name must be 1 to 60 characters");
                if (body.DisplayName is not null) s.User.DisplayName = body.DisplayName;
                if (body.Locale is not null) s.User.Locale = body.Locale;
                return Results.Ok(new Profile
                {
                    Id = s.User.Id, DisplayName = s.User.DisplayName, Email = s.User.Email, Locale = s.User.Locale,
                    Tier = TierOf(ctx, s), CreatedAt = s.User.CreatedAt,
                });
            }
        });

        api.MapGet("/me/entitlements", (HttpContext ctx, MockStore s) =>
        {
            lock (s.Sync)
            {
                var scenario = ctx.Request.Headers["X-Mock-Scenario"].FirstOrDefault();
                // Without a tier header the entitlements come from the billing mock, so subscribing changes them.
                return ctx.Request.Headers.ContainsKey("X-Mock-Tier") || scenario is not null
                    ? Results.Ok(s.EntitlementsFor(TierOf(ctx, s), scenario))
                    : Results.Ok(s.EntitlementsFromBilling());
            }
        });

        api.MapGet("/tiers", () =>
            Results.Ok(new[] { Tier.Novice, Tier.Gardener, Tier.Druid }
                .Select(tier => new TierLimits { Tier = tier, Limits = MockStore.LimitsFor(tier) })
                .ToList()));

        // ---- species ---------------------------------------------------------------------------------
        api.MapGet("/species", (string? query, MockStore s) =>
            Results.Ok(string.IsNullOrWhiteSpace(query)
                ? s.Species
                : s.Species.Where(x => x.CommonName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || x.ScientificName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList()));

        api.MapGet("/species/{speciesId}", (string speciesId, MockStore s) =>
            s.FindSpecies(speciesId) is { } species ? Results.Ok(species) : NotFound("Unknown species"));

        // ---- pots ------------------------------------------------------------------------------------
        api.MapGet("/pots", (MockStore s) =>
        {
            lock (s.Sync) return Results.Ok(s.Pots.Values.Select(p => p.Pot).OrderBy(p => p.CreatedAt).ToList());
        });

        api.MapPost("/pots", (PotCreate body, HttpContext ctx, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 60 || body.Kind is null)
                    return Fail(400, "validation_failed", "A name (1 to 60 characters) and a kind are required");
                var tier = TierOf(ctx, s);
                var limit = MockStore.LimitsFor(tier).Pots;
                if (limit is { } max && s.Pots.Count >= max)
                    return Fail(403, "limit_reached", "Pot limit reached", $"The {tier} tier allows {max} pots", "pots", NextTier(tier));
                var pot = s.CreatePot(body.Name, body.Location, body.Kind.Value);
                return Results.Created($"/api/greenhaus/v1/pots/{pot.Pot.Id}", pot.Pot);
            }
        });

        api.MapGet("/pots/{potId:guid}", (Guid potId, MockStore s) =>
        {
            lock (s.Sync) return s.Pots.TryGetValue(potId, out var p) ? Results.Ok(p.Pot) : NotFound();
        });

        api.MapPatch("/pots/{potId:guid}", (Guid potId, PotUpdate body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                if (body.Name is null && body.Location is null) return Fail(400, "validation_failed", "Nothing to update");
                if (body.Name is { Length: 0 or > 60 }) return Fail(400, "validation_failed", "Name must be 1 to 60 characters");
                if (body.Name is not null) p.Pot.Name = body.Name;
                if (body.Location is not null) p.Pot.Location = body.Location;
                return Results.Ok(p.Pot);
            }
        });

        api.MapDelete("/pots/{potId:guid}", (Guid potId, MockStore s) =>
        {
            lock (s.Sync) return s.Pots.Remove(potId) ? Results.NoContent() : NotFound();
        });

        // ---- zones -----------------------------------------------------------------------------------
        api.MapPut("/pots/{potId:guid}/view-mode", (Guid potId, ViewModeUpdate body, HttpContext ctx, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                if (body.Mode is null) return Fail(400, "validation_failed", "A mode is required");
                var tier = TierOf(ctx, s);
                var limits = MockStore.LimitsFor(tier);

                if (body.Mode == ViewMode.Zones)
                {
                    var rows = body.GridRows ?? 2;
                    var cols = body.GridCols ?? 2;
                    if (limits.ZoneGridMax < 2)
                        return Fail(403, "tier_required", "The zone grid needs a higher tier", null, "zone_grid", Tier.Gardener);
                    if (rows > limits.ZoneGridMax || cols > limits.ZoneGridMax)
                        return Fail(403, "limit_reached", "Grid too large for this tier", $"The {tier} tier allows up to {limits.ZoneGridMax}x{limits.ZoneGridMax}", "zone_grid", NextTier(tier));

                    p.Zones = MockStore.BuildZones(rows, cols, simple: false);
                    foreach (var group in p.Channels.GroupBy(c => c.Metric))
                    {
                        var index = 0;
                        foreach (var channel in group) channel.ZoneId = p.Zones[index++ % p.Zones.Count].Id;
                    }
                    foreach (var plant in p.Plants) plant.ZoneId = null;
                    p.Pot.ViewMode = ViewMode.Zones;
                    p.Pot.GridRows = rows;
                    p.Pot.GridCols = cols;
                    return Results.Ok(p.Pot);
                }

                foreach (var group in p.Channels.GroupBy(c => c.Metric).Where(g => g.Count() > 1))
                {
                    var chosen = body.PrimaryChannels?.FirstOrDefault(pc => pc.Metric == group.Key && group.Any(c => c.Id == pc.ChannelId));
                    if (chosen is null)
                        return Fail(400, "primary_channel_required", "Choose a primary channel", $"More than one {group.Key} channel exists");
                    p.Channels.RemoveAll(c => c.Metric == group.Key && c.Id != chosen.ChannelId);
                }

                p.Zones = MockStore.BuildZones(1, 1, simple: true);
                foreach (var channel in p.Channels) channel.ZoneId = p.Zones[0].Id;
                foreach (var plant in p.Plants) plant.ZoneId = null;
                p.Pot.ViewMode = ViewMode.Simple;
                p.Pot.GridRows = null;
                p.Pot.GridCols = null;
                return Results.Ok(p.Pot);
            }
        });

        api.MapGet("/pots/{potId:guid}/zones", (Guid potId, MockStore s) =>
        {
            lock (s.Sync) return s.Pots.TryGetValue(potId, out var p) ? Results.Ok(p.Zones) : NotFound();
        });

        api.MapPatch("/pots/{potId:guid}/zones/{zoneId:guid}", (Guid potId, Guid zoneId, LabelUpdate body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                var zone = p.Zones.FirstOrDefault(z => z.Id == zoneId);
                if (zone is null) return NotFound("Unknown zone");
                if (string.IsNullOrWhiteSpace(body.Label) || body.Label.Length > 40) return Fail(400, "validation_failed", "Label must be 1 to 40 characters");
                zone.Label = body.Label;
                return Results.Ok(zone);
            }
        });

        // ---- plants ----------------------------------------------------------------------------------
        api.MapGet("/pots/{potId:guid}/plants", (Guid potId, MockStore s) =>
        {
            lock (s.Sync) return s.Pots.TryGetValue(potId, out var p) ? Results.Ok(p.Plants) : NotFound();
        });

        api.MapPost("/pots/{potId:guid}/plants", (Guid potId, PlantCreate body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                if (string.IsNullOrWhiteSpace(body.SpeciesId) || string.IsNullOrWhiteSpace(body.Nickname) || body.Nickname.Length > 60)
                    return Fail(400, "validation_failed", "A species and a nickname (1 to 60 characters) are required");
                if (s.FindSpecies(body.SpeciesId) is null) return Fail(400, "unknown_species", "Unknown species");
                if (body.ZoneId is { } zoneId && p.Zones.All(z => z.Id != zoneId)) return Fail(400, "validation_failed", "The zone does not belong to this pot");
                var plant = new Plant
                {
                    Id = Guid.NewGuid(), SpeciesId = body.SpeciesId, Nickname = body.Nickname, ZoneId = body.ZoneId,
                    PlantedAt = body.PlantedAt ?? DateOnly.FromDateTime(DateTime.UtcNow), Notes = body.Notes,
                };
                p.Plants.Add(plant);
                return Results.Created($"/api/greenhaus/v1/pots/{potId}/plants/{plant.Id}", plant);
            }
        });

        api.MapPatch("/pots/{potId:guid}/plants/{plantId:guid}", (Guid potId, Guid plantId, PlantUpdate body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                var plant = p.Plants.FirstOrDefault(x => x.Id == plantId);
                if (plant is null) return NotFound("Unknown plant");
                if (body.Nickname is null && body.ZoneId is null && body.Notes is null) return Fail(400, "validation_failed", "Nothing to update");
                if (body.ZoneId is { } zoneId && p.Zones.All(z => z.Id != zoneId)) return Fail(400, "validation_failed", "The zone does not belong to this pot");
                if (body.Nickname is not null) plant.Nickname = body.Nickname;
                if (body.ZoneId is not null) plant.ZoneId = body.ZoneId;
                if (body.Notes is not null) plant.Notes = body.Notes;
                return Results.Ok(plant);
            }
        });

        api.MapDelete("/pots/{potId:guid}/plants/{plantId:guid}", (Guid potId, Guid plantId, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                return p.Plants.RemoveAll(x => x.Id == plantId) > 0 ? Results.NoContent() : NotFound("Unknown plant");
            }
        });

        // ---- requirements ----------------------------------------------------------------------------
        api.MapGet("/pots/{potId:guid}/requirements", (Guid potId, MockStore s) =>
        {
            lock (s.Sync) return s.Pots.TryGetValue(potId, out var p) ? Results.Ok(p.Requirements) : NotFound();
        });

        api.MapPut("/pots/{potId:guid}/requirements", (Guid potId, Requirements body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                if (body.Metrics.Any(m => m.Min is { } min && m.Max is { } max && min > max))
                    return Fail(400, "validation_failed", "A minimum cannot be above its maximum");
                if (body.Metrics.GroupBy(m => m.Metric).Any(g => g.Count() > 1))
                    return Fail(400, "validation_failed", "Each metric may appear once");
                p.Requirements = body;
                return Results.Ok(p.Requirements);
            }
        });

        api.MapGet("/pots/{potId:guid}/requirements/suggestion", (Guid potId, MockStore s) =>
        {
            lock (s.Sync) return s.Pots.TryGetValue(potId, out var p) ? Results.Ok(s.Suggest(p)) : NotFound();
        });

        // ---- status ----------------------------------------------------------------------------------
        api.MapGet("/pots/{potId:guid}/status", (Guid potId, MockStore s) =>
        {
            lock (s.Sync) return s.Pots.TryGetValue(potId, out var p) ? Results.Ok(s.StatusOf(p, DateTimeOffset.UtcNow)) : NotFound();
        });

        api.MapGet("/pots/{potId:guid}/devices", (Guid potId, MockStore s) =>
        {
            lock (s.Sync) return s.Pots.TryGetValue(potId, out var p) ? Results.Ok(p.Devices) : NotFound();
        });

        // ---- controls --------------------------------------------------------------------------------
        api.MapGet("/pots/{potId:guid}/actuators", (Guid potId, HttpContext ctx, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                var levels = MockStore.LimitsFor(TierOf(ctx, s)).Automation;
                foreach (var a in p.Actuators) a.MaxModeForTier = levels.First(l => l.Metric == a.Metric).MaxMode;
                return Results.Ok(p.Actuators);
            }
        });

        api.MapPut("/pots/{potId:guid}/actuators/{actuatorId}/mode", (Guid potId, string actuatorId, ModeUpdate body, HttpContext ctx, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                var actuator = p.Actuators.FirstOrDefault(a => a.Id == actuatorId);
                if (actuator is null) return NotFound("Unknown actuator");
                if (body.Mode is null) return Fail(400, "validation_failed", "A mode is required");

                var max = MockStore.LimitsFor(TierOf(ctx, s)).Automation.First(l => l.Metric == actuator.Metric).MaxMode;
                if (body.Mode > max)
                    return Fail(403, "tier_required", "This automation needs a higher tier", null, "automation", MockStore.LowestTierAllowingAuto(actuator.Metric));

                if (body.Mode == ControlMode.Auto)
                {
                    if (actuator.Kind == ActuatorKind.Heater && p.Devices.Any(d => !d.ThermalCutoffDeclared))
                        return Fail(400, "thermal_cutoff_required", "A heater cannot run in auto mode without a hardware thermal cutoff");
                    if (p.Channels.Any(c => c.Metric == actuator.Metric && c.Calibration is null))
                        return Fail(400, "calibration_required", "Calibrate the sensor before enabling auto mode");
                }

                var previous = actuator.Mode;
                actuator.Mode = body.Mode.Value;
                actuator.MaxModeForTier = max;
                Audit(p, "user", "mode_changed", $"Mode changed from {previous} to {actuator.Mode}", actuatorId, "completed");
                return Results.Ok(actuator);
            }
        });

        api.MapPost("/pots/{potId:guid}/actuators/{actuatorId}/run", (Guid potId, string actuatorId, RunRequest body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                var actuator = p.Actuators.FirstOrDefault(a => a.Id == actuatorId);
                if (actuator is null) return NotFound("Unknown actuator");
                if (body.DurationSeconds is null or < 1 or > 3600) return Fail(400, "validation_failed", "Duration must be 1 to 3600 seconds");
                if (p.Pot.KillSwitch)
                {
                    Audit(p, "safety", "blocked", "Manual run refused because the kill switch is on", actuatorId, "refused");
                    return Fail(409, "kill_switch_on", "The kill switch is on");
                }

                var duration = Math.Min(body.DurationSeconds.Value, actuator.Limits.MaxRunSeconds);
                actuator.LastRunAt = DateTimeOffset.UtcNow;
                Audit(p, "user", "run", $"Manual run for {duration} s", actuatorId, "completed");
                return Results.Json(new CommandReceipt { CommandId = Guid.NewGuid(), Status = "sent" }, statusCode: 202);
            }
        });

        api.MapPut("/pots/{potId:guid}/actuators/{actuatorId}/limits", (Guid potId, string actuatorId, SafetyLimits body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                var actuator = p.Actuators.FirstOrDefault(a => a.Id == actuatorId);
                if (actuator is null) return NotFound("Unknown actuator");
                var hard = actuator.HardLimits!;
                if (body.MaxRunSeconds < 1 || body.DailyCapSeconds < 1 || body.MinIntervalSeconds < 0)
                    return Fail(400, "validation_failed", "Limits must be positive");
                if (body.MaxRunSeconds > hard.MaxRunSeconds || body.DailyCapSeconds > hard.DailyCapSeconds || body.MinIntervalSeconds < hard.MinIntervalSeconds)
                    return Fail(400, "exceeds_hard_limit", "Limits can only be tightened within the controller's hard limits");
                actuator.Limits = body;
                Audit(p, "user", "limit_changed", "Safety limits were tightened", actuatorId, "completed");
                return Results.Ok(actuator);
            }
        });

        api.MapPut("/pots/{potId:guid}/kill-switch", (Guid potId, KillSwitchUpdate body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                if (body.Enabled is null) return Fail(400, "validation_failed", "enabled is required");
                p.Pot.KillSwitch = body.Enabled.Value;
                Audit(p, "user", "kill_switch", body.Enabled.Value ? "Kill switch turned on" : "Kill switch turned off", null, "completed");
                return Results.Ok(p.Pot);
            }
        });

        api.MapGet("/pots/{potId:guid}/audit-log", (Guid potId, int? limit, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                return Results.Ok(p.Audit.Take(Math.Clamp(limit ?? 50, 1, 200)).ToList());
            }
        });

        api.MapPut("/pots/{potId:guid}/channels/{channelId}/calibration", (Guid potId, string channelId, Calibration body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                var channel = p.Channels.FirstOrDefault(c => c.Id == channelId);
                if (channel is null) return NotFound("Unknown channel");
                if (body.DryRaw == body.WetRaw) return Fail(400, "validation_failed", "The dry and wet values must differ");
                channel.Calibration = new Calibration { DryRaw = body.DryRaw, WetRaw = body.WetRaw, CalibratedAt = DateTimeOffset.UtcNow };
                return Results.Ok(channel.Calibration);
            }
        });

        // ---- care tasks ------------------------------------------------------------------------------
        api.MapGet("/pots/{potId:guid}/care-tasks", (Guid potId, string? state, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                return Results.Ok(p.Tasks.Where(t => state is null || t.State == state).OrderBy(t => t.DueAt).ToList());
            }
        });

        api.MapPost("/pots/{potId:guid}/care-tasks", (Guid potId, CareTaskCreate body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                if (string.IsNullOrWhiteSpace(body.Title) || body.Title.Length > 80 || body.DueAt is null)
                    return Fail(400, "validation_failed", "A title (1 to 80 characters) and a due date are required");
                var task = new CareTask
                {
                    Id = Guid.NewGuid(), Kind = "custom", Title = body.Title, DueAt = body.DueAt.Value, State = "open", Origin = "user",
                };
                p.Tasks.Add(task);
                return Results.Created($"/api/greenhaus/v1/pots/{potId}/care-tasks/{task.Id}", task);
            }
        });

        api.MapPost("/pots/{potId:guid}/care-tasks/{taskId:guid}/complete", (Guid potId, Guid taskId, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Pots.TryGetValue(potId, out var p)) return NotFound();
                var task = p.Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is null) return NotFound("Unknown task");
                task.State = "done";
                task.CompletedAt = DateTimeOffset.UtcNow;
                return Results.Ok(task);
            }
        });

        // ---- claims ----------------------------------------------------------------------------------
        // ---- notifications ---------------------------------------------------------------------------
        api.MapGet("/notifications", (bool? unread, int? limit, MockStore s) =>
        {
            if (limit is < 1 or > 100) return Fail(400, "validation_failed", "limit must be 1 to 100");
            lock (s.Sync)
            {
                return Results.Ok(s.Notifications.Where(n => unread != true || n.ReadAt is null).OrderByDescending(n => n.CreatedAt).Take(limit ?? 30).ToList());
            }
        });

        api.MapPost("/notifications/{notificationId:guid}/read", (Guid notificationId, MockStore s) =>
        {
            lock (s.Sync)
            {
                var notification = s.Notifications.FirstOrDefault(n => n.Id == notificationId);
                if (notification is null) return NotFound("Unknown notification");
                notification.ReadAt ??= DateTimeOffset.UtcNow;
                return Results.Ok(notification);
            }
        });

        api.MapPost("/notifications/read-all", (MockStore s) =>
        {
            lock (s.Sync)
            {
                foreach (var n in s.Notifications.Where(n => n.ReadAt is null)) n.ReadAt = DateTimeOffset.UtcNow;
                return Results.NoContent();
            }
        });

        // The assistant streams its answer as server-sent events. The text is canned, but the rules are the real ones:
        // a monthly quota per tier, only answered questions count, and what the answer says it saw depends on the tier.
        api.MapPost("/pots/{potId:guid}/assistant/ask", async (Guid potId, AssistantQuestion body, HttpContext ctx, MockStore s) =>
        {
            bool exists; Tier tier; int used;
            lock (s.Sync) { exists = s.Pots.ContainsKey(potId); tier = TierOf(ctx, s); used = s.AiQuestions; }
            if (!exists) { await NotFound().ExecuteAsync(ctx); return; }
            if (string.IsNullOrWhiteSpace(body.Question) || body.Question.Length > 500)
            {
                await Fail(400, "validation_failed", "A question of 1 to 500 characters is required").ExecuteAsync(ctx);
                return;
            }

            var limits = MockStore.LimitsFor(tier);
            if (used >= limits.AiQuestionsPerMonth)
            {
                await Fail(403, "limit_reached", "Question limit reached", $"The {tier} tier allows {limits.AiQuestionsPerMonth} questions a month", "ai_questions", NextTier(tier)).ExecuteAsync(ctx);
                return;
            }

            var seen = limits.AiDataAccess switch
            {
                "history" => "your readings of the last days and your care log",
                "current" => "the current readings of your pot",
                _ => "your pot and its plants (readings are not part of your plan)",
            };
            var words = $"Based on {seen}: the plants look fine. Water when the soil gets dry.".Split(' ');
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            async Task Send(object evt) { await ctx.Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(evt, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))}\n\n"); await ctx.Response.Body.FlushAsync(); }
            await Send(new { type = "sources", sources = Array.Empty<object>() });
            foreach (var word in words) await Send(new { type = "token", text = word + " " });
            await Send(new { type = "done", mode = "answer", tokens = words.Length });
            lock (s.Sync) s.AiQuestions++;
        });

        api.MapGet("/claims/preview", (string deviceId, string token, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (!s.Devices.TryGetValue(deviceId, out var d) || d.Token != token) return Fail(404, "token_invalid", "Unknown device or token");
                var reason = d.Claimed ? "already_claimed" : d.Expired ? "token_expired" : !d.Shipped ? "not_shipped" : null;
                return Results.Ok(new ClaimPreview
                {
                    DeviceId = d.Id, Model = d.Model, Claimable = reason is null, Reason = reason, Channels = d.Channels, Actuators = d.Actuators,
                });
            }
        });

        api.MapPost("/claims", (ClaimRequest body, MockStore s) =>
        {
            lock (s.Sync)
            {
                if (string.IsNullOrWhiteSpace(body.DeviceId) || string.IsNullOrWhiteSpace(body.Token) || body.PotId is null)
                    return Fail(400, "validation_failed", "deviceId, token and potId are required");
                if (!s.Devices.TryGetValue(body.DeviceId, out var d) || d.Token != body.Token) return Fail(404, "token_invalid", "Unknown device or token");
                if (!s.Pots.TryGetValue(body.PotId.Value, out var p)) return NotFound("Unknown pot");
                if (d.Claimed) return Fail(409, "already_claimed", "The device is already claimed");
                if (d.Expired) return Fail(409, "token_expired", "The claim token has expired");
                if (!d.Shipped) return Fail(409, "not_shipped", "The order has not shipped yet");

                d.Claimed = true;
                p.Devices.Add(new LinkedDevice
                {
                    DeviceId = d.Id, Model = d.Model, Status = "online", LastSeenAt = DateTimeOffset.UtcNow, Firmware = "0.3.1", ThermalCutoffDeclared = true,
                });
                foreach (var channel in d.Channels)
                {
                    p.Channels.Add(new Channel { Id = $"{d.Id}-{channel.ChannelId}", Metric = channel.Metric, ZoneId = p.Zones[0].Id, Base = channel.Metric == Metric.Temperature ? 22 : 50 });
                }
                return Results.Created("/api/greenhaus/v1/claims", new Claim { DeviceId = d.Id, PotId = p.Pot.Id, ClaimedAt = DateTimeOffset.UtcNow });
            }
        });
    }
}
