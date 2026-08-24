using System.Reflection;
using DriveUnion.Core.Application;
using DriveUnion.Web.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DriveUnion.Tests.Telegram;

/// <summary>
/// The webhook endpoint, classified.
///
/// <para>It is the product's fourth anonymous surface after the public landing page, the public
/// stream and the OAuth callback, and the design asks for it to be on an explicit, commented
/// allow-list rather than to be anonymous by omission. The generated route audit those three live
/// under belongs to a milestone that has not landed; this stands in for it and is deliberately narrow
/// — it asserts the properties of this endpoint rather than enumerating every route in the product,
/// so another slice adding a screen does not turn it red for no reason.</para>
/// </summary>
public class TelegramWebhookRouteTests
{
    private static readonly MethodInfo Receive =
        typeof(TelegramWebhookController).GetMethod(nameof(TelegramWebhookController.Receive))!;

    [Fact]
    public void The_endpoint_is_anonymous_on_purpose_and_says_so()
    {
        // Anonymous by declaration, not by omission. Every Telegram update arrives with no cookie,
        // no principal and no tenant, so there is nothing to authorise — and an endpoint that got
        // there by nobody having thought about it is the one that acquires a policy by accident and
        // starts refusing every update in production.
        typeof(TelegramWebhookController)
            .GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().NotBeNull();

        typeof(TelegramWebhookController)
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().BeNull();
    }

    [Fact]
    public void It_takes_no_tenant_and_no_user()
    {
        // The tenant is resolved from the sender through the one reader that turns a chat into a
        // tenant, and never from anything in the request. A parameter here would be a tenant chosen
        // by whoever sent the POST.
        Receive.GetParameters()
            .Select(p => p.ParameterType)
            .Should().NotContain(typeof(Guid));

        Receive.GetParameters()
            .Should().NotContain(p => p.Name!.Contains("tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_body_is_bounded()
    {
        // An update carries metadata and never file contents, so a few hundred kilobytes is generous
        // by two orders of magnitude. Without it this is an anonymous unbounded POST.
        var limit = Receive.GetCustomAttribute<RequestSizeLimitAttribute>();

        limit.Should().NotBeNull();
        TelegramWebhookController.MaxBodyBytes.Should().Be(256 * 1024);
    }

    [Fact]
    public void It_answers_only_posts()
    {
        Receive.GetCustomAttribute<HttpPostAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void It_names_the_limiter_policy_it_wants()
    {
        // The policy table is registered in a file this slice does not own, so the endpoint carries
        // the name and the deployment notes carry the one line that adds it. Naming it here is what
        // keeps "the webhook is rate limited" from being an assumption nobody can check.
        TelegramWebhookController.RateLimitPolicy.Should().Be("DriveUnion.TelegramWebhook");
    }

    [Fact]
    public void The_only_seam_it_reaches_through_is_the_update_handler()
    {
        // Nothing tenant-scoped is injected here. The endpoint authenticates, parses and hands over;
        // every decision about whose files a chat may read is behind ITelegramUpdateHandler, where
        // there is one place that turns a sender id into a tenant.
        var injected = typeof(TelegramWebhookController)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToList();

        injected.Should().Contain(typeof(ITelegramUpdateHandler));
        injected.Should().NotContain(typeof(IFileCatalog));
        injected.Should().NotContain(typeof(IShareLinkService));
        injected.Should().NotContain(typeof(IUploadCoordinator));
    }
}
