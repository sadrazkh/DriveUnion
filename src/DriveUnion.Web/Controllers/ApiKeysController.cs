using DriveUnion.Core.Api;
using DriveUnion.Core.Application;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Models;
using DriveUnion.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DriveUnion.Web.Controllers;

/// <summary>
/// «کلیدهای API» — where a customer mints the key their program will use.
///
/// <para>Behind the tenant policy and the cookie, never behind a key: a key that could mint another
/// key is a key that cannot be revoked, because the one you revoke has already made its
/// replacement. Minting is something a person does while signed in.</para>
/// </summary>
[Authorize(Policy = DriveUnionPolicies.Tenant)]
[Route("keys")]
public sealed class ApiKeysController(IApiTokens tokens, IOptions<DriveUnionWebOptions> options) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        SetShell();

        var keys = await tokens.ListAsync(tenantId, cancellationToken);

        return View(new ApiKeysPageViewModel(
            [.. keys.Select(ApiKeyRowViewModel.From)],
            TempData["Notice"] as string,

            // Carried in TempData for exactly one render. It is the only moment the secret exists
            // outside the customer's hands, and it must not survive a refresh — a page that shows a
            // key again on F5 is a key sitting in a browser's history.
            TempData["MintedSecret"] as string,
            BaseUrl()));
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Mint(
        string? name,
        string? scope,
        int? expiresInDays,
        CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();
        if (User.GetUserId() is not { } userId) return Forbid();

        var wanted = scope == nameof(ApiScope.Write) ? ApiScope.Write : ApiScope.Read;

        var expiry = expiresInDays is > 0 and <= 3650
            ? DateTimeOffset.UtcNow.AddDays(expiresInDays.Value)
            : (DateTimeOffset?)null;

        var minted = await tokens.MintAsync(tenantId, userId, name ?? string.Empty, wanted, expiry, cancellationToken);

        TempData["Notice"] = minted.Outcome switch
        {
            ApiTokenOutcome.Done => UiText.ApiKeys.Minted,
            ApiTokenOutcome.NameEmpty => UiText.ApiKeys.NeedsAName,
            ApiTokenOutcome.TooMany => UiText.ApiKeys.TooMany(ApiToken.MaxPerTenant),
            _ => UiText.Files.NotFound,
        };

        if (minted.Minted is { } made) TempData["MintedSecret"] = made.Secret;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetTenantId() is not { } tenantId) return Forbid();

        var revoked = await tokens.RevokeAsync(tenantId, id, cancellationToken);

        TempData["Notice"] = revoked.Succeeded ? UiText.ApiKeys.Revoked : UiText.Files.NotFound;

        return RedirectToAction(nameof(Index));
    }

    private string BaseUrl() =>
        options.Value.PublicBaseUrl is { Length: > 0 } configured
            ? configured
            : $"{Request.Scheme}://{Request.Host}";

    private void SetShell() => ViewData[ShellContext.Key] = new ShellContext
    {
        UserName = User.Identity?.Name,
        UserRole = User.IsOperator() ? UiText.Shell.RoleOperator : UiText.Shell.RoleUser,
    };
}
