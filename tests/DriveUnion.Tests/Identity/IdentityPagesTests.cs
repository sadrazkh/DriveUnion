using System.Net;
using DriveUnion.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriveUnion.Tests.Identity;

/// <summary>
/// Half of "the panel is unreachable" was that the challenge went to a page nobody had written.
/// This boots the real pipeline and asks for the sign-in address, because area routing and area
/// view resolution are conventions — <c>/Views/Shared/{0}.cshtml</c> being the last area view
/// location is what lets these pages use the panel's own layout — and a convention that is nearly
/// right renders a 404 that reads like a missing controller.
/// </summary>
public class IdentityPagesTests
{
    [Fact]
    public async Task The_sign_in_page_is_served_at_the_address_the_cookie_handler_points_at()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient();

        using var response = await client.GetAsync(new Uri("/Identity/Account/Login", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("name=\"Email\"");
        html.Should().Contain("name=\"Password\"");

        // The shared layout, not a second one: the brand mark only exists in Views/Shared/_Layout.
        html.Should().Contain("brand-mark");

        // M1 has no sign-up (spec §12), so the page must not offer one.
        html.Should().NotContain("/Identity/Account/Register");
    }

    [Fact]
    public async Task Signing_out_is_a_form_and_not_a_link()
    {
        using var harness = new IdentityPagesHarness();
        using var client = harness.NewClient();

        // Anonymous, so the sign-out page is behind the same authentication everything else is.
        // Without ConfigureApplicationCookie it challenges to Identity's default path; either way
        // it must not be a page that ends a session for whoever happens to load an image.
        using var response = await client.GetAsync(new Uri("/Identity/Account/Logout", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }
}

/// <summary>
/// DriveUnion.Web's real pipeline with the Npgsql context swapped for SQLite in memory. Nothing
/// here reaches Google or Postgres; the connection string exists only because Program.cs refuses to
/// start without one.
/// </summary>
public sealed class IdentityPagesHarness : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection;

    public IdentityPagesHarness()
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        using var schema = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connection).Options);

        // Includes DataProtectionKeys — the antiforgery token on the sign-in form is protected with
        // that key ring, so a missing table is a 500 on the one page that has to work.
        schema.Database.EnsureCreated();
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        // UseSetting rather than ConfigureAppConfiguration: under the minimal hosting model the
        // latter runs after Program.cs has already read the connection string on its second line.
        builder.UseSetting("ConnectionStrings:Default", "Host=unreachable.invalid;Database=unused");

        builder.ConfigureTestServices(services =>
        {
            // AddDbContext leaves the context, DbContextOptions, DbContextOptions<T> and EF 9's
            // IDbContextOptionsConfiguration<T> behind, and the last still carries UseNpgsql —
            // stacking UseSqlite on a surviving provider throws at resolve time.
            var doomed = services
                .Where(d => d.ServiceType == typeof(DriveUnionDbContext)
                    || d.ServiceType == typeof(DbContextOptions)
                    || (d.ServiceType.IsGenericType
                        && d.ServiceType.GetGenericArguments().Contains(typeof(DriveUnionDbContext))))
                .ToList();

            foreach (var descriptor in doomed) services.Remove(descriptor);

            services.AddDbContext<DriveUnionDbContext>(options => options.UseSqlite(connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing) connection.Dispose();
    }
}
