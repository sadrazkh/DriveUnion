using DriveUnion.Infrastructure.Identity;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Default is not configured. The panel holds encrypted Google credentials; "
        + "it must not fall back to an implicit or in-memory store.");

builder.Services.AddDbContext<DriveUnionDbContext>(options => options.UseNpgsql(connectionString));

// Keys in the database, not on disk — see DriveUnionDbContext.DataProtectionKeys for why.
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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

app.Run();

/// <summary>Named so the integration tests can boot this exact pipeline in-process.</summary>
public partial class Program;
