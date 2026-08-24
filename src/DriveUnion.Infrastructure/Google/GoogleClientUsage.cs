using DriveUnion.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Infrastructure.Google;

/// <summary>
/// Which client each account is bound to, and why it last stopped working.
///
/// <para>Separate from <see cref="Core.Application.GoogleAccountSummary"/> on purpose. That record is
/// the pool's shape and the upload router reads it; these are two operator-diagnostic columns that
/// only the accounts screen has any use for, and one of them can carry a Google session URI or an
/// address — which is exactly the kind of thing that must not drift into a contract a tenant-facing
/// path also consumes.</para>
/// </summary>
/// <param name="ClientId">
/// Google's client id the account was connected under, or null on a row written before accounts were
/// bound to a client at all. A null is not a fault: those accounts are refreshed with the client in
/// force, which is the client that connected them, and the value fills itself in the first time that
/// works.
/// </param>
/// <param name="ClientLabel">
/// The <c>C1</c>-shaped handle, when a stored row owns this client id. Null when the client comes
/// from the server's configuration, which has no row and no label.
/// </param>
/// <param name="ClientIsUsable">
/// False when the account names a client this panel can no longer produce a secret for. That account
/// cannot be refreshed and the card has to say so — it is the state the old file store left the whole
/// pool in after a redeploy, with nothing on any screen to say why.
/// </param>
public sealed record GoogleAccountClientNote(
    string? ClientId,
    string? ClientLabel,
    bool ClientIsUsable,
    bool FromConfiguration,
    string? LastFailureReason,
    DateTimeOffset? LastFailureAt);

/// <param name="Accounts">Keyed by account id.</param>
/// <param name="AccountsPerClientId">
/// Keyed by Google's client id. The count is what lets the screen warn before «remove» is pressed
/// rather than only refusing afterwards.
/// </param>
public sealed record GoogleClientUsage(
    IReadOnlyDictionary<Guid, GoogleAccountClientNote> Accounts,
    IReadOnlyDictionary<string, int> AccountsPerClientId);

/// <summary>
/// What the accounts screen needs about the clients that is not in the pool's own summary.
/// </summary>
public interface IGoogleClientUsageReader
{
    Task<GoogleClientUsage> ReadAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGoogleClientUsageReader"/>
public sealed class GoogleClientUsageReader(
    DriveUnionDbContext db,
    IGoogleOAuthCredentials credentials) : IGoogleClientUsageReader
{
    public async Task<GoogleClientUsage> ReadAsync(CancellationToken cancellationToken)
    {
        var rows = await db.GoogleAccounts
            .AsNoTracking()
            .Select(a => new
            {
                a.Id,
                a.OAuthClientId,
                a.LastFailureReason,
                a.LastFailureAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var state = credentials.Describe();

        var stored = state.StoredClients
            .Where(c => c.HasClientSecret)
            .ToDictionary(c => c.ClientId, StringComparer.Ordinal);

        // The configured client has no row, so it has no label either — but an account bound to it
        // is perfectly refreshable, and a card that called it unknown would be accusing the one
        // credential the deployment is most confident about.
        var configured = state.ClientId.Source is GoogleCredentialSource.Configuration
            && state.ClientSecretSource is not GoogleCredentialSource.None
                ? state.ClientId.Value
                : null;

        var notes = new Dictionary<Guid, GoogleAccountClientNote>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.OAuthClientId is not { Length: > 0 } clientId)
            {
                // Bound to nothing is not "bound to something unknown": the row predates the column
                // and is refreshed with whatever is in force, which is the client that connected it.
                // A warning on that card would be a warning on a card that works.
                notes[row.Id] = new GoogleAccountClientNote(
                    null,
                    null,
                    ClientIsUsable: true,
                    FromConfiguration: false,
                    row.LastFailureReason,
                    row.LastFailureAt);

                continue;
            }

            counts[clientId] = counts.GetValueOrDefault(clientId) + 1;

            var fromConfiguration = string.Equals(clientId, configured, StringComparison.Ordinal);
            var label = stored.TryGetValue(clientId, out var client) ? client.Label : null;

            notes[row.Id] = new GoogleAccountClientNote(
                clientId,
                label,
                label is not null || fromConfiguration,
                fromConfiguration,
                row.LastFailureReason,
                row.LastFailureAt);
        }

        return new GoogleClientUsage(notes, counts);
    }
}
