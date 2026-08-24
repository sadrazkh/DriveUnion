using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Storage;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Persistence;
using DriveUnion.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Google;

/// <summary>
/// The operator's pool. SQLite stands in for Postgres; nothing asserted here is dialect-specific.
/// </summary>
public sealed class GoogleAccountDirectoryTests : IDisposable
{
    private const string RefreshTokenPlaintext = "1//stub-refresh-token";

    private readonly SqliteConnection _keepAlive;
    private readonly DriveUnionDbContext _db;
    private readonly DataProtectionTokenProtector _protector;
    private readonly StubAboutReader _about = new();
    private readonly StubDriveClient _drive = new();
    private readonly StubTokenService _tokens = new();
    private readonly ImmediateTimeProvider _time = new();
    private readonly GoogleAccountDirectory _directory;

    public GoogleAccountDirectoryTests()
    {
        var connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        _db = new DriveUnionDbContext(
            new DbContextOptionsBuilder<DriveUnionDbContext>().UseSqlite(connectionString).Options);
        _db.Database.EnsureCreated();

        _protector = new DataProtectionTokenProtector(
            new EphemeralDataProtectionProvider(),
            NullLogger<DataProtectionTokenProtector>.Instance);

        _directory = new GoogleAccountDirectory(
            _db,
            _tokens,
            _about,
            _drive,
            _protector,
            _time,
            NullLogger<GoogleAccountDirectory>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _keepAlive.Dispose();
    }

    [Fact]
    public async Task Connecting_stores_the_refresh_token_encrypted_and_labels_the_account()
    {
        var id = await Connect();

        var account = await _db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == id);

        account.Email.Should().Be(StubAboutReader.DefaultEmail);
        account.Label.Should().Be("A1");
        account.Status.Should().Be(GoogleAccountStatus.Healthy);
        account.QuotaTotalBytes.Should().Be(5497558138880);

        account.RefreshTokenProtected.Should().NotBe(RefreshTokenPlaintext, "a database dump is not a key ring");
        _protector.Unprotect(account.RefreshTokenProtected).Should().Be(RefreshTokenPlaintext);
    }

    [Fact]
    public async Task A_second_account_gets_the_next_label()
    {
        await Connect();

        _time.Now = _time.Now.AddMinutes(5);
        _about.CurrentEmail = "pool-a2@example.com";
        await Connect();

        var accounts = await _directory.ListAsync(CancellationToken.None);

        accounts.Select(a => a.Label).Should().Equal("A1", "A2");
        accounts.Select(a => a.Email).Should().Equal("pool-a1@example.com", "pool-a2@example.com");
    }

    /// <summary>
    /// One mailbox, two spellings. Gmail treats a dotted address, a plus-tagged one and the plain
    /// one as the same account and echoes back whichever was typed at the consent screen, so an
    /// operator reconnecting on a tired evening can approve a spelling the panel has not seen.
    ///
    /// Keyed on the address that is a second row, a second label, and five terabytes of pool
    /// capacity that does not exist — and the upload router would then send files to an account it
    /// believes is empty. Keyed on Drive's permissionId it is what it actually is: the same account.
    /// </summary>
    [Fact]
    public async Task The_same_account_under_a_Gmail_alias_is_not_a_second_account()
    {
        var first = await Connect();

        _time.Now = _time.Now.AddMinutes(5);

        // The same identity Google reported the first time, under a spelling the panel has not seen.
        _about.PinnedPermissionId = StubAboutReader.PermissionIdFor(StubAboutReader.DefaultEmail);
        _about.CurrentEmail = "pool.a1+cold@example.com";
        var second = await Connect();

        second.Should().Be(first);
        (await _db.GoogleAccounts.CountAsync()).Should().Be(1);

        var accounts = await _directory.ListAsync(CancellationToken.None);
        accounts.Select(a => a.Label).Should().Equal("A1");

        // The card reads what the operator will see in Google, not the spelling used months ago.
        accounts.Single().Email.Should().Be("pool.a1+cold@example.com");
    }

