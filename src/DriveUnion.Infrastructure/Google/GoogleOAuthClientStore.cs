using System.Globalization;
using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// One stored OAuth client as the panel may see it — everything about it except the secret.
///
/// There is deliberately no way to get the secret out of this record. The panel renders it, and a
/// screen that could print the secret is a screen that eventually will: into an HTML source view, a
/// browser cache, a bug report screenshot. <see cref="HasClientSecret"/> is the only thing the
/// browser ever learns, and <see cref="IGoogleOAuthClientStore.ReadSecret"/> — the one way back to
/// the plaintext — is reached only by the two requests that go to Google.
/// </summary>
/// <param name="HasClientSecret">
/// True only when a stored secret is present <em>and</em> still decrypts. A secret written under a
/// Data Protection key that has since been lost is reported as absent on purpose: the operator's
/// only fix is to paste it again, and a screen that claimed the secret was set would send them
/// hunting through Google Cloud for a fault that is on this side.
/// </param>
public sealed record StoredGoogleOAuthClient(
    Guid Id,
    string Label,
    string ClientId,
    string RedirectUri,
    bool HasClientSecret,
    bool IsDefault,
    DateTimeOffset UpdatedAt);

/// <summary>How a removal ended. Three answers, because the screen says something different to each.</summary>
public enum GoogleOAuthClientRemoval
{
    Removed,

    NotFound,

    /// <summary>
    /// Accounts were connected under this client and can only be refreshed with it. Removing it
    /// would strand every one of them at the next hour boundary, and the operator would meet that as
    /// uploads failing rather than as anything they had done.
    /// </summary>
    InUseByAccounts,
}

/// <param name="AccountLabels">
/// The accounts standing in the way, by the label their cards carry, so the refusal can name them
/// instead of asserting that something somewhere depends on this.
/// </param>
public sealed record GoogleOAuthClientRemovalResult(
    GoogleOAuthClientRemoval Outcome,
    IReadOnlyList<string> AccountLabels);

/// <summary>How a save ended. Three answers, because the screen says something different to each.</summary>
public enum GoogleOAuthClientSave
{
    Saved,

    /// <summary>The edit named a client that is not there any more.</summary>
    NotFound,

    /// <summary>
    /// Another row already holds this client id. Refused rather than allowed, because two rows for
    /// one Google client are two secrets for one credential, and which of them a refresh found would
    /// depend on row order. The unique index would refuse it anyway; this refuses it in a sentence.
    /// </summary>
    DuplicateClientId,
}

public sealed record GoogleOAuthClientSaveResult(
    GoogleOAuthClientSave Outcome,
    StoredGoogleOAuthClient? Client);

/// <summary>
/// Where the OAuth clients the operator types into the panel are kept.
///
/// This exists because the owner has no terminal on the box. Google will not issue a token without a
/// client id — that part is Google's rule — but needing <c>dotnet user-secrets</c> to supply one was
/// ours, and this is the seam that removes it.
/// </summary>
public interface IGoogleOAuthClientStore
{
    /// <summary>Every stored client, oldest first. Empty when the operator has saved nothing.</summary>
    IReadOnlyList<StoredGoogleOAuthClient> List();

    /// <summary>
    /// The stored client a new consent flow runs with, or null when nothing is stored. Configuration
    /// still outranks it — <see cref="GoogleOAuthCredentialResolver"/> is where that is decided.
    /// </summary>
    StoredGoogleOAuthClient? Default();

    StoredGoogleOAuthClient? Find(Guid id);

    /// <summary>The row holding Google's own client id, which is what an account is bound to.</summary>
    StoredGoogleOAuthClient? FindByClientId(string clientId);

    /// <summary>
    /// The secret in the clear, for the authorization-code exchange and the refresh that are the only
    /// two things that need it. Null when none is stored, or when the stored one no longer decrypts.
    /// </summary>
    string? ReadSecret(Guid id);

