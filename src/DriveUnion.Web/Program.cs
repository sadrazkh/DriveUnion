using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Web.Hosting;
using DriveUnion.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
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

// The Drive client, the OAuth token service and the account directory. It has no ValidateOnStart on
// purpose: the panel boots without Google credentials, and only connecting an account fails.
builder.Services.AddGoogleDrive(builder.Configuration);

// The application layer — file catalogue, uploads, share links, and the public reader.
builder.Services.AddDriveUnionServices();

// Authorization policies and the rate limiter for /d/*.
builder.Services.AddDriveUnionWeb();

// Resolves hashed Vite bundles for Razor. A singleton because the manifest is read from disk once
// and every view @injects it — without this registration every page throws before it renders a byte.
builder.Services.AddSingleton<ViteManifest>();

builder.Services.AddControllersWithViews();

var app = builder.Build();

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

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

app.Run();

/// <summary>Named so the integration tests can boot this exact pipeline in-process.</summary>
public partial class Program;
