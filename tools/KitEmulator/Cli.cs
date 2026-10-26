using System.Globalization;
using System.Text.Json;

namespace Greenhaus.KitEmulator;

public sealed class EmulatorOptions
{
    public BrokerSettings Broker { get; set; } = new("localhost", 1883);
    public List<DeviceSpec> Devices { get; } = [];
    public string? KitsPath { get; set; }
    public double Speed { get; set; } = 1;
    public double Interval { get; set; } = 5;
    public double Noise { get; set; } = 1;
    public int Seed { get; set; } = 1;
    public Dictionary<string, double> Initial { get; } = [];
    public string? ProvisionFrom { get; set; }
    public string? Order { get; set; }
    public string? Token { get; set; }
    public bool Help { get; set; }

    public const string DefaultSku = "GH-POT-KIT-01";

    public const string Usage = """
        Greenhaus kit emulator: pot controllers that talk to the broker the way the real ones will.

          --broker <host[:port]>          the MQTT broker (default localhost:1883, or KIT_BROKER)
          --device <id>[:<sku>]           a controller to emulate, repeatable (sku default GH-POT-KIT-01)
          --credential <id>=<password>    the broker password of a device, repeatable (the local broker may be anonymous)
          --provision-from <url>          the fulfilment API (for example http://localhost:8093/api/fulfilment/v1) to read the kits of an order from,
          --order <orderId>               with the order, and
          --token <bearer>                an admin token (or FULFILMENT_TOKEN): the emulator is flashed with the id and credential of the allocated devices
          --kits <path>                   data/kit-capabilities.json (default: KITS_PATH, or found above the current folder)
          --speed <n>                     the surroundings change n times faster than real time (default 1, 60 shows a day cycle in 24 minutes)
          --initial soil=22,tank=80       a starting state: soil, temp, humidity, tank, hour, room
          --interval <seconds>            how often readings are sent (default 5)
          --noise <n>                     scale of the sensor noise, 0 for exact readings (default 1)
          --seed <n>                      seed of the noise
        """;

    public static EmulatorOptions Parse(string[] args)
    {
        var options = new EmulatorOptions();
        var credentials = new Dictionary<string, string>();
        var devices = new List<(string Id, string Sku)>();
        if (Environment.GetEnvironmentVariable("KIT_BROKER") is { Length: > 0 } fromEnvironment) options.Broker = ParseBroker(fromEnvironment);
        options.Token = Environment.GetEnvironmentVariable("FULFILMENT_TOKEN");
        options.KitsPath = Environment.GetEnvironmentVariable("KITS_PATH");

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--help" or "-h": options.Help = true; break;
                case "--broker": options.Broker = ParseBroker(Next()); break;
                case "--device":
                    var parts = Next().Split(':', 2);
                    devices.Add((parts[0], parts.Length > 1 ? parts[1] : DefaultSku));
                    break;
                case "--credential":
                    var pair = Next().Split('=', 2);
                    if (pair.Length != 2) throw new ArgumentException("--credential is <deviceId>=<password>");
                    credentials[pair[0]] = pair[1];
                    break;
                case "--provision-from": options.ProvisionFrom = Next(); break;
                case "--order": options.Order = Next(); break;
                case "--token": options.Token = Next(); break;
                case "--kits": options.KitsPath = Next(); break;
                case "--speed": options.Speed = Number(Next(), "--speed"); break;
                case "--interval": options.Interval = Number(Next(), "--interval"); break;
                case "--noise": options.Noise = Number(Next(), "--noise"); break;
                case "--seed": options.Seed = (int)Number(Next(), "--seed"); break;
                case "--initial":
                    foreach (var item in Next().Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = item.Split('=', 2);
                        if (kv.Length != 2 || kv[0] is not ("soil" or "temp" or "humidity" or "tank" or "hour" or "room"))
                            throw new ArgumentException("--initial takes soil, temp, humidity, tank, hour or room, for example soil=22,tank=80");
                        options.Initial[kv[0]] = Number(kv[1], "--initial " + kv[0]);
                    }
                    break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }

        if (options.Speed <= 0 || options.Interval <= 0) throw new ArgumentException("--speed and --interval must be positive");
        foreach (var (id, sku) in devices) options.Devices.Add(new DeviceSpec(id, sku, credentials.GetValueOrDefault(id)));
        if (options.Devices.Count == 0 && options.ProvisionFrom is null && !options.Help) throw new ArgumentException("give a --device or --provision-from");
        options.Credentials = credentials;
        return options;
    }

    internal Dictionary<string, string> Credentials { get; private set; } = [];

    private static BrokerSettings ParseBroker(string value)
    {
        value = value.Replace("mqtt://", "").Replace("tcp://", "").TrimEnd('/');
        var parts = value.Split(':', 2);
        return new BrokerSettings(parts[0], parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1883);
    }

    private static double Number(string value, string name) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : throw new ArgumentException($"{name} needs a number, got '{value}'");

    public PhysicsOptions PhysicsOptions()
    {
        var physics = new PhysicsOptions { Speed = Speed, Noise = Noise };
        foreach (var (key, value) in Initial)
        {
            switch (key)
            {
                case "soil": physics.InitialSoilMoisture = value; break;
                case "temp": physics.InitialTemperature = value; break;
                case "humidity": physics.InitialAirHumidity = value; break;
                case "tank": physics.InitialTankLevel = value; break;
                case "hour": physics.StartHour = value; break;
                case "room": physics.RoomTemperature = value; break;
            }
        }
        return physics;
    }
}

/// <summary>Flashes the emulator with the controllers of an order: what the packing bundle of the fulfilment service says.</summary>
public static class Provisioning
{
    public static async Task<List<DeviceSpec>> FromFulfilmentAsync(string baseUrl, string orderId, string? token, CancellationToken ct)
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/orders/{Uri.EscapeDataString(orderId)}/allocations");
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return [.. document.RootElement.EnumerateArray()
            .Where(a => a.TryGetProperty("state", out var state) && state.GetString() != "retired")
            .Select(a => new DeviceSpec(a.GetProperty("deviceId").GetString()!, a.GetProperty("sku").GetString()!,
                a.TryGetProperty("credential", out var credential) ? credential.GetString() : null))];
    }
}