using DriveUnion.Infrastructure.Dashboard;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.LocalStorage;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Plans;
using DriveUnion.Infrastructure.S3;
using DriveUnion.Infrastructure.Seeding;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Infrastructure.Telegram;
using DriveUnion.Infrastructure.Tenancy;
using DriveUnion.Infrastructure.Trash;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Localization;
using DriveUnion.Web.Security;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Three spellings, because the panel is configured by hand locally and by a platform in production.
// Attaching Postgres on Harbora writes ConnectionStrings__DefaultConnection and DATABASE_DSN into
// the environment; this app spells its key "Default", and reading all three here is cheaper than a
// deployment that boots, fails its first query, and dies at the health check.
//
// DATABASE_URL is deliberately absent from the list. It is a URI, and every ADO.NET provider parses
// keyword=value only — an app handed the URI starts and then throws a driver error that names
// neither the database nor the attachment that was meant to supply it.
var connectionString =
    builder.Configuration.GetConnectionString("Default")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration["DATABASE_DSN"]
    ?? throw new InvalidOperationException(
        "No database connection string. Set ConnectionStrings:Default — in user-secrets for local "
        + "work — or attach a Postgres database so ConnectionStrings__DefaultConnection or "
        + "DATABASE_DSN is present. The panel holds encrypted Google credentials; it must not fall "
        + "back to an implicit or in-memory store.");

builder.Services.AddDbContext<DriveUnionDbContext>(options => options.UseNpgsql(connectionString));

// Keys in the database, not on disk — see DriveUnionDbContext.DataProtectionKeys for why. This has
// to come before AddGoogleDrive: the token protector encrypts refresh tokens with this key ring.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<DriveUnionDbContext>()
    .SetApplicationName("DriveUnion");

builder.Services
    .AddIdentity<AppUser, IdentityRole<Guid>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
    })
    .AddEntityFrameworkStores<DriveUnionDbContext>()
    // Identity writes its own refusals — "must be at least 10 characters" — and they were the only
    // English left inside a Persian page. The describer translates them and takes every error Code
    // from base so the two cannot drift apart.
    .AddErrorDescriber<DriveUnionIdentityErrorDescriber>()
    .AddDefaultTokenProviders();

// Projects TenantId and IsOperator onto the signed-in principal, and registers the first-operator
// seeder. Without it every panel policy fails closed — correctly, and for everybody.
builder.Services.AddDriveUnionIdentity(builder.Configuration);

// The API's bearer scheme, added beside Identity's cookie rather than replacing anything.
//
// It is not the default scheme and must not become one: the default is what an unauthenticated
// request is challenged with, and the cookie's challenge is a redirect to the sign-in page. Every
// /api/v1 policy names this scheme explicitly, so a browser session cannot reach those routes and
// a key cannot reach the panel's.
builder.Services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.LogoutPath = "/Identity/Account/Logout";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
});

// The Drive client, the OAuth token service and the account directory. It has no ValidateOnStart on
// purpose: the panel boots without Google credentials, and only connecting an account fails.
builder.Services.AddGoogleDrive(builder.Configuration);

// A development substitute for Google Drive: files on this box's disk, so the whole product —
// upload, link, public page, streamed download — can be exercised before a Google Cloud project
// exists. Off unless DriveUnion:LocalDisk:Enabled, and it refuses to start in Production.
//
// It must come after AddGoogleDrive: enabling it removes the Google registration and takes its
// place, rather than shadowing it, so two clients and a resolution order nobody re-reads can never
// be what decides where a customer's file went.
builder.Services.AddLocalDiskDrive(builder.Configuration);

// The application layer — file catalogue, uploads, share links, and the public reader.
builder.Services.AddDriveUnionServices();

// Plans, the per-file cap and the tenant's storage numbers. It replaces the standalone default
// quota setting rather than sitting beside it — two sources for one number is one of them wrong.
builder.Services.AddDriveUnionPlans();

// The operator's workspaces and the people in them — the screens that replaced "set four
// environment variables and redeploy". After AddDriveUnionPlans, because creating a workspace gives
// it a tier through ITenantPlanService.
//
// It also sets SecurityStampValidatorOptions.ValidationInterval to zero, which is the difference
// between "disabled" and "disabled within the next half hour": the stamp is compared on every
// authenticated request instead of twice an hour. That is one indexed lookup per signed-in request,
// and it is the same price already paid for reading role and tenant from the database rather than
// trusting a cookie.
builder.Services.AddDriveUnionTenancy();

// The trash. Delete used to stamp a column and stop: the bytes stayed in the operator's Drive for
// ever and the customer's usage never came down, so a full plan could not be emptied. This gives
// the catalogue somewhere to move a file to, and the operator settings row the retention comes from.
// After AddDriveUnionServices, which registers the catalogue this hands a trash to.
builder.Services.AddDriveUnionTrash();

// The purge loop, and separate from the line above for the reason the Telegram pair below documents:
// every in-process test host boots this pipeline over one shared SQLite connection, and a background
// loop opening scopes against it turns unrelated suites into "database is locked".
//
// Without this line nothing is ever purged — files wait in the trash for ever and no space is
// returned, which is a quieter version of the bug this phase set out to fix.
builder.Services.AddDriveUnionTrashSweeper();

// The trash screen's own reader, plus the capacity card the layout draws for a tenant. After
// AddDriveUnionTrash, whose ITrash it reads, and AddDriveUnionPlans, whose figures it puts beside it.
builder.Services.AddDriveUnionTrashPanel();

// The S3 gateway's staging sweeper. Separate from the gateway itself for the reason the trash
// sweeper and the Telegram drainer are separate: every in-process test host boots this pipeline
// over one shared SQLite connection, and a background loop opening scopes against it turns
// unrelated suites into «database is locked». Without this line an abandoned multipart upload's
// parts sit on the operator's volume until somebody notices.
builder.Services.AddDriveUnionS3Sweeper();

// The two dashboards behind «/». After the three lines above and AddGoogleDrive: the customer's
// reader is built on ITenantPlanService and ITrash, the operator's on IGoogleAccountDirectory, and a
// dashboard assembled from services nobody registered fails on the panel's home page rather than at
// start-up — which is where this exact line being absent put it.
builder.Services.AddDriveUnionDashboard();

// Telegram identity, account linking and the operator's bot settings. After AddGoogleDrive, which
// registers the ITokenProtector the bot token is encrypted with. No transport yet: the gateway
// registered here reports that nothing was delivered, which is the truth rather than a placeholder.
builder.Services.AddDriveUnionTelegram();

// The drainer, the update poller and the work-directory sweeper. Separate from the line above on
// purpose: every in-process test host calls that one, and a drainer opening scopes against a shared
// SQLite connection turns unrelated suites into "database is locked". Without this line the queue
// never drains and the bot answers nothing.
builder.Services.AddDriveUnionTelegramTransport();

// Authorization policies and the rate limiter for /d/*.
builder.Services.AddDriveUnionWeb();

// /design is operator-only unless a deployment deliberately opens it — see DesignController. The
// flag is read here because the policy depends on configuration and the security file must not.
var designGuideIsPublic = builder.Configuration.GetValue("DriveUnion:PublicDesignGuide", false);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(DriveUnionPolicies.DesignGuide, policy => policy.RequireAssertion(context =>
        designGuideIsPublic
        || context.User.HasClaim(DriveUnionClaimTypes.Operator, DriveUnionClaimTypes.OperatorValue)));

// Resolves hashed Vite bundles for Razor. A singleton because the manifest is read from disk once
// and every view @injects it — without this registration every page throws before it renders a byte.
builder.Services.AddSingleton<ViteManifest>();

// Behind the OVH proxy every visitor arrives from the proxy's address. Untreated, that collapses the
// /d/* rate limiter into one partition and makes every download event look like the same party — the
// limiter would throttle the world together and the owner's analytics would read "400 pulls, one
// visitor" for a link that went out to four hundred people.
//
// Trust is opt-in through DriveUnion:TrustedProxies. With nothing configured the framework's default
// stands (loopback only), because a box reachable directly must not let a caller pick its own
// X-Forwarded-For and step around the limiter it was rate-limited by.
var trustedProxies = builder.Configuration
    .GetSection("DriveUnion:TrustedProxies")
    .Get<string[]>() ?? [];

// ASP.NET trusts a forwarding proxy only on loopback by default. On a platform like Harbora the
// proxy sits on a container network, so the headers are dropped — and nothing reports it. What is
// seen instead is a panel served over https whose Request.Scheme is http, every visitor arriving
// from the proxy's own address, the /d/* rate limiter putting the whole internet in one bucket, and
// the download log recording four hundred different people as one party.
//
// Off unless asked for. Turning it on clears the loopback-only defaults and takes the headers from
// whatever is in front, which is safe exactly while nothing can reach the container except the
// platform's proxy. That is the arrangement here, and it is written down rather than assumed —
// the day it stops being true, a client can name its own address.
//
// A named list wins when there is one, because naming the proxy is stricter than trusting any.
var trustAnyProxy = builder.Configuration.GetValue("DriveUnion:TrustAnyProxy", false);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // X-Forwarded-Host is deliberately not among these. The host arrives intact already, and
    // honouring the header would let whatever is in front rewrite the address this panel believes
    // it lives at — including the redirect URI it tells the operator to register with Google.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    var named = trustedProxies.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    if (named.Count > 0)
    {
        foreach (var proxy in named) options.KnownProxies.Add(IPAddress.Parse(proxy.Trim()));
    }
    else if (trustAnyProxy)
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

// Persian by default, English on request. Only CurrentUICulture ever varies: letting fa-IR onto the
// thread would swap the decimal point in every byte size and quota figure an operator copies into a
// support ticket.
builder.Services.AddDriveUnionLocalization();

builder.Services.AddControllersWithViews();

var app = builder.Build();

// Bring the schema up before anything reads or writes it.
//
// Harbora has no release-command hook, so a deploy is the container starting and nothing else. This
// is what the alternative looks like, and it has already happened once here: the panel booted three
// migrations behind, came up apparently fine, and the first symptom was a background drainer
// looping on `relation "TelegramOutbox" does not exist` — a message that names a table nobody was
// thinking about rather than the migration that was never applied.
//
// It runs ahead of SeedDriveUnionAsync, which writes to AspNetUsers and so needs the tables to be
// there. It also runs in Development, which is exactly where the three-migrations-behind boot came
// from.
//
// The caveat, recorded rather than guarded: this races if the app is ever scaled past one replica.
// EF applies migrations under a lock, so two instances serialise rather than corrupt the schema —
// but a second replica is the moment to make this a deliberate step instead of a side effect of
// starting.
await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>().Database;

    // Only against the provider these migrations were written for. The HTTP tests boot this exact
    // pipeline with SQLite substituted, and EF builds a different model for a different provider —
    // column types and default-value SQL do not match the Npgsql snapshot, so a migrate there fails
    // with PendingModelChangesWarning claiming somebody forgot a migration. Nobody had; it is the
    // wrong database. Those tests create their schema themselves.
    if (database.IsNpgsql() && (await database.GetPendingMigrationsAsync()).ToList() is { Count: > 0 } pending)
    {
        app.Logger.LogInformation(
            "Applying {Count} pending migration(s): {Migrations}",
            pending.Count,
            string.Join(", ", pending));

        await database.MigrateAsync();
    }
}

// Creates the first operator from configuration when there is none. Idempotent, and a no-op when
// nothing is configured — the password comes from user-secrets or the environment, never a file.
await app.SeedDriveUnionAsync();

// Before everything that reads an address — routing, the rate limiter, and the download recorder.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// Before routing, so every endpoint — including the anonymous ones — runs with a culture already
// resolved rather than whatever the thread happened to carry.
app.UseRequestLocalization();

app.UseRouting();

// After UseRouting, so the limiter can see which endpoint — and therefore which policy — a request
// resolved to. Before authentication, because /d/* is anonymous and must be throttled regardless.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute("areas", "{area:exists}/{controller=Home}/{action=Index}/{id?}");
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

app.Run();

/// <summary>Named so the integration tests can boot this exact pipeline in-process.</summary>
public partial class Program;
