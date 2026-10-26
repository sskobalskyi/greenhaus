using Greenhaus.KitEmulator;

EmulatorOptions options;
try { options = EmulatorOptions.Parse(args); }
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(EmulatorOptions.Usage);
    return 2;
}
if (options.Help) { Console.WriteLine(EmulatorOptions.Usage); return 0; }

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();

static void Log(string device, string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} [{device}] {message}");

var kitsPath = options.KitsPath ?? FindKits() ?? throw new InvalidOperationException("data/kit-capabilities.json not found, give --kits");
var kits = Kits.Load(kitsPath);

if (options.ProvisionFrom is { } url)
{
    if (options.Order is null) { Console.Error.WriteLine("--provision-from needs --order"); return 2; }
    foreach (var spec in await Provisioning.FromFulfilmentAsync(url, options.Order, options.Token, stop.Token))
        options.Devices.Add(spec);
    Console.WriteLine($"{options.Devices.Count} device(s) read from the packing bundle of order {options.Order}");
}

var runs = new List<Task>();
foreach (var (spec, index) in options.Devices.Select((d, i) => (d, i)))
{
    if (!kits.TryGetValue(spec.Sku, out var kit))
    {
        Console.Error.WriteLine($"{spec.DeviceId}: unknown kit {spec.Sku} (known: {string.Join(", ", kits.Keys)})");
        return 2;
    }
    var logger = (string message) => Log(spec.DeviceId, message);
    var physics = new PlantPhysics(options.PhysicsOptions(), options.Seed + index);
    var sink = new MqttSink();
    var controller = new KitController(kit, spec.DeviceId, physics, TimeProvider.System, sink,
        new ControllerOptions { TelemetryInterval = TimeSpan.FromSeconds(options.Interval) }, logger);
    var device = new MqttDevice(controller, sink, spec, options.Broker, logger);
    logger($"{kit.Model} ({spec.Sku}), speed x{options.Speed}, soil {physics.SoilMoisture:0.#} %, tank {physics.TankLevel:0.#} %");
    runs.Add(device.RunAsync(stop.Token));
}

await Task.WhenAll(runs);
return 0;

static string? FindKits()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        var path = Path.Combine(directory.FullName, "data", "kit-capabilities.json");
        if (File.Exists(path)) return path;
    }
    return File.Exists("/data/kit-capabilities.json") ? "/data/kit-capabilities.json" : null;
}