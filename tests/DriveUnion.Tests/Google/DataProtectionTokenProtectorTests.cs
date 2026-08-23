using DriveUnion.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveUnion.Tests.Google;

public class DataProtectionTokenProtectorTests
{
    [Fact]
    public void A_token_survives_the_round_trip()
    {
        var protector = Build();

        var ciphertext = protector.Protect("1//0gRefreshTokenShapedString");

        ciphertext.Should().NotBe("1//0gRefreshTokenShapedString");
        protector.Unprotect(ciphertext).Should().Be("1//0gRefreshTokenShapedString");
    }

    [Fact]
    public void A_payload_that_will_not_decrypt_comes_back_as_null()
    {
        var protector = Build();

        // Null, not an exception. Whoever triggered this was uploading a chunk; a cryptographic
        // stack trace is not their error, and "reconnect this account" is the only useful answer.
        protector.Unprotect("this is not a protected value").Should().BeNull();
    }

    [Fact]
    public void A_payload_from_a_different_key_ring_comes_back_as_null()
    {
        var written = Build();
        var ciphertext = written.Protect("1//0gRefreshTokenShapedString");

        // This is the redeploy that loses filesystem-held keys, reproduced: same purpose, same code,
        // different key material.
        var readBack = Build();

        readBack.Unprotect(ciphertext).Should().BeNull();
    }

    [Fact]
    public void An_empty_column_is_not_a_cryptographic_failure()
    {
        Build().Unprotect(string.Empty).Should().BeNull();
    }

    private static DataProtectionTokenProtector Build() =>
        new(new EphemeralDataProtectionProvider(), NullLogger<DataProtectionTokenProtector>.Instance);
}
