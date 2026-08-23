using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.LocalStorage;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Seeding;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Infrastructure.Telegram;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Infrastructure;
using DriveUnion.Web.Security;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Default is not configured. The panel holds encrypted Google credentials; "
        + "it must not fall back to an implicit or in-memory store.");

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
    .AddDefaultTokenProviders();

// Projects TenantId and IsOperator onto the signed-in principal, and registers the first-operator
// seeder. Without it every panel policy fails closed — correctly, and for everybody.
builder.Services.AddDriveUnionIdentity(builder.Configuration);

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

// Telegram identity, account linking and the operator's bot settings. After AddGoogleDrive, which
// registers the ITokenProtector the bot token is encrypted with. No transport yet: the gateway
// registered here reports that nothing was delivered, which is the truth rather than a placeholder.
builder.Services.AddDriveUnionTelegram();

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

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    foreach (var proxy in trustedProxies.Where(p => !string.IsNullOrWhiteSpace(p)))
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy.Trim()));
    }
});

builder.Services.AddControllersWithViews();

var app = builder.Build();

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
