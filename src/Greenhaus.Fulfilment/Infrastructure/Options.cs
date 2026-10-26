namespace Greenhaus.Fulfilment.Infrastructure;

public sealed class FulfilmentOptions
{
    /// <summary>The kit descriptions: for each SKU the model, channels and actuators.</summary>
    public string KitsPath { get; set; } = "data/kit-capabilities.json";
    /// <summary>How long a claim token works. A device that sits in a warehouse for longer needs a new token.</summary>
    public int TokenLifetimeDays { get; set; } = 90;
    /// <summary>How often the worker looks for devices Pulse still has to hear about.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>The wait after the first failed call to Pulse. It doubles with every attempt up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>After this many failed calls a device is shown as pulse_failed and waits for someone to retry it.</summary>
    public int MaxAttempts { get; set; } = 8;
    /// <summary>Base64 of the 32-byte key that seals the claim tokens and device passwords in the database. Required outside development.</summary>
    public string? SecretsKey { get; set; }
}

public sealed class WebOptions
{
    /// <summary>The Greenhaus web app, where the claim page lives. The QR or link on the box points there.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5174";
}

public sealed class PulseOptions
{
    /// <summary>Base address of the Pulse device API, for example http://pulse:8080/api/pulse/v2.</summary>
    public string? BaseUrl { get; set; }
    /// <summary>A fixed bearer token, for local runs. Otherwise the service uses client credentials.</summary>
    public string? StaticToken { get; set; }
    public string? TokenUrl { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}