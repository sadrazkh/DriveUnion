using System.Text.Json;
using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.Google;
using FluentAssertions;

namespace DriveUnion.Tests.Google;

/// <summary>
/// The one-time carry of <c>App_Data/google-oauth.json</c> into the database.
///
/// The file is what this change exists to retire, but a deployment that still has one must not be
/// asked to re-paste two strings it already gave the panel — and a deployment that has already lost
/// its file must not be asked anything at all. So: nothing happens when there is no file, exactly
/// one row appears when there is, and running again produces neither a second row nor a second
/// sentence in the log.
/// </summary>
public sealed class GoogleOAuthClientImportTests : IDisposable
{
    private const string ClientId = "from-the-file.apps.googleusercontent.com";
    private const string RedirectUri = "https://drive.example/accounts/callback";
    private const string Secret = "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e";

    private readonly GoogleClientStoreHarness _harness = new();

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"driveunion-import-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        _harness.Dispose();

        foreach (var path in new[] { _path, _path + GoogleOAuthClientImport.ImportedSuffix })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void With_no_file_nothing_is_imported_and_nothing_is_touched()
    {
        _harness.Import(_path).Run().Should().BeFalse();

        _harness.Store().List().Should().BeEmpty();
    }

    [Fact]
    public void A_file_the_old_store_wrote_becomes_a_row_with_its_secret_intact()
    {
        WriteFile(Secret);

        _harness.Import(_path).Run().Should().BeTrue();

        var stored = _harness.Store().List().Should().ContainSingle().Subject;

        stored.ClientId.Should().Be(ClientId);
        stored.RedirectUri.Should().Be(RedirectUri);
        stored.HasClientSecret.Should().BeTrue();
        stored.IsDefault.Should().BeTrue("it is the only client, so it is the one new connections use");

        _harness.Store().ReadSecret(stored.Id).Should().Be(
            Secret,
            "the deployment that still has this file must not have to paste the secret again");
    }

    /// <summary>
    /// Twice is once. The guard is two-sided on purpose: the client id already being a row covers an
    /// ordinary restart, and the file being renamed aside covers the case that guard cannot — an
    /// operator who imports a client and then deliberately removes it from the panel must not find
    /// it back after the next restart.
    /// </summary>
    [Fact]
    public void Importing_twice_writes_one_row()
    {
        WriteFile(Secret);

        _harness.Import(_path).Run().Should().BeTrue();
        _harness.Import(_path).Run().Should().BeFalse();

        _harness.Store().List().Should().ContainSingle();

        File.Exists(_path).Should().BeFalse("the file is renamed aside once its contents are rows");
        File.Exists(_path + GoogleOAuthClientImport.ImportedSuffix).Should().BeTrue(
            "it is moved and not deleted — it holds the only other copy of a credential");
    }

    [Fact]
    public void A_client_already_in_the_database_is_not_imported_a_second_time()
    {
        _harness.Store().Save(id: null, ClientId, "GOCSPX-already-here", RedirectUri);

        WriteFile(Secret);

        _harness.Import(_path).Run().Should().BeFalse();

        var stored = _harness.Store().List().Should().ContainSingle().Subject;
        _harness.Store().ReadSecret(stored.Id).Should().Be(
            "GOCSPX-already-here",
            "what is already in the database is the newer of the two and must not be overwritten");
    }

    /// <summary>
    /// The file's ciphertext was written under a Data Protection key ring. If that ring is gone the
    /// client id and redirect URI are still true and still worth having — the operator's only
    /// remaining action is to paste the secret, and a screen that claimed one was stored would send
    /// them looking for the fault in Google Cloud.
    /// </summary>
    [Fact]
    public void A_secret_the_key_ring_can_no_longer_read_still_imports_the_rest_of_the_client()
    {
        WriteFile(Secret);

        _harness.Import(_path, GoogleClientStoreHarness.NewProtector()).Run().Should().BeTrue();

        var stored = _harness.Store().List().Should().ContainSingle().Subject;

        stored.ClientId.Should().Be(ClientId);
        stored.HasClientSecret.Should().BeFalse();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"clientId\":\"x\"}")]
    public void A_file_that_cannot_be_understood_imports_nothing_and_does_not_throw(string content)
    {
        File.WriteAllText(_path, content);

        _harness.Import(_path).Run().Should().BeFalse();

        _harness.Store().List().Should().BeEmpty();

        // Left where it is. Nothing was taken out of it, so nothing has been carried anywhere and
        // renaming it would only hide the file somebody has to look at.
        File.Exists(_path).Should().BeTrue();
    }

    /// <summary>Exactly the shape the file store used to write, field names and all.</summary>
    private void WriteFile(string secret)
    {
        ITokenProtector protector = _harness.Protector;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("clientId", ClientId);
            writer.WriteString("redirectUri", RedirectUri);
            writer.WriteString("clientSecretProtected", protector.Protect(secret));
            writer.WriteString("updatedAt", new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero));
            writer.WriteEndObject();
        }

        File.WriteAllBytes(_path, buffer.ToArray());
    }
}
