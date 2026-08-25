using System.Security.Claims;
using System.Text.Encodings.Web;
using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Security;

/// <summary>
/// <c>Authorization: Bearer du_…</c>, turned into the same principal a cookie produces.
///
/// <para><b>The same claims, deliberately.</b> Every policy, every controller and every call in this
/// product reads the tenant off <c>User</c>; a token that produced a different shape would mean a
/// second way to be authorised and a second place for the isolation rule to be got wrong. What a
/// key adds is one claim of its own — the scope — and everything downstream is unchanged.</para>
///
/// <para><b>No cookie, no antiforgery.</b> A CSRF token defends a credential the browser attaches on
/// its own; a bearer header is attached by the program that holds it and by nothing else. The API
/// controllers say so rather than inheriting the panel's <c>[AutoValidateAntiforgeryToken]</c>, and
/// they are on their own route prefix so the two cannot be confused.</para>
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiTokens tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DriveUnion.ApiKey";

    /// <summary>The scope the presented key carries, as a claim so a policy can read it.</summary>
    public const string ScopeClaim = "drive_union:api_scope";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header)) return AuthenticateResult.NoResult();

        var value = header.ToString();

        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var presented = value["Bearer ".Length..].Trim();
        var caller = await tokens.AuthenticateAsync(presented, Context.RequestAborted);

        // NoResult and not Fail, so a request carrying a dead key falls through to the challenge
        // below and is answered 401 — the same answer as carrying none. Fail would let a handler
        // further along treat «tried and was refused» differently from «did not try», which is a
        // distinction worth nothing here and worth something to somebody probing prefixes.
        if (caller is null) return AuthenticateResult.NoResult();

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, caller.OwnerUserId.ToString()),
            new Claim(DriveUnionClaimTypes.TenantId, caller.TenantId.ToString()),
            new Claim(ScopeClaim, caller.Scope.ToString()),
        ],
        SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>
    /// 401 with a <c>WWW-Authenticate</c>, and never a redirect.
    ///
    /// <para>The cookie scheme answers an unauthenticated request by sending a browser to the
    /// sign-in page. A program calling this API would follow that redirect and be handed an HTML
    /// login form with a 200 on it, which is the least useful possible answer to «your key is not
    /// good».</para>
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Bearer realm=\"{ApiToken.Marker}\"";

        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    }
}

/// <summary>The two policies the API's own routes are behind.</summary>
public static class ApiPolicies
{
    /// <summary>A live key of any scope.</summary>
    public const string Read = "DriveUnion.Api.Read";

    /// <summary>A live key that may change things.</summary>
    public const string Write = "DriveUnion.Api.Write";

    internal static bool HasWrite(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ApiKeyAuthenticationHandler.ScopeClaim) == nameof(ApiScope.Write);
}
