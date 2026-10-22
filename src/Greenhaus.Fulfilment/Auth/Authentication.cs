using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Greenhaus.Fulfilment.Auth;

public sealed class AuthOptions
{
    /// <summary>`Keycloak` validates bearer tokens. `Development` trusts the X-User-Id header and is for local use only.</summary>
    public string Mode { get; set; } = "Keycloak";
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    /// <summary>The issuer tokens carry, when it differs from the authority the service reaches Keycloak at (inside Docker, for example).</summary>
    public string? Issuer { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
    /// <summary>The realm role that may read the provisioning bundle of an order, the people who pack devices.</summary>
    public string AdminRole { get; set; } = "fulfilment-admin";
}

/// <summary>
/// Local development without Keycloak: whoever the X-User-Id header names is the caller, and without the header it is the demo
/// user. The X-Roles header (comma separated) sets the roles, and without it the caller is a fulfilment admin, so the packing
/// API works on a laptop. Registered only when Auth:Mode is Development, and the startup log says so.
/// </summary>
public sealed class DevelopmentAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IOptions<AuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";
    public const string UserHeader = "X-User-Id";
    public const string RolesHeader = "X-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[UserHeader].FirstOrDefault() is { Length: > 0 } header ? header : "demo-user";
        var roles = Request.Headers[RolesHeader].FirstOrDefault() is { } given
            ? given.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [auth.Value.AdminRole];
        var claims = new List<Claim> { new("sub", user), new(ClaimTypes.NameIdentifier, user), new("preferred_username", user) };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}

public static class UserExtensions
{
    /// <summary>True for a caller with the admin role, as a plain role claim or in Keycloak's realm_access.roles.</summary>
    public static bool IsFulfilmentAdmin(this ClaimsPrincipal user, string role)
    {
        if (user.Claims.Any(c => c.Type is "role" or "roles" or ClaimTypes.Role && c.Value == role)) return true;
        foreach (var claim in user.FindAll("realm_access"))
        {
            try
            {
                using var json = JsonDocument.Parse(claim.Value);
                if (json.RootElement.TryGetProperty("roles", out var roles) && roles.EnumerateArray().Any(r => r.GetString() == role)) return true;
            }
            catch (JsonException) { /* not a Keycloak realm_access claim */ }
        }
        return false;
    }
}