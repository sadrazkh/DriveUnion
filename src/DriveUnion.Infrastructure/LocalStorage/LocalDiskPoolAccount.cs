using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriveUnion.Infrastructure.LocalStorage;

/// <summary>
/// Puts one account in the pool so the local-disk backend can actually be uploaded to.
///
/// <para><b>Why this is needed at all.</b> Enabling the local disk replaces <c>IDriveClient</c> and
/// nothing else, and the upload path does not go looking for a client — it goes looking for an
/// <i>account</i>. <c>SingleAccountUploadTargetSelector</c> wants a <c>GoogleAccount</c> row that is
/// Healthy and has room; <c>IDriveFolders</c> reads that row's <c>RootFolderId</c>; the file it
/// writes records the account it landed in. With no row, every upload is refused with «no connected
/// account can take this file» — which is what the backend did, so the claim that the product can be
/// exercised before a Google Cloud project exists was untrue at the first step anybody would take.
/// </para>
///
/// <para><b>Why a row and not a special case.</b> Teaching the selector, the folder resolver and the
/// catalogue that storage might be account-less means three branches for a development backend, on
/// paths that decide where a customer's bytes go. A row costs one insert and leaves every one of
/// them reading exactly what it reads in production.</para>
///
/// <para>It cannot reach production: <c>AddLocalDiskDrive</c> only registers this when the backend is
/// on, and <c>LocalDiskDriveOptionsValidator</c> refuses to let it be on in Production.</para>
/// </summary>
public sealed class LocalDiskPoolAccount(
    IServiceScopeFactory scopes,
    IOptions<LocalDiskDriveOptions> options,
    TimeProvider clock,
    ILogger<LocalDiskPoolAccount> logger) : IHostedService
{
    /// <summary>
    /// Not an address, and shaped so nobody mistakes it for one. It is what the operator's accounts
    /// screen will show, and it should read as a machine rather than as a mailbox somebody could try
    /// to reconnect.
    /// </summary>
    public const string Email = "local-disk@this-machine.invalid";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();

        if (await db.GoogleAccounts.AnyAsync(a => a.Email == Email, cancellationToken)) return;

        // Only when the pool is otherwise empty. A developer who has connected a real Google account
        // to their local panel is telling us where they want their bytes, and a second account
        // appearing beside it — the disk under their feet — is a router deciding between the two.
        if (await db.GoogleAccounts.AnyAsync(cancellationToken))
        {
            logger.LogInformation(
                "The pool already has an account, so the local-disk backend is not adding one. "
                + "Uploads will be routed by the existing account's own client.");
            return;
        }

        var root = options.Value.RootPath;
        var free = FreeBytes(root);

        db.GoogleAccounts.Add(new GoogleAccount
        {
            Id = Guid.CreateVersion7(),
            Email = Email,
            Label = "L1",
            GoogleUserId = null,

            // There is no grant behind this row and there never will be. The column is required, and
            // a value that cannot be unprotected is the honest one: anything that tries to refresh
            // this account gets "reconnect it" rather than a token that half-works.
            RefreshTokenProtected = "local-disk-has-no-refresh-token",

            Status = GoogleAccountStatus.Healthy,
            QuotaTotalBytes = free,
            QuotaUsedBytes = 0,
            CreatedAt = clock.GetUtcNow(),
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Added the local-disk pool account {Email} with {FreeGb} GB of room at {Root}. "
            + "It exists so uploads have somewhere to be routed; it is not a Google account.",
            Email,
            free / (1024 * 1024 * 1024),
            root);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// What the disk actually has, so the per-file and quota refusals mean something here too.
    /// Falls back to a terabyte when the drive cannot be inspected — a development backend refusing
    /// an upload because it could not read a disk would be the wrong kind of faithful.
    /// </summary>
    private static long FreeBytes(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 1024L * 1024 * 1024 * 1024;
        }
    }
}
