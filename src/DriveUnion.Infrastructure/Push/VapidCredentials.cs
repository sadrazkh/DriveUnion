using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace DriveUnion.Infrastructure.Push;

/// <summary>Why this deployment cannot send a push, or that it can.</summary>
public enum VapidState
{
    /// <summary>Nothing is set. The ordinary state of a development machine and of a fresh deployment.</summary>
    NotConfigured,

    /// <summary>Something is set and it is not a usable P-256 key pair, or the contact is not a contact.</summary>
    Unusable,

    Ready,
}

/// <summary>
/// The credential picture as the screen needs it: what state it is in, and the one value that is
/// safe to print.
/// </summary>
/// <param name="PublicKey">
/// The application server key, base64url, which is exactly what a browser is handed as
/// <c>applicationServerKey</c>. Public by design — it is published to every subscriber — and null
/// unless the pair is <see cref="VapidState.Ready"/>, so a half-configured deployment cannot render
/// a subscribe button that mints subscriptions nothing can ever send to.
/// </param>
/// <param name="Problem">
/// What is wrong, in the operator's own configuration vocabulary. Never a key and never part of one.
/// </param>
public sealed record VapidStatus(VapidState State, string? PublicKey, string? Problem)
{
    public bool IsReady => State is VapidState.Ready;
}

/// <summary>
/// Where the application server's identity comes from, and where it must not come from.
/// </summary>
public interface IVapidCredentials
{
    VapidStatus Describe();

    /// <summary>
    /// The signing key, or null when this deployment has none. The caller disposes it.
    ///
    /// <para>Null rather than an exception, for the reason <c>GoogleOAuthCredentialResolver.Value</c>
    /// gives: not being configured is a state the panel renders rather than an error it unwinds on.
    /// The one place it becomes a refusal is the moment something actually tries to send.</para>
    /// </summary>
    ECDsa? CreateSigningKey();

    /// <summary>The <c>sub</c> claim: who to complain to about this server.</summary>
    string Subject { get; }
}

/// <summary>
/// VAPID keys, read from configuration and from nowhere else.
///
/// <para><b>They are never in the repository.</b> The private half is what proves a message came
/// from this deployment; committing it would let anybody who has read the source post to every
/// device this panel has ever registered. So it is <c>Push:PrivateKey</c> — environment,
/// user-secrets, a platform's secret store — and <c>appsettings.json</c> carries the key with an
/// empty value as documentation of its existence, exactly as <c>Google:ClientSecret</c> does. Blank
/// counts as absent for the same reason it does there.</para>
///
/// <para><b>And they are not in the database.</b> Google's OAuth client ended up in a table because
/// it is a per-operator setting somebody types into a running panel and because a redeploy had
/// already destroyed one copy. This is not that: it is one key pair for the whole deployment, it is
/// generated once, and a panel screen that could display or replace it would be a screen that can
/// silently orphan every subscription in the table — a new key pair does not invalidate old
/// subscriptions, it makes every send to them fail with a 403 that names nothing.</para>
///
/// <para><b>An absent key pair is a state, not a failure.</b> The panel boots, the notifications
/// screen renders, and it says the operator has not set this up — the same bargain the accounts
/// screen makes with a missing Google client. What must not happen is the subscribe control being
/// offered anyway: a browser will happily mint a subscription against a made-up application server
/// key, and every send to it is then a 403 for the life of the row.</para>
/// </summary>
public sealed class VapidCredentials : IVapidCredentials
{
    /// <summary>The configuration section, so one string is spelled in one place.</summary>
    public const string SectionName = "Push";

    public const string PublicKeyKey = "PublicKey";

    public const string PrivateKeyKey = "PrivateKey";

    public const string SubjectKey = "Subject";

    /// <summary>
    /// What <c>sub</c> says when the operator has not said anything.
    ///
    /// <para>A push service uses it to reach whoever is sending, and RFC 8292 only requires it to be
    /// a <c>mailto:</c> or an <c>https:</c> URI. This is a URI that reaches nobody, which is worse
    /// than a real address and better than an absent claim — some services refuse a token without
    /// one. The screen says it is unset so an operator can fix it.</para>
    /// </summary>
    public const string UnsetSubject = "mailto:operator@localhost";

    /// <summary>The 32-byte private scalar of a P-256 key.</summary>
    private const int PrivateKeyLength = 32;

    private readonly IConfiguration _section;

    /// <param name="section">The <c>Push</c> section, not the root.</param>
    public VapidCredentials(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        _section = section;
    }

    public string Subject =>
        Read(SubjectKey) is { } configured && IsContact(configured) ? configured : UnsetSubject;

