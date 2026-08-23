using System.Text.Json;
using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.Google;
using DriveUnion.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Accounts;

/// <summary>
/// The client secret at rest: a real file, the real <see cref="DataProtectionTokenProtector"/> that
/// protects the Google refresh tokens, and no mocking of either.
///
/// The one thing worth proving twice is that the value the operator typed comes back byte for byte
/// and is not sitting in the file in the clear. Everything else here is what happens when it cannot
/// come back — the key ring rotated, the file was hand-edited — which must be an accounts screen
/// that says «تنظیم نشده», never an exception in front of whoever happened to be uploading.
/// </summary>
public sealed class GoogleCredentialStoreTests : IDisposable
{
    private const string ClientId = "982374-abcdef.apps.googleusercontent.com";
    private const string RedirectUri = "https://drive.example/accounts/callback";
    private const string Secret = "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rendered";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"driveunion-store-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void A_saved_secret_comes_back_exactly_and_is_not_in_the_file()
    {
        var protector = Protector();
        var store = Build(protector);

        store.Save(ClientId, Secret, RedirectUri);

        store.ReadClientSecret().Should().Be(Secret);

        var onDisk = File.ReadAllText(_path);
        onDisk.Should().NotContain(Secret, "the secret is encrypted at rest, not stored beside the client id");
        onDisk.Should().Contain(ClientId, "the client id is not a secret — it travels in the authorization URL");

        // The ciphertext in the file is what this protector wrote, and only it can read it back.
        using var document = JsonDocument.Parse(onDisk);
        var ciphertext = document.RootElement.GetProperty("clientSecretProtected").GetString();

        protector.Unprotect(ciphertext!).Should().Be(Secret);
    }

    [Fact]
    public void What_was_saved_survives_a_new_process()
    {
        var protector = Protector();

        Build(protector).Save(ClientId, Secret, RedirectUri);

        // A second store over the same file and the same key ring is the panel after a restart.
        var reopened = Build(protector);

        var stored = reopened.Read();
        stored.Should().NotBeNull();
        stored!.ClientId.Should().Be(ClientId);
        stored.RedirectUri.Should().Be(RedirectUri);
        stored.HasClientSecret.Should().BeTrue();
        reopened.ReadClientSecret().Should().Be(Secret);
    }

    /// <summary>
    /// The form cannot show a secret back, so it cannot ask for it again either. Correcting a typo
    /// in the client id must not cost the operator a trip to Google Cloud.
    /// </summary>
    [Fact]
    public void Saving_with_no_secret_keeps_the_one_already_there()
    {
        var store = Build(Protector());
        store.Save(ClientId, Secret, RedirectUri);

        store.Save("corrected.apps.googleusercontent.com", clientSecret: null, RedirectUri);

        store.Read()!.ClientId.Should().Be("corrected.apps.googleusercontent.com");
        store.ReadClientSecret().Should().Be(Secret);
    }

    [Fact]
    public void A_new_secret_replaces_the_old_one()
    {
        var store = Build(Protector());
        store.Save(ClientId, Secret, RedirectUri);

        store.Save(ClientId, "GOCSPX-rotated", RedirectUri);

        store.ReadClientSecret().Should().Be("GOCSPX-rotated");
    }

    /// <summary>
    /// The redeploy that loses filesystem-held Data Protection keys, reproduced. The screen has to
    /// say the secret is not set — because it is not usable — rather than claim it is stored and
    /// send the operator hunting through Google Cloud for a fault that is on this side.
    /// </summary>
    [Fact]
    public void A_secret_written_under_a_lost_key_reads_as_absent_rather_than_throwing()
    {
        Build(Protector()).Save(ClientId, Secret, RedirectUri);

        var afterKeyLoss = Build(Protector());

        afterKeyLoss.ReadClientSecret().Should().BeNull();

        var stored = afterKeyLoss.Read();
        stored.Should().NotBeNull();
        stored!.HasClientSecret.Should().BeFalse();
        stored.ClientId.Should().Be(ClientId, "the rest of the client is not encrypted and is still true");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"clientId\":\"x\"}")]
    public void A_file_that_cannot_be_understood_is_no_credentials_rather_than_a_crash(string content)
    {
        File.WriteAllText(_path, content);

        // The accounts screen is where an operator would go to fix this. It has to render.
        Build(Protector()).Read().Should().BeNull();
    }

    [Fact]
    public void Clearing_removes_the_file_and_says_whether_there_was_anything_to_remove()
    {
        var store = Build(Protector());

        store.Clear().Should().BeFalse("nothing has been saved");

        store.Save(ClientId, Secret, RedirectUri);
        store.Clear().Should().BeTrue();

        File.Exists(_path).Should().BeFalse();
        store.Read().Should().BeNull();
        store.ReadClientSecret().Should().BeNull();
    }

    [Fact]
    public void The_store_creates_its_own_directory()
    {
        var nested = Path.Combine(_path + ".d", "nested", "google-oauth.json");

        try
        {
            new FileGoogleOAuthCredentialStore(
                nested,
                Protector(),
                NullLogger<FileGoogleOAuthCredentialStore>.Instance)
                .Save(ClientId, Secret, RedirectUri);

            File.Exists(nested).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(_path + ".d")) Directory.Delete(_path + ".d", recursive: true);
        }
    }

    private FileGoogleOAuthCredentialStore Build(ITokenProtector protector) =>
        new(_path, protector, NullLogger<FileGoogleOAuthCredentialStore>.Instance);

    /// <summary>
    /// A fresh key ring each time. Two protectors from this method cannot read each other, which is
    /// exactly the redeploy the design put the real key ring in the database to avoid.
    /// </summary>
    private static ITokenProtector Protector() =>
        new DataProtectionTokenProtector(
            new EphemeralDataProtectionProvider(),
            NullLogger<DataProtectionTokenProtector>.Instance);
}
