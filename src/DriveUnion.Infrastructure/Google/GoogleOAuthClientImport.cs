using System.Text.Json;
using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// Carries the OAuth client out of <c>App_Data/google-oauth.json</c> and into the database, once.
///
/// <para>That file is the thing this change exists to retire. It lived inside the container, a
/// redeploy deleted it, and every upload afterwards failed with «storage is unavailable» because a
/// refresh needs the client id and secret the file was holding. But a deployment that still has its
/// file must not be asked to re-paste anything, and the one that has already lost it must not be
/// asked twice — so this runs at startup, does nothing at all when there is no file, and leaves a
/// line in the log when there was.</para>
///
/// <para><b>What makes it once.</b> Two guards, because either one alone is wrong. A client id
/// already in the table is not imported again, which covers the ordinary restart. And the file is
/// renamed aside afterwards, which covers the case the first guard cannot: an operator who imports a
/// client and then deliberately removes it from the panel must not find it back after the next
/// restart. The rename is best-effort — a read-only mount refuses it — and its failure is a warning,
/// not a stop.</para>
///
/// <para>The file is renamed and not deleted. It holds the only copy of a credential this process
/// has just moved; if anything about the move was wrong, the operator's evidence should still be on
/// the disk it was on.</para>
/// </summary>
public sealed class GoogleOAuthClientImport : IHostedService
{
    /// <summary>What the old store called its three fields. Read, never written.</summary>
    private const string ClientIdProperty = "clientId";
    private const string ClientSecretProperty = "clientSecretProtected";
    private const string RedirectUriProperty = "redirectUri";

    /// <summary>The suffix the file takes once its contents are rows.</summary>
    public const string ImportedSuffix = ".imported";

    private readonly string _path;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITokenProtector _protector;
    private readonly IGoogleOAuthClientStore _store;
    private readonly ILogger<GoogleOAuthClientImport> _logger;

    public GoogleOAuthClientImport(
        string path,
        IServiceScopeFactory scopeFactory,
        ITokenProtector protector,
        IGoogleOAuthClientStore store,
        ILogger<GoogleOAuthClientImport> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _scopeFactory = scopeFactory;
        _protector = protector;
        _store = store;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Run();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The import, separated from the hosted-service plumbing so a test can run it twice and watch
    /// the second one do nothing.
    /// </summary>
    /// <returns>True when a client was written on this call.</returns>
    public bool Run()
    {
        // Nothing else in here touches the database when there is no file, which is what makes this
        // safe to register unconditionally: a fresh deployment, and every in-process test host, walk
        // out of this method before a query is ever built.
        if (!File.Exists(_path)) return false;

        if (Read() is not { } file) return false;

        var imported = Import(file);

        RenameAside();

        return imported;
    }

    private bool Import(LegacyClient file)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();

        if (db.GoogleOAuthClients.Any(c => c.ClientId == file.ClientId))
        {
            _logger.LogInformation(
                "The Google OAuth client in {Path} is already in the database; nothing was imported.",
                _path);

            return false;
        }

        // Saved through the store so the label allocation and the "first client is the default" rule
        // are the same ones the screen uses, rather than a second copy of them here.
        if (_store.Save(id: null, file.ClientId, file.Secret, file.RedirectUri)
            is not { Outcome: GoogleOAuthClientSave.Saved, Client: { } saved })
        {
            return false;
        }

        _logger.LogInformation(
            "Imported the Google OAuth client from {Path} into the database as {Label}. It survives a "
            + "redeploy now; the file does not, which is why every upload failed after the last one. "
            + "The client secret {SecretState}.",
            _path,
            saved.Label,
            file.Secret is null
                ? "could not be decrypted and has to be pasted again on the accounts screen"
                : "came across intact");

        return true;
    }

    private LegacyClient? Read()
    {
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read must not stop the panel from booting — the accounts screen
            // is where an operator would go to paste the client back in by hand.
            _logger.LogWarning(
                exception,
                "The stored Google OAuth client at {Path} could not be read and was not imported.",
                _path);

            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "The stored Google OAuth client at {Path} is not JSON and was not imported.",
                _path);

            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object) return null;

            if (StringProperty(root, ClientIdProperty) is not { } clientId
                || StringProperty(root, RedirectUriProperty) is not { } redirectUri)
            {
                _logger.LogWarning(
                    "The stored Google OAuth client at {Path} is missing a client id or redirect URI "
                    + "and was not imported.",
                    _path);

                return null;
            }

            // The ciphertext was written by this same protector, under a key ring that lives in the
            // database — so on the deployment this is for, it still decrypts. Decrypting here rather
            // than copying the ciphertext across means the value is re-encrypted under whatever key
            // is current, and it means a secret that cannot be read is reported as absent instead of
            // being carried forward as a row that lies about having one.
            var cipher = StringProperty(root, ClientSecretProperty);
            var secret = cipher is null ? null : _protector.Unprotect(cipher);

            return new LegacyClient(clientId, redirectUri, secret);
        }
    }

    private void RenameAside()
    {
        var moved = _path + ImportedSuffix;

        try
        {
            File.Move(_path, moved, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "The Google OAuth client was imported but {Path} could not be renamed to {Moved}. "
                + "Removing that client from the panel will not stick across a restart until the file "
                + "is gone.",
                _path,
                moved);
        }
    }

    private static string? StringProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
        && element.ValueKind is JsonValueKind.String
        && element.GetString() is { Length: > 0 } value
            ? value
            : null;

    /// <param name="Secret">
    /// Null both when the file carried none and when the one it carried no longer decrypts. Those
    /// are the same situation for the operator: paste it again.
    /// </param>
    private sealed record LegacyClient(string ClientId, string RedirectUri, string? Secret);
}