    public VapidStatus Describe()
    {
        var publicKey = Read(PublicKeyKey);
        var privateKey = Read(PrivateKeyKey);

        if (publicKey is null && privateKey is null)
        {
            return new VapidStatus(VapidState.NotConfigured, null, null);
        }

        // Half-set is reported as its own problem rather than as «invalid». It is by far the
        // commonest way this goes wrong — a public key pasted into the environment and a private key
        // that never left the machine it was generated on — and «Push:PrivateKey is not set» is an
        // instruction, where «the key pair is not usable» is a puzzle.
        if (publicKey is null) return Unusable($"{SectionName}:{PublicKeyKey} is not set.");
        if (privateKey is null) return Unusable($"{SectionName}:{PrivateKeyKey} is not set.");

        if (Base64UrlText.Decode(publicKey) is not { } publicBytes)
        {
            return Unusable($"{SectionName}:{PublicKeyKey} is not base64.");
        }

        if (publicBytes.Length != WebPushEncryption.PublicKeyLength || publicBytes[0] != 0x04)
        {
            return Unusable(
                $"{SectionName}:{PublicKeyKey} is {publicBytes.Length} bytes; an uncompressed P-256 "
                + $"public key is {WebPushEncryption.PublicKeyLength} and begins 0x04.");
        }

        if (Base64UrlText.Decode(privateKey) is not { Length: PrivateKeyLength })
        {
            return Unusable(
                $"{SectionName}:{PrivateKeyKey} is not {PrivateKeyLength} base64 bytes; a P-256 "
                + "private key is exactly that.");
        }

        // The two are checked against each other by signing and verifying, which is the only test
        // that catches the pair being from two different generations. Every push service answers a
        // mismatched pair with a 403 and no detail, on every send, for ever — and nothing else in
        // this product would ever say why. Refusing here turns that into a sentence on a screen.
        try
        {
            using var key = CreateSigningKey();

            if (key is null) return Unusable("The key pair could not be loaded.");

            var probe = "vapid"u8.ToArray();
            var signature = key.SignData(probe, HashAlgorithmName.SHA256);

            if (!key.VerifyData(probe, signature, HashAlgorithmName.SHA256))
            {
                return Unusable(
                    $"{SectionName}:{PublicKeyKey} is not the public half of {SectionName}:{PrivateKeyKey}.");
            }
        }
        catch (CryptographicException exception)
        {
            // Which platform refuses a mismatched pair, and at which call, is not something to rely
            // on: OpenSSL checks the pair on import and Windows does not always. Both ends of that
            // arrive here.
            return Unusable($"The key pair was refused: {exception.Message}");
        }

        return new VapidStatus(
            VapidState.Ready,

            // Re-encoded from the bytes rather than passed through. What the browser is handed has
            // to be unpadded base64url, and an operator who pasted standard base64 out of a key
            // generator would otherwise have a subscribe call that throws in the browser with
            // «InvalidCharacterError» and nothing on the server saying anything at all.
            Base64UrlText.Encode(publicBytes),
            Read(SubjectKey) is null ? $"{SectionName}:{SubjectKey} is not set." : null);
    }

    public ECDsa? CreateSigningKey()
    {
        if (Base64UrlText.Decode(Read(PublicKeyKey)) is not { } publicBytes) return null;
        if (Base64UrlText.Decode(Read(PrivateKeyKey)) is not { Length: PrivateKeyLength } privateBytes) return null;
        if (publicBytes.Length != WebPushEncryption.PublicKeyLength || publicBytes[0] != 0x04) return null;

        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateBytes,
            Q = WebPushEncryption.PointOf(publicBytes),
        });
    }

    /// <summary>
    /// A fresh application server key pair, as the two configuration values.
    ///
    /// <para>Here rather than behind a button. A panel that could mint these would be a panel that
    /// can replace them, and replacing them does not invalidate a single existing subscription — it
    /// makes every send to all of them fail with a 403 for ever. Generating is a thing an operator
    /// does once, on a console, before the deployment starts; this is what the tests use and what
    /// the documentation points at.</para>
    /// </summary>
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(true);

        return (
            Base64UrlText.Encode(WebPushEncryption.UncompressedPoint(parameters.Q)),
            Base64UrlText.Encode(LeftPad(parameters.D!, PrivateKeyLength)));
    }

    private static VapidStatus Unusable(string problem) => new(VapidState.Unusable, null, problem);

    /// <summary>Blank counts as absent — see the class remarks, and <c>GoogleOAuthCredentialResolver</c>.</summary>
    private string? Read(string key) =>
        _section[key] is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool IsContact(string value) =>
        value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The same right-alignment <c>UncompressedPoint</c> does, for the same reason: .NET exports the
    /// private scalar minimally, so one key in 256 is 31 bytes and a consumer expecting 32 refuses it.
    /// </summary>
    private static byte[] LeftPad(byte[] value, int length)
    {
        if (value.Length == length) return value;

        var padded = new byte[length];
        value.CopyTo(padded, length - value.Length);

        return padded;
    }
}