    /// <summary>The same, found by Google's client id rather than by the row's.</summary>
    string? ReadSecretForClientId(string clientId);

    /// <summary>
    /// Adds a client when <paramref name="id"/> is null, and edits that one when it is not.
    ///
    /// A null or empty <paramref name="clientSecret"/> keeps the secret already stored, which is what
    /// makes "correct a typo in the client id" possible without asking the operator to fetch the
    /// secret out of Google Cloud again.
    /// </summary>
    GoogleOAuthClientSaveResult Save(Guid? id, string clientId, string? clientSecret, string redirectUri);

    /// <summary>Makes one client the one new connections use. False when it names nothing.</summary>
    bool MakeDefault(Guid id);

    /// <summary>
    /// Forgets a client, unless accounts are still refreshed with it — see
    /// <see cref="GoogleOAuthClientRemoval.InUseByAccounts"/>.
    /// </summary>
    GoogleOAuthClientRemovalResult Remove(Guid id);
}

/// <summary>
/// The OAuth clients, as rows, with their secrets encrypted by the same
/// <see cref="ITokenProtector"/> that protects the Google refresh tokens.
///
/// <para><b>Nothing here is cached.</b> The version this replaced held the credential for the life of
/// the process, which was safe only because a file has one writer. A row does not: a second replica,
/// a psql session, or a support fix applied while the panel is up would all be invisible to a cache
/// nothing invalidates. Every call below is a query against a table that holds one or two rows, and
/// the callers are an operator loading a screen and a token refresh that happens once an hour per
/// account — there is nothing here worth caching and no staleness worth debugging.</para>
///
/// <para><b>Why the reads are synchronous.</b> <see cref="Microsoft.Extensions.Options.IOptions{T}"/>
/// has a synchronous <c>Value</c>, and the whole point of resolving the client per read is that the
/// existing consumers keep asking for exactly what they asked for before. A scope is opened per call
/// rather than a <see cref="DriveUnionDbContext"/> held, because this is a singleton and a context is
/// not thread-safe.</para>
/// </summary>
public sealed class GoogleOAuthClientStore : IGoogleOAuthClientStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITokenProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GoogleOAuthClientStore> _logger;

    public GoogleOAuthClientStore(
        IServiceScopeFactory scopeFactory,
        ITokenProtector protector,
        TimeProvider timeProvider,
        ILogger<GoogleOAuthClientStore> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = protector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public IReadOnlyList<StoredGoogleOAuthClient> List()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = Context(scope);

        // Ordered here rather than in the query, like the account pool next door: this table holds
        // the operator's one or two clients and is already materialised, so pushing the sort to the
        // database buys nothing and costs the query its portability. By age and not by label,
        // because «C10» sorts before «C2» as text.
        return [.. Rows(db).OrderBy(c => c.CreatedAt).Select(Describe)];
    }

    public StoredGoogleOAuthClient? Default()
    {
        using var scope = _scopeFactory.CreateScope();

        var row = DefaultRow(Rows(Context(scope)));

        return row is null ? null : Describe(row);
    }

    public StoredGoogleOAuthClient? Find(Guid id)
    {
        using var scope = _scopeFactory.CreateScope();

        var row = Context(scope).GoogleOAuthClients.AsNoTracking().FirstOrDefault(c => c.Id == id);

        return row is null ? null : Describe(row);
    }

    public StoredGoogleOAuthClient? FindByClientId(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        using var scope = _scopeFactory.CreateScope();

        var row = Row(Context(scope), clientId.Trim());

        return row is null ? null : Describe(row);
    }

    public string? ReadSecret(Guid id)
    {
        using var scope = _scopeFactory.CreateScope();

        var row = Context(scope).GoogleOAuthClients.AsNoTracking().FirstOrDefault(c => c.Id == id);

        return row is null ? null : Unprotect(row.ClientSecretProtected);
    }

    public string? ReadSecretForClientId(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        using var scope = _scopeFactory.CreateScope();

        var row = Row(Context(scope), clientId.Trim());

        return row is null ? null : Unprotect(row.ClientSecretProtected);
    }

    public GoogleOAuthClientSaveResult Save(Guid? id, string clientId, string? clientSecret, string redirectUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        using var scope = _scopeFactory.CreateScope();
        var db = Context(scope);

        var rows = db.GoogleOAuthClients.ToList();
        var now = _timeProvider.GetUtcNow();
        var wanted = clientId.Trim();

        // Checked here rather than left to the unique index, so the answer is a sentence on the
        // screen instead of a 500 the operator has to guess the meaning of.
        if (rows.Any(c => c.Id != id && string.Equals(c.ClientId, wanted, StringComparison.Ordinal)))
        {
            return new GoogleOAuthClientSaveResult(GoogleOAuthClientSave.DuplicateClientId, null);
        }

        GoogleOAuthClient row;

        if (id is { } existing)
        {
            if (rows.FirstOrDefault(c => c.Id == existing) is not { } found)
            {
                return new GoogleOAuthClientSaveResult(GoogleOAuthClientSave.NotFound, null);
            }

            row = found;
        }
        else
        {
            row = new GoogleOAuthClient
            {
                Id = Guid.CreateVersion7(),
                Label = NextLabel(rows),
                ClientId = string.Empty,
                RedirectUri = string.Empty,
                CreatedAt = now,

                // The first client stored is the one new connections use. Every one after it has to
                // be promoted by hand — see GoogleOAuthClient.IsDefault.
                IsDefault = rows.Count == 0,
            };

            db.GoogleOAuthClients.Add(row);
            rows.Add(row);
        }

        // Re-protecting the surviving secret rather than leaving its ciphertext alone is deliberate:
        // it re-encrypts under whatever key is current, so a value written years ago is quietly
        // carried forward every time the operator touches this form.
        var secret = string.IsNullOrEmpty(clientSecret)
            ? Unprotect(row.ClientSecretProtected)
            : clientSecret;

        row.ClientId = wanted;
        row.RedirectUri = redirectUri.Trim();
        row.ClientSecretProtected = secret is null ? null : _protector.Protect(secret);
        row.UpdatedAt = now;

        db.SaveChanges();

        // No values, not even the client id. This line exists to make "the credentials changed"
        // findable in a log; nothing about it should ever need the credentials themselves.
        _logger.LogInformation(
            "Google OAuth client {Label} was {Action} from the panel.",
            row.Label,
            id is null ? "added" : "updated");

        return new GoogleOAuthClientSaveResult(GoogleOAuthClientSave.Saved, Describe(row));
    }

    public bool MakeDefault(Guid id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = Context(scope);

        var rows = db.GoogleOAuthClients.ToList();

        if (rows.FirstOrDefault(c => c.Id == id) is not { } chosen) return false;

        // Cleared on the others in the same SaveChanges, so there is never a moment where two rows
        // claim it and the answer depends on which one a query reads first.
        foreach (var row in rows) row.IsDefault = row.Id == id;

        db.SaveChanges();

        _logger.LogInformation(
            "Google OAuth client {Label} is now the one new connections use.",
            chosen.Label);

        return true;
    }

    public GoogleOAuthClientRemovalResult Remove(Guid id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = Context(scope);

        if (db.GoogleOAuthClients.FirstOrDefault(c => c.Id == id) is not { } row)
        {
            return new GoogleOAuthClientRemovalResult(GoogleOAuthClientRemoval.NotFound, []);
        }

        // A refresh token is bound to the client that issued it. Presenting it to another client is
        // an invalid_grant, which this product turns into "reconnect this account" — so removing a
        // client that accounts still name does not fail here, it fails silently an hour later, on
        // every one of them at once. That is the failure this whole change is about.
        var dependents = db.GoogleAccounts
            .AsNoTracking()
            .Where(a => a.OAuthClientId == row.ClientId)
            .Select(a => a.Label)
            .ToList();

        if (dependents.Count > 0)
        {
            return new GoogleOAuthClientRemovalResult(
                GoogleOAuthClientRemoval.InUseByAccounts,
                [.. dependents.OrderBy(label => label, StringComparer.Ordinal)]);
        }

        var wasDefault = row.IsDefault;

        db.GoogleOAuthClients.Remove(row);
        db.SaveChanges();

        if (wasDefault)
        {
            // Something has to be the client new connections use, or the panel would sit there with a
            // saved client and no way to reach Google. The oldest survivor takes it, which is the
            // same rule that made the first one the default.
            //
            // Ordered in memory: SQLite stores a DateTimeOffset as text and refuses to compare one,
            // and this table holds the operator's one or two rows.
            var heir = db.GoogleOAuthClients.ToList().MinBy(c => c.CreatedAt);

            if (heir is not null)
            {
                heir.IsDefault = true;
                db.SaveChanges();
            }
        }

        _logger.LogInformation("Google OAuth client {Label} was removed from the panel.", row.Label);

        return new GoogleOAuthClientRemovalResult(GoogleOAuthClientRemoval.Removed, []);
    }

    /// <summary>
    /// <c>C1</c>, <c>C2</c>, … — one past the highest ever issued, never filling a gap.
    ///
    /// The same rule as <see cref="GoogleAccountDirectory"/>'s account labels, for the same reason:
    /// an account card names the client that connected it, and a label reused for a different client
    /// would make every card that still carries the old one say something false. Only labels shaped
    /// <c>C</c>-and-a-number reserve a number; nothing writes any other shape, so this is
    /// defensiveness rather than policy.
    /// </summary>
    private static string NextLabel(IReadOnlyList<GoogleOAuthClient> rows)
    {
        var highest = 0;

        foreach (var row in rows)
        {
            if (row.Label.Length > 1
                && row.Label[0] is 'C' or 'c'
                && int.TryParse(
                    row.Label.AsSpan(1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number)
                && number > highest)
            {
                highest = number;
            }
        }

        return $"C{highest + 1}";
    }

    private static DriveUnionDbContext Context(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<DriveUnionDbContext>();

    private static List<GoogleOAuthClient> Rows(DriveUnionDbContext db) =>
        db.GoogleOAuthClients.AsNoTracking().ToList();

    private static GoogleOAuthClient? Row(DriveUnionDbContext db, string clientId) =>
        db.GoogleOAuthClients.AsNoTracking().FirstOrDefault(c => c.ClientId == clientId);

    /// <summary>
    /// The default among a set of rows.
    ///
    /// The oldest wins if two ever carry the flag — the writes above make that impossible, and a
    /// stable wrong answer is still better than one that changes with row order.
    /// </summary>
    private static GoogleOAuthClient? DefaultRow(IReadOnlyList<GoogleOAuthClient> rows) =>
        rows.Where(c => c.IsDefault).OrderBy(c => c.CreatedAt).FirstOrDefault()
        ?? rows.OrderBy(c => c.CreatedAt).FirstOrDefault();

    private StoredGoogleOAuthClient Describe(GoogleOAuthClient row) => new(
        row.Id,
        row.Label,
        row.ClientId,
        row.RedirectUri,
        Unprotect(row.ClientSecretProtected) is not null,
        row.IsDefault,
        row.UpdatedAt);

    /// <summary>
    /// Null both when nothing is stored and when what is stored no longer decrypts. The two are the
    /// same situation for every caller: there is no usable secret, and the operator has to paste one.
    /// </summary>
    private string? Unprotect(string? cipher) =>
        string.IsNullOrEmpty(cipher) ? null : _protector.Unprotect(cipher);
}