    /// <summary>
    /// A row written before permissionId was stored matches on its address once, and is filled in
    /// while it is there — so the fallback that exists for it retires itself rather than staying
    /// forever as the path nobody tests.
    /// </summary>
    [Fact]
    public async Task A_row_that_predates_the_identity_column_is_matched_by_address_and_backfilled()
    {
        await Connect();

        var legacy = await _db.GoogleAccounts.SingleAsync();
        legacy.GoogleUserId = null;
        await _db.SaveChangesAsync();

        _time.Now = _time.Now.AddMinutes(5);
        var again = await Connect();

        again.Should().Be(legacy.Id);
        (await _db.GoogleAccounts.CountAsync()).Should().Be(1);

        var reconnected = await _db.GoogleAccounts.SingleAsync();
        reconnected.GoogleUserId.Should().Be(StubAboutReader.PermissionIdFor(StubAboutReader.DefaultEmail));
    }

    /// <summary>
    /// Drive is not documented to omit permissionId, and the code does not assume it will not.
    /// Without an identity there is nothing to key on but the address, which is what the product
    /// did before this column existed — worse, and still better than refusing to connect at all.
    /// </summary>
    [Fact]
    public async Task An_about_response_with_no_identity_still_connects_on_the_address()
    {
        _about.ReportsNoIdentity = true;

        var first = await Connect();
        var second = await Connect();

        second.Should().Be(first);
        (await _db.GoogleAccounts.CountAsync()).Should().Be(1);
        (await _db.GoogleAccounts.SingleAsync()).GoogleUserId.Should().BeNull();
    }

    [Fact]
    public async Task Reconnecting_the_same_address_replaces_the_credentials_rather_than_adding_a_row()
    {
        var first = await Connect();
        var second = await Connect();

        second.Should().Be(first, "the unique index on Email would reject a duplicate anyway, and "
            + "'this account stopped working, here it is again' is what the operator meant");

        (await _db.GoogleAccounts.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// The three things a reconnection must not disturb, asserted together because they fail
    /// together: the row is the same row, its label is the same label, and the files stored on it
    /// still point at it. A reconnection that renumbered anything would move a tenant's files under
    /// them without moving a byte.
    /// </summary>
    [Fact]
    public async Task Reconnecting_keeps_the_label_and_the_files_already_stored_on_the_account()
    {
        await Connect();

        _time.Now = _time.Now.AddMinutes(5);
        _about.CurrentEmail = "pool-a2@example.com";
        var second = await Connect();

        var fileId = Guid.CreateVersion7();
        _db.StoredFiles.Add(new StoredFile
        {
            Id = fileId,
            TenantId = Guid.CreateVersion7(),
            GoogleAccountId = second,
            DriveFileId = "drive-file-id",
            Name = "quarterly.tar.zst",
            MimeType = "application/zstd",
            SizeBytes = 4096,
            CreatedAt = _time.Now,
            ModifiedAt = _time.Now,
        });
        await _db.SaveChangesAsync();

        // The account stops working — the seven-day refresh-token expiry a Testing consent screen
        // imposes is the usual cause — and the operator presses «اتصال دوباره» on its card.
        (await _directory.DisconnectAsync(second, CancellationToken.None)).Should().BeTrue();

        var again = await Connect();

        again.Should().Be(second);
        (await _db.GoogleAccounts.CountAsync()).Should().Be(2, "a reconnection is not a new account");

        var account = await _db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == second);
        account.Label.Should().Be("A2", "the label is how the operator tells the cards apart");
        account.Status.Should().Be(GoogleAccountStatus.Healthy, "the grant is what was missing");

        (await _db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == fileId))
            .GoogleAccountId.Should().Be(second);
    }

    /// <summary>
    /// A1, A2, A3 — then disconnect A2 and connect a fourth account. It is A4.
    ///
    /// The alternative, one past the row count or the first free number, would hand the new account
    /// the name «A2» while a card labelled A2 sat on the same screen: the disconnected account keeps
    /// its row, its files and every public link served through them, so its number is still spoken
    /// for. M2 §2 requires the same of <c>ShortCode</c> for the same reason — the label outlives the
    /// account in old job rows and in support conversations.
    /// </summary>
    [Fact]
    public async Task A_disconnected_label_is_never_handed_to_the_next_account()
    {
        await Connect();

        _time.Now = _time.Now.AddMinutes(5);
        _about.CurrentEmail = "pool-a2@example.com";
        var a2 = await Connect();

        _time.Now = _time.Now.AddMinutes(5);
        _about.CurrentEmail = "pool-a3@example.com";
        await Connect();

        (await _directory.DisconnectAsync(a2, CancellationToken.None)).Should().BeTrue();

        _time.Now = _time.Now.AddMinutes(5);
        _about.CurrentEmail = "pool-a4@example.com";
        await Connect();

        var accounts = await _directory.ListAsync(CancellationToken.None);

        accounts.Select(a => a.Label).Should().Equal("A1", "A2", "A3", "A4");

        // And A2 is still there, disconnected rather than gone, which is why its number is taken.
        accounts.Single(a => a.Label == "A2").Status.Should().Be(GoogleAccountStatus.Disconnected);
    }

