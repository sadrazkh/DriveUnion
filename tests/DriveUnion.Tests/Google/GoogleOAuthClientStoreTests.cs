using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Google;

/// <summary>
/// The OAuth clients at rest: a real database, the real <c>DataProtectionTokenProtector</c> that
/// protects the Google refresh tokens, and no mocking of either.
///
/// The two things worth proving are the two that failed in production. That the value the operator
/// typed comes back byte for byte and is not sitting in the table in the clear — and that it is
/// still there after a restart, because the version this replaced kept it in a JSON file inside the
/// container and a redeploy deleted it, leaving every account with a refresh token nothing could
/// refresh and a customer being told storage was unavailable.
/// </summary>
public sealed class GoogleOAuthClientStoreTests : IDisposable
{
    private const string ClientId = "982374-abcdef.apps.googleusercontent.com";
    private const string RedirectUri = "https://drive.example/accounts/callback";
    private const string Secret = "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rendered";

    private readonly GoogleClientStoreHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void A_saved_secret_comes_back_exactly_and_is_not_in_the_table()
    {
        var store = _harness.Store();

        var saved = Save(store);

        store.ReadSecret(saved.Id).Should().Be(Secret);
        store.ReadSecretForClientId(ClientId).Should().Be(Secret);

        var row = _harness.Read(db => db.GoogleOAuthClients.AsNoTracking().Single());

        row.ClientSecretProtected.Should().NotBeNull();
        row.ClientSecretProtected.Should().NotContain(Secret, "a database dump is not a key ring");
        row.ClientId.Should().Be(ClientId, "the client id is not a secret — it travels in the authorization URL");

        // The ciphertext in the row is what this protector wrote, and only it can read it back.
        _harness.Protector.Unprotect(row.ClientSecretProtected!).Should().Be(Secret);
    }

    /// <summary>
    /// The redeploy, reproduced. A second store over the same database is the panel after a restart,
    /// and this is the whole reason the client stopped being a file: the key ring is in this
    /// database too, so both halves of the credential survive together or not at all.
    /// </summary>
    [Fact]
    public void What_was_saved_survives_a_restart()
    {
        Save(_harness.Store());

        var reopened = _harness.Store();

        var stored = reopened.List().Should().ContainSingle().Subject;
        stored.ClientId.Should().Be(ClientId);
        stored.RedirectUri.Should().Be(RedirectUri);
        stored.HasClientSecret.Should().BeTrue();
        stored.IsDefault.Should().BeTrue();

        reopened.ReadSecret(stored.Id).Should().Be(Secret);
        reopened.Default()!.ClientId.Should().Be(ClientId);
    }

    /// <summary>
    /// The form cannot show a secret back, so it cannot ask for it again either. Correcting a typo
    /// in the client id must not cost the operator a trip to Google Cloud.
    /// </summary>
    [Fact]
    public void Saving_with_no_secret_keeps_the_one_already_there()
    {
        var store = _harness.Store();
        var saved = Save(store);

        store.Save(saved.Id, "corrected.apps.googleusercontent.com", clientSecret: null, RedirectUri)
            .Outcome.Should().Be(GoogleOAuthClientSave.Saved);

        store.Find(saved.Id)!.ClientId.Should().Be("corrected.apps.googleusercontent.com");
        store.ReadSecret(saved.Id).Should().Be(Secret);
    }

    [Fact]
    public void A_new_secret_replaces_the_old_one()
    {
        var store = _harness.Store();
        var saved = Save(store);

        store.Save(saved.Id, ClientId, "GOCSPX-rotated", RedirectUri);

        store.ReadSecret(saved.Id).Should().Be("GOCSPX-rotated");
    }

    /// <summary>
    /// A key ring that is gone. The screen has to say the secret is not set — because it is not
    /// usable — rather than claim it is stored and send the operator hunting through Google Cloud
    /// for a fault that is on this side.
    /// </summary>
    [Fact]
    public void A_secret_written_under_a_lost_key_reads_as_absent_rather_than_throwing()
    {
        var saved = Save(_harness.Store());

        var afterKeyLoss = _harness.Store(GoogleClientStoreHarness.NewProtector());

        afterKeyLoss.ReadSecret(saved.Id).Should().BeNull();

        var stored = afterKeyLoss.List().Should().ContainSingle().Subject;
        stored.HasClientSecret.Should().BeFalse();
        stored.ClientId.Should().Be(ClientId, "the rest of the client is not encrypted and is still true");
    }

    [Fact]
    public void The_first_client_saved_is_the_one_new_connections_use_and_the_second_is_not()
    {
        var store = _harness.Store();

        var first = Save(store);
        var second = Add(store, "second.apps.googleusercontent.com");

        store.Default()!.Id.Should().Be(first.Id);
        store.Find(second.Id)!.IsDefault.Should().BeFalse(
            "adding a client must not silently move which one the next account is bound to");
    }

