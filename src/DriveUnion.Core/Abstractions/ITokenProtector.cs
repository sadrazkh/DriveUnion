namespace DriveUnion.Core.Abstractions;

/// <summary>
/// Encrypts the Google refresh and access tokens at rest, so a database dump is not a set of keys
/// to the operator's Drive.
///
/// The implementation must persist its key material somewhere that survives a redeploy. Keys held
/// only in a container filesystem orphan every stored token on the next deploy, and the symptom is
/// both Google accounts appearing to have spontaneously disconnected.
/// </summary>
public interface ITokenProtector
{
    string Protect(string plaintext);

    /// <summary>
    /// Returns null when the payload cannot be unprotected — a rotated or lost key — so the caller
    /// can say "reconnect this account" instead of throwing an unexplained cryptographic error at
    /// whoever happened to trigger the refresh.
    /// </summary>
    string? Unprotect(string protectedValue);
}
