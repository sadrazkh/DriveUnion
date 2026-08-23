using System.Text.Json;
using DriveUnion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// The OAuth client the operator typed into the panel — everything about it except the secret.
///
/// There is deliberately no way to get the secret out of this record. The panel renders it, and a
/// screen that could print the secret is a screen that eventually will: into an HTML source view, a
/// browser cache, a bug report screenshot. <see cref="HasClientSecret"/> is the only thing the
/// browser ever learns, and <see cref="IGoogleOAuthCredentialStore.ReadClientSecret"/> — the one way
/// back to the plaintext — is reached only by the two requests that go to Google.
/// </summary>
/// <param name="HasClientSecret">
/// True only when a stored secret is present <em>and</em> still decrypts. A secret written under a
/// Data Protection key that has since been lost is reported as absent on purpose: the operator's
/// only fix is to paste it again, and a screen that claimed the secret was set would send them
/// hunting through Google Cloud for a fault that is on this side.
/// </param>
public sealed record StoredGoogleOAuthClient(
    string ClientId,
    string RedirectUri,
    bool HasClientSecret,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Where the client id, client secret and redirect URI typed into the panel are kept.
///
/// This exists because the owner has no terminal on the box. Google will not issue a token without a
/// client id — that part is Google's rule — but needing <c>dotnet user-secrets</c> to supply one was
/// ours, and this is the seam that removes it.
/// </summary>
public interface IGoogleOAuthCredentialStore
{
    /// <summary>What the panel may see. Null when the operator has saved nothing.</summary>
    StoredGoogleOAuthClient? Read();

    /// <summary>
    /// The secret in the clear, for the authorization-code exchange and the refresh that are the
    /// only two things that need it. Null when none is stored, or when the stored one no longer
    /// decrypts.
    /// </summary>
    string? ReadClientSecret();

    /// <summary>
    /// Persists the client. A null or empty <paramref name="clientSecret"/> keeps the secret already
    /// stored, which is what makes "correct a typo in the client id" possible without asking the
    /// operator to fetch the secret out of Google Cloud again.
    /// </summary>
    StoredGoogleOAuthClient Save(string clientId, string? clientSecret, string redirectUri);

    /// <summary>Forgets the stored client. False when there was nothing to forget.</summary>
    bool Clear();
}

/// <summary>
/// The stored OAuth client, in one JSON file, with the secret encrypted by the same
/// <see cref="ITokenProtector"/> that protects the Google refresh tokens.
///
/// <para><b>Why a file and not a row.</b> This model has no table that can hold an
/// application-level setting, and adding one was out of scope for the change that introduced this.
/// The consequence is worth stating plainly: the Data Protection key ring lives in the database and
/// survives a redeploy, but <em>this file does not</em> unless the deployment keeps it on a volume.
/// Losing it costs the operator one re-paste of two strings on the screen that teaches them where to
/// find those strings — recoverable, and visible, unlike the silent token orphaning that put the key
/// ring in the database. A <c>GoogleOAuthClient</c> single-row table would remove even that.</para>
///
/// <para>The cache is held for the life of the process because this process is the only writer.
/// Hand-editing the file while the panel is running is not a supported way to change it — the
/// screen is.</para>
/// </summary>
public sealed class FileGoogleOAuthCredentialStore : IGoogleOAuthCredentialStore
{
    private const string ClientIdProperty = "clientId";
    private const string ClientSecretProperty = "clientSecretProtected";
    private const string RedirectUriProperty = "redirectUri";
    private const string UpdatedAtProperty = "updatedAt";

    private readonly string _path;
    private readonly ITokenProtector _protector;
    private readonly ILogger<FileGoogleOAuthCredentialStore> _logger;
    private readonly Lock _gate = new();

    private Entry? _entry;
    private bool _loaded;

    public FileGoogleOAuthCredentialStore(
        string path,
        ITokenProtector protector,
        ILogger<FileGoogleOAuthCredentialStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>Where this store writes. Shown to nobody; used by the tests that pin the format.</summary>
    public string FilePath => _path;

    public StoredGoogleOAuthClient? Read()
    {
        var entry = Load();

        return entry is null
            ? null
            : new StoredGoogleOAuthClient(
                entry.ClientId,
                entry.RedirectUri,
                entry.Secret is not null,
                entry.UpdatedAt);
    }

    public string? ReadClientSecret() => Load()?.Secret;

    public StoredGoogleOAuthClient Save(string clientId, string? clientSecret, string redirectUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        lock (_gate)
        {
            var existing = LoadLocked();

            // Re-protecting the surviving secret rather than copying its ciphertext across is
            // deliberate: it re-encrypts under whatever key is current, so a value written years ago
            // is quietly carried forward every time the operator touches this form.
            var secret = string.IsNullOrEmpty(clientSecret) ? existing?.Secret : clientSecret;
            var protectedSecret = secret is null ? null : _protector.Protect(secret);

            var entry = new Entry(clientId, redirectUri, secret, DateTimeOffset.UtcNow);

            Write(entry, protectedSecret);

            _entry = entry;
            _loaded = true;

            // No values, not even the client id. This line exists to make "the credentials changed"
            // findable in a log; nothing about it should ever need the credentials themselves.
            _logger.LogInformation("The Google OAuth client was updated from the panel.");

            return new StoredGoogleOAuthClient(
                entry.ClientId,
                entry.RedirectUri,
                entry.Secret is not null,
                entry.UpdatedAt);
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            _entry = null;
            _loaded = true;

            if (!File.Exists(_path)) return false;

            File.Delete(_path);
            _logger.LogInformation("The stored Google OAuth client was removed from the panel.");

            return true;
        }
    }

    private Entry? Load()
    {
        lock (_gate)
        {
            return LoadLocked();
        }
    }

    private Entry? LoadLocked()
    {
        if (_loaded) return _entry;

        _loaded = true;
        _entry = ReadFile();

        return _entry;
    }

    private Entry? ReadFile()
    {
        string text;
        try
        {
            if (!File.Exists(_path)) return null;

            text = File.ReadAllText(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A store that cannot be read must not take the accounts screen down with it — that
            // screen is where the operator would go to fix this.
            _logger.LogWarning(
                exception,
                "The stored Google OAuth client at {Path} could not be read. The panel will behave "
                + "as though nothing has been saved.",
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
                "The stored Google OAuth client at {Path} is not JSON and was ignored.",
                _path);
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object) return null;

            var clientId = StringProperty(root, ClientIdProperty);
            var redirectUri = StringProperty(root, RedirectUriProperty);

            if (clientId is null || redirectUri is null)
            {
                _logger.LogWarning(
                    "The stored Google OAuth client at {Path} is missing a client id or redirect URI "
                    + "and was ignored.",
                    _path);
                return null;
            }

            var cipher = StringProperty(root, ClientSecretProperty);

            // Unprotect returns null for a key that is gone rather than throwing, and this is where
            // that matters: the panel keeps rendering, the screen says the secret is not set, and
            // the operator's next action — paste it again — is the correct one.
            var secret = cipher is null ? null : _protector.Unprotect(cipher);

            var updatedAt = root.TryGetProperty(UpdatedAtProperty, out var stamp)
                && stamp.ValueKind is JsonValueKind.String
                && stamp.TryGetDateTimeOffset(out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow;

            return new Entry(clientId, redirectUri, secret, updatedAt);
        }
    }

    private static string? StringProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
        && element.ValueKind is JsonValueKind.String
        && element.GetString() is { Length: > 0 } value
            ? value
            : null;

    private void Write(Entry entry, string? protectedSecret)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(ClientIdProperty, entry.ClientId);
            writer.WriteString(RedirectUriProperty, entry.RedirectUri);

            if (protectedSecret is not null)
            {
                writer.WriteString(ClientSecretProperty, protectedSecret);
            }

            writer.WriteString(UpdatedAtProperty, entry.UpdatedAt);
            writer.WriteEndObject();
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written beside the target and renamed over it. A half-written file here is a panel that
        // comes up with no Google client and no explanation, and rename is the one filesystem
        // operation that cannot leave one behind.
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, buffer.ToArray());
        RestrictToOwner(temporary);
        File.Move(temporary, _path, overwrite: true);
    }

    private static void RestrictToOwner(string path)
    {
        // The secret in this file is ciphertext, but the client id beside it names the operator's
        // Google project, and no other account on the box has any reason to read either.
        //
        // Windows has no file mode to set — a new file inherits the directory's ACL — and calling
        // this there throws. The panel's home is Linux; this guard is for the development machine.
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// The decrypted view of the file. <see cref="Secret"/> is null both when none was stored and
    /// when the stored one no longer decrypts — the two are the same situation for every caller.
    /// </summary>
    private sealed record Entry(string ClientId, string RedirectUri, string? Secret, DateTimeOffset UpdatedAt);
}