    [Fact]
    public void Promoting_a_client_demotes_the_other()
    {
        var store = _harness.Store();

        var first = Save(store);
        var second = Add(store, "second.apps.googleusercontent.com");

        store.MakeDefault(second.Id).Should().BeTrue();

        store.Default()!.Id.Should().Be(second.Id);
        store.List().Where(c => c.IsDefault).Should().ContainSingle(
            "two rows claiming it would make the answer depend on row order");
        store.Find(first.Id)!.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Two_rows_cannot_hold_one_client_id()
    {
        var store = _harness.Store();
        Save(store);

        var again = store.Save(id: null, ClientId, "GOCSPX-another", RedirectUri);

        again.Outcome.Should().Be(GoogleOAuthClientSave.DuplicateClientId);
        again.Client.Should().BeNull();
        store.List().Should().ContainSingle("one Google client is one credential, not two secrets");
    }

    [Fact]
    public void Editing_a_client_that_is_not_there_says_so_rather_than_inserting_one()
    {
        var store = _harness.Store();

        var result = store.Save(Guid.CreateVersion7(), ClientId, Secret, RedirectUri);

        result.Outcome.Should().Be(GoogleOAuthClientSave.NotFound);
        store.List().Should().BeEmpty();
    }

    /// <summary>
    /// The refusal this whole change exists for.
    ///
    /// A refresh token can only be presented by the client that issued it — anything else is
    /// <c>invalid_grant</c>, which this product turns into "reconnect this account". So removing a
    /// client accounts still name does not fail when it is pressed: it fails an hour later, on every
    /// one of them at once, as uploads reporting that storage is unavailable.
    /// </summary>
    [Fact]
    public void Removing_a_client_accounts_depend_on_is_refused_and_names_them()
    {
        var store = _harness.Store();
        var saved = Save(store);

        SeedAccount("A2", ClientId);
        SeedAccount("A1", ClientId);
        SeedAccount("A3", "some-other-client.apps.googleusercontent.com");

        var removal = store.Remove(saved.Id);

        removal.Outcome.Should().Be(GoogleOAuthClientRemoval.InUseByAccounts);
        removal.AccountLabels.Should().Equal("A1", "A2");

        store.List().Should().ContainSingle("nothing may be removed out from under an account");
    }

    [Fact]
    public void Removing_a_client_nothing_depends_on_hands_the_default_to_the_oldest_survivor()
    {
        var store = _harness.Store();

        var first = Save(store);
        var second = Add(store, "second.apps.googleusercontent.com");

        store.Remove(first.Id).Outcome.Should().Be(GoogleOAuthClientRemoval.Removed);

        store.List().Should().ContainSingle();
        store.Default()!.Id.Should().Be(second.Id, "something has to be what the next connection uses");
    }

    [Fact]
    public void Removing_something_that_is_not_there_reports_it()
    {
        var store = _harness.Store();

        store.Remove(Guid.CreateVersion7()).Outcome.Should().Be(GoogleOAuthClientRemoval.NotFound);
    }

    /// <summary>
    /// An account connected under a client that was later removed still names it, and the store has
    /// to answer honestly rather than silently handing back another client's secret.
    /// </summary>
    [Fact]
    public void A_client_id_nothing_holds_produces_no_secret()
    {
        var store = _harness.Store();
        Save(store);

        store.FindByClientId("never-saved.apps.googleusercontent.com").Should().BeNull();
        store.ReadSecretForClientId("never-saved.apps.googleusercontent.com").Should().BeNull();
    }

    [Fact]
    public void Labels_are_allocated_one_past_the_highest_ever_issued()
    {
        var store = _harness.Store();

        var first = Save(store);
        var second = Add(store, "second.apps.googleusercontent.com");

        first.Label.Should().Be("C1");
        second.Label.Should().Be("C2");

        store.Remove(first.Id);
        Add(store, "third.apps.googleusercontent.com").Label.Should().Be("C3",
            "an account card names the client that connected it, and a reused label would make every "
            + "card carrying the old one say something false");
    }

    private static StoredGoogleOAuthClient Save(IGoogleOAuthClientStore store)
    {
        var result = store.Save(id: null, ClientId, Secret, RedirectUri);

        result.Outcome.Should().Be(GoogleOAuthClientSave.Saved);

        return result.Client!;
    }

    private static StoredGoogleOAuthClient Add(IGoogleOAuthClientStore store, string clientId)
    {
        var result = store.Save(id: null, clientId, $"GOCSPX-for-{clientId}", RedirectUri);

        result.Outcome.Should().Be(GoogleOAuthClientSave.Saved);

        return result.Client!;
    }

    private void SeedAccount(string label, string clientId) => _harness.Write(db => db.GoogleAccounts.Add(
        new GoogleAccount
        {
            Id = Guid.CreateVersion7(),
            Email = $"{label.ToLowerInvariant()}@example.com",
            Label = label,
            RefreshTokenProtected = "not-a-real-protected-token",
            OAuthClientId = clientId,
            CreatedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
        }));
}
