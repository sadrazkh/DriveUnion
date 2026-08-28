using System.Reflection;
using System.Security.Cryptography;
using DriveUnion.Core.Application;
using DriveUnion.Core.Storage;
using DriveUnion.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DriveUnion.Tests.Uploads;

/// <summary>
/// The link-fetch's key protocol, after the passphrase stopped being sent to the server.
///
/// <para><b>What changed and why.</b> This path used to take what the customer typed, derive a
/// wrapping key from it here, and wrap a fresh content key. On its own terms that was defensible:
/// the server downloads the plaintext, so it holds the file either way. It stopped being defensible
/// the moment you notice that people use one secret for everything — a server that had seen that
/// passphrase once could open every file the customer had ever locked <i>in their own browser</i>,
/// which is the product's central promise with an exception in it.</para>
///
/// <para>The browser derives now. These are about that being true rather than nearly true: the
/// server must have no way to accept a passphrase, and no way to derive one.</para>
/// </summary>
public class FetchCustodyTests
{
    private const string Url = "https://example.test/holiday.mp4";

    /// <summary>
    /// <b>The passphrase cannot be sent, because there is nowhere to put it.</b>
    ///
    /// <para>Asserted against the signature rather than against behaviour, and that is the point: a
    /// behavioural test can only show that some particular string was not used, while this shows
    /// there is no parameter it could arrive in. A future edit that adds one back fails here.</para>
    /// </summary>
    [Fact]
    public void The_start_signature_has_nowhere_to_receive_a_passphrase()
    {
        var start = typeof(IRemoteFetches).GetMethod(nameof(IRemoteFetches.StartAsync))!;

        var strings = start.GetParameters()
            .Where(p => p.ParameterType == typeof(string))
            .Select(p => p.Name)
            .ToList();

        // The URL, and nothing else. A second string parameter on this method is how the old
        // protocol would come back — most likely called something innocent.
        strings.Should().Equal(["url"], "the only string a fetch needs is the address");

        start.GetParameters().Should().Contain(
            p => p.ParameterType == typeof(FetchCustody),
            "the wrapping the browser made is what arrives instead");
    }

    /// <summary>
    /// And the service does not derive, anywhere.
    ///
    /// <para>A source scan, because the failure it guards is a line being added rather than a value
    /// being wrong. <c>DeriveWrappingKey</c> is the only way to turn a secret into a key in this
    /// product, so an appearance of it on this path would mean a passphrase had reached it.</para>
    /// </summary>
    [Fact]
    public void The_fetch_service_derives_nothing()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src/DriveUnion.Infrastructure/Uploads/RemoteFetches.cs"));

        source.Should().NotContain(
            "DeriveWrappingKey",
            "deriving here means a passphrase reached the server, which is the whole thing this "
                + "change removed");

        source.Should().NotContain(
            "WrapKey",
            "wrapping here means the server made the custody rather than receiving it");
    }

    /// <summary>
    /// Custody and the key arrive together or the fetch is refused.
    ///
    /// <para>One without the other produces a file that seals cleanly and opens for nobody: a key
    /// with no wrapping cannot be recovered from the passphrase, and a wrapping with no key would be
    /// stored beside ciphertext made with something else. Both are discovered months later by the
    /// person trying to open the file, which is why they are refused now.</para>
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Half_a_lock_is_refused(bool withCustody, bool withKey)
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var (custody, key) = Locked();

        var result = await harness.Fetches().StartAsync(
            tenant.Id,
            null,
            Url,
            withCustody ? custody : null,
            withKey ? key : null,
            default);

        result.Started.Should().BeFalse();
        result.Detail.Should().Be("bad_custody");

        (await harness.Db.RemoteFetches.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_key_that_is_not_the_right_length_is_refused()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var (custody, _) = Locked();

        // Sixteen bytes. AES-256 takes thirty-two, and a short key would fail inside the sealing
        // loop — a job that dies rather than a request that was refused.
        var result = await harness.Fetches().StartAsync(
            tenant.Id, null, Url, custody, new byte[16], default);

        result.Started.Should().BeFalse();
        result.Detail.Should().Be("bad_custody");
    }

    [Fact]
    public async Task Custody_that_is_not_shaped_like_custody_is_refused()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var (custody, key) = Locked();

        // An iteration count below the floor. It is carried and never used on this side, so nothing
        // here would notice — but it is what the browser will derive with when the file is opened,
        // and a thousand rounds is a passphrase with almost no protection behind it.
        var weak = custody with { KdfIterations = 1_000 };

        var result = await harness.Fetches().StartAsync(tenant.Id, null, Url, weak, key, default);

        result.Started.Should().BeFalse();
        result.Detail.Should().Be("bad_custody");
    }

    /// <summary>An unlocked fetch is still an ordinary thing to ask for.</summary>
    [Fact]
    public async Task A_fetch_with_no_lock_at_all_is_accepted()
    {
        await using var harness = ServiceTestHarness.Create();
        var tenant = harness.SeedTenant("acme");

        var result = await harness.Fetches().StartAsync(tenant.Id, null, Url, null, null, default);

        result.Started.Should().BeTrue();

        var row = await harness.Db.RemoteFetches.AsNoTracking().SingleAsync();
        row.IsEncrypted.Should().BeFalse();
    }

    /// <summary>What the browser does before it posts. See <c>RemoteFetchTests.Locked</c>.</summary>
    private static (FetchCustody Custody, byte[] Key) Locked()
    {
        const int iterations = 100_000;

        var salt = RandomNumberGenerator.GetBytes(Du1.SaltBytes);
        var wrapping = Du1.DeriveWrappingKey("a passphrase", salt, iterations);
        var key = RandomNumberGenerator.GetBytes(Du1.KeyBytes);

        return (
            new FetchCustody(
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(Du1.NoncePrefixBytes)),
                Convert.ToBase64String(salt),
                iterations,
                Convert.ToBase64String(Du1.WrapKey(key, wrapping))),
            key);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("DriveUnion.slnx").Any()) return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("DriveUnion.slnx was not found above the test binaries.");
    }
}
