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
            new StubTokenService(),
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

    [Fact]
    public async Task Reconnecting_the_same_address_replaces_the_credentials_rather_than_adding_a_row()
    {
        var first = await Connect();
        var second = await Connect();

        second.Should().Be(first, "the unique index on Email would reject a duplicate anyway, and "
            + "'this account stopped working, here it is again' is what the operator meant");

        (await _db.GoogleAccounts.CountAsync()).Should().Be(1);
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

    private Task<Guid> Connect() =>
        _directory.ConnectAsync("auth-code", "https://drive.example/oauth", CancellationToken.None);

    private sealed class StubAboutReader : IGoogleAboutReader
    {
        public const string DefaultEmail = "pool-a1@example.com";

        public string CurrentEmail { get; set; } = DefaultEmail;

        public Task<GoogleAboutInfo> GetAboutAsync(string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(new GoogleAboutInfo(CurrentEmail, 5497558138880, 1099511627776));
    }

    /// <summary>
    /// Only <see cref="GetStorageQuotaAsync"/> is reachable from the directory. The others throw
    /// rather than returning a default, so a call the directory has no business making shows up as a
    /// failure instead of a pass.
    /// </summary>
    private sealed class StubDriveClient : IDriveClient
    {
        public DriveStorageQuota Quota { get; set; } = new(5497558138880, 1099511627776);

        public Task<DriveStorageQuota> GetStorageQuotaAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult(Quota);

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