    /// <summary>
    /// A label that is not A-and-a-number reserves no number, and the sequence carries on around it
    /// rather than throwing or guessing.
    ///
    /// Nothing in the panel writes such a label today — there is no rename — so this pins how the
    /// parse behaves if one ever appears, from a fixture, a support insert or a later milestone.
    /// </summary>
    [Fact]
    public async Task A_label_that_is_not_a_number_reserves_nothing()
    {
        var first = await Connect();

        _time.Now = _time.Now.AddMinutes(5);
        _about.CurrentEmail = "pool-a2@example.com";
        await Connect();

        var account = await _db.GoogleAccounts.SingleAsync(a => a.Id == first);
        account.Label = "archive";
        await _db.SaveChangesAsync();

        _time.Now = _time.Now.AddMinutes(5);
        _about.CurrentEmail = "pool-a3@example.com";
        await Connect();

        (await _directory.ListAsync(CancellationToken.None))
            .Single(a => a.Email == "pool-a3@example.com")
            .Label.Should().Be("A3", "A2 is still taken, and «archive» takes nothing");
    }

    /// <summary>
    /// Which client obtained the grant, written onto the row.
    ///
    /// A refresh token can only be presented by the client that issued it; anything else is
    /// <c>invalid_grant</c>, which this product reports as an account the operator has to reconnect.
    /// So the binding is not an audit field — it is what makes the account refreshable at all once
    /// the panel holds more than one client.
    /// </summary>
    [Fact]
    public async Task Connecting_records_the_client_the_grant_was_obtained_with()
    {
        var id = await Connect();

        (await _db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == id))
            .OAuthClientId.Should().Be(StubTokenService.ExchangedClientId);
    }

    /// <summary>
    /// Reconnecting under a different client is how an operator moves an account from one Google
    /// project to another. The new grant belongs to the new client, so the binding has to move with
    /// it — a stale one here makes the account unrefreshable an hour later, with the panel blaming
    /// the consent screen.
    /// </summary>
    [Fact]
    public async Task Reconnecting_under_a_different_client_moves_the_binding()
    {
        var id = await Connect();

        _time.Now = _time.Now.AddMinutes(5);
        _tokens.ClientId = "a-second-project.apps.googleusercontent.com";

        (await Connect()).Should().Be(id, "it is the same account, approved again");

        (await _db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == id))
            .OAuthClientId.Should().Be("a-second-project.apps.googleusercontent.com");
    }

    [Fact]
    public async Task Connecting_clears_the_failure_the_card_was_showing()
    {
        var id = await Connect();

        var account = await _db.GoogleAccounts.SingleAsync(a => a.Id == id);
        account.LastFailureReason = "Google rejected the grant (invalid_grant).";
        account.LastFailureAt = _time.Now;
        await _db.SaveChangesAsync();

        _time.Now = _time.Now.AddMinutes(5);
        await Connect();

        var reconnected = await _db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == id);
        reconnected.LastFailureReason.Should().BeNull("a new grant is the answer to whatever failed");
        reconnected.LastFailureAt.Should().BeNull();
    }

    [Fact]
    public async Task Disconnecting_does_not_revoke_the_token()
    {
        var id = await Connect();
        var storedToken = (await _db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == id))
            .RefreshTokenProtected;

        (await _directory.DisconnectAsync(id, CancellationToken.None)).Should().BeTrue();

        var after = await _db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == id);

        after.Status.Should().Be(GoogleAccountStatus.Disconnected);

        // Revoking at Google would kill every live /d/{slug} backed by this account instantly, with
        // no way back until M3 can move the files somewhere else.
        after.RefreshTokenProtected.Should().Be(storedToken);
    }

    [Fact]
    public async Task Disconnecting_something_that_is_not_there_reports_it()
    {
        (await _directory.DisconnectAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Refreshing_the_quota_writes_what_Drive_reported()
    {
        var id = await Connect();

        _drive.Quota = new DriveStorageQuota(5497558138880, 4000000000000);
        await _directory.RefreshQuotaAsync(id, CancellationToken.None);

        var account = await _db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == id);
        account.QuotaUsedBytes.Should().Be(4000000000000);
    }

    /// <summary>
    /// With a pool, every per-account action has to land on the account whose card was pressed. The
    /// failure being guarded against is not an exception — it is the second card's button quietly
    /// operating on the first account, which looks like nothing happening at all.
    /// </summary>
    [Fact]
    public async Task A_per_account_action_touches_that_account_and_no_other()
    {
        var a1 = await Connect();

        _time.Now = _time.Now.AddMinutes(5);
        _about.CurrentEmail = "pool-a2@example.com";
        var a2 = await Connect();

        _drive.Quota = new DriveStorageQuota(5497558138880, 4000000000000);
        await _directory.RefreshQuotaAsync(a2, CancellationToken.None);

        _drive.AskedFor.Should().Equal(a2);

        await _directory.DisconnectAsync(a2, CancellationToken.None);

        var untouched = await _db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == a1);
        untouched.QuotaUsedBytes.Should().Be(1099511627776, "A1's figures came from its own connect");
        untouched.Status.Should().Be(GoogleAccountStatus.Healthy);

        var acted = await _db.GoogleAccounts.AsNoTracking().SingleAsync(a => a.Id == a2);
        acted.QuotaUsedBytes.Should().Be(4000000000000);
        acted.Status.Should().Be(GoogleAccountStatus.Disconnected);
    }

    private Task<Guid> Connect() =>
        _directory.ConnectAsync("auth-code", "https://drive.example/oauth", CancellationToken.None);

    private sealed class StubAboutReader : IGoogleAboutReader
    {
        public const string DefaultEmail = "pool-a1@example.com";

        public string CurrentEmail { get; set; } = DefaultEmail;

        /// <summary>
        /// Pins Drive's identity for this account independently of its address.
        ///
        /// Left null it is derived from the address, so a test that changes only the address goes on
        /// describing a different account — which is what every test here that does so means. Set it
        /// to the identity of an account already connected and it describes the opposite: one
        /// mailbox under two spellings, which must stay one row.
        /// </summary>
        public string? PinnedPermissionId { get; set; }

        /// <summary>Drive is not documented to omit permissionId. This is how a test says it did.</summary>
        public bool ReportsNoIdentity { get; set; }

        public static string PermissionIdFor(string email) => $"permission-for-{email}";

        public Task<GoogleAboutInfo> GetAboutAsync(string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(new GoogleAboutInfo(
                CurrentEmail,
                ReportsNoIdentity ? null : PinnedPermissionId ?? PermissionIdFor(CurrentEmail),
                5497558138880,
                1099511627776));
    }

    /// <summary>
    /// Only <see cref="GetStorageQuotaAsync"/> is reachable from the directory. The others throw
    /// rather than returning a default, so a call the directory has no business making shows up as a
    /// failure instead of a pass.
    /// </summary>
    private sealed class StubDriveClient : IDriveClient
    {
        private readonly List<Guid> _askedFor = [];

        public DriveStorageQuota Quota { get; set; } = new(5497558138880, 1099511627776);

        /// <summary>Which accounts a quota was actually asked for, in order.</summary>
        public IReadOnlyList<Guid> AskedFor => _askedFor;

        public Task<DriveStorageQuota> GetStorageQuotaAsync(Guid accountId, CancellationToken cancellationToken)
        {
            _askedFor.Add(accountId);

            return Task.FromResult(Quota);
        }

        public Task<DriveResumableSession> BeginResumableUploadAsync(
            Guid accountId, DriveUploadRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DriveChunkOutcome> WriteChunkAsync(
            Uri sessionUri, Stream content, long offset, long length, long totalSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> GetConfirmedLengthAsync(
            Uri sessionUri, long totalSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DriveDownload> OpenDownloadAsync(
            Guid accountId, string driveFileId, string? rangeHeader, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> EnsureFolderAsync(
            Guid accountId, string folderName, string? parentFolderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
