using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Greenhaus.Api.Auth;

public sealed class AuthOptions
{
    /// <summary>`Keycloak` validates bearer tokens. `Development` trusts the X-User-Id header and is for local use only.</summary>
    public string Mode { get; set; } = "Keycloak";
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>
/// Local development without Keycloak: whoever the X-User-Id header names is the caller, and without the header it is
/// the demo user. It is registered only when Auth:Mode is Development and the startup log says so.
/// </summary>
public sealed class DevelopmentAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";
    public const string Header = "X-User-Id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[Header].FirstOrDefault() is { Length: > 0 } header ? header : "demo-user";
        var identity = new ClaimsIdentity(
            [new Claim("sub", user), new Claim(ClaimTypes.NameIdentifier, user), new Claim("preferred_username", user), new Claim("email", $"{user}@greenhaus.local")],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public static class UserExtensions
{
    /// <summary>The Keycloak subject of the caller, which scopes everything they can see.</summary>
    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The token has no subject.");
}