using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Security;

/// <summary>
/// Authenticates a caller by the API key in its Authorization header.
/// </summary>
/// <remarks>
/// Registered as a scheme rather than as middleware so a host's own authentication keeps working
/// unchanged: a browser session still signs a person in, and this only applies where an endpoint asks for
/// it. A key carries whatever claims it was given — a service identity — and those decide what it can
/// retrieve, exactly as a person's claims would.
/// </remarks>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyService keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "NetCoreAI.ApiKey";

    /// <summary>Claim carrying the key's id, so a run can be traced back to the credential that caused it.</summary>
    public const string KeyIdClaim = "netcoreai:key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || header.Parameter is not { Length: > 0 } secret)
        {
            // No key presented is not a failure: another scheme may sign this caller in.
            return AuthenticateResult.NoResult();
        }

        var result = await keys.AuthenticateAsync(secret, Context.Connection.RemoteIpAddress, Context.RequestAborted).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            if (result.Failure == ApiKeyFailure.RateLimited && result.RetryAfterSeconds is { } retry)
            {
                // Told rather than left to guess: a client that knows when to retry stops hammering.
                Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return AuthenticateResult.Fail(Describe(result.Failure));
        }

        var key = result.Key!;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, key.Name),
            new(ClaimTypes.NameIdentifier, key.Id),
            new(KeyIdClaim, key.Id),
        };

        foreach (var claim in key.Claims)
        {
            var parts = claim.Split('=', 2);
            if (parts.Length == 2 && parts[0].Length > 0)
            {
                claims.Add(new Claim(parts[0], parts[1]));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        Context.Items[nameof(ApiKey)] = key;
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private static string Describe(ApiKeyFailure failure) => failure switch
    {
        ApiKeyFailure.Disabled => "This API key has been disabled.",
        ApiKeyFailure.Expired => "This API key has expired.",
        ApiKeyFailure.AddressNotAllowed => "This API key may not be used from this address.",
        ApiKeyFailure.RateLimited => "This API key has made too many requests. Try again shortly.",

        // Deliberately vague: distinguishing "no such key" from "wrong key" tells a guesser which half of
        // their guess was right.
        _ => "The API key was not recognised.",
    };
}

/// <summary>Registering API key authentication.</summary>
public static class ApiKeyAuthenticationExtensions
{
    /// <summary>
    /// Adds the API key scheme, so another application can reach this host's API with a key issued here.
    /// </summary>
    public static AuthenticationBuilder AddNetCoreAIApiKey(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
    }

    /// <summary>The key a request was authenticated with, when it was authenticated with one.</summary>
    public static ApiKey? ApiKey(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(nameof(NetCoreAI.ApiKey), out var key) ? key as ApiKey : null;
    }
}
