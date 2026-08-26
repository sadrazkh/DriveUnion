using System.Collections.Concurrent;

namespace DriveUnion.Core.Uploads;

/// <summary>
/// The content keys of encrypted fetches that have not finished yet — in memory, and nowhere else.
///
/// <para><b>What this is instead of.</b> An encrypted fetch is asked for by a web request and
/// carried out by a worker minutes later, so something has to hold the key between them. Persisting
/// it would put the thing that opens the file in the same database as the wrapped copy, which is
/// the entire point of wrapping it — a stolen database would carry both halves. So it is held in
/// this process and written down nowhere.</para>
///
/// <para><b>What that costs, said plainly.</b> A restart loses every key for a fetch still in
/// flight, and those fetches fail and tell the customer to start again with their secret. That is
/// the honest failure: what is left on disk after a restart has never included anything that opens
/// a file, which is the property being paid for.</para>
///
/// <para><b>What it is not.</b> It is not a defence against the operator. This server is fetching
/// the plaintext and encrypting it; it holds the bytes and the key for the length of the transfer
/// whatever this class does. It exists so the <i>durable</i> state never does — see
/// <see cref="Storage.SealedBy.Server"/>, which is where that distinction is written down for the
/// customer rather than for a reader of this file.</para>
/// </summary>
public sealed class FetchKeyring
{
    private readonly ConcurrentDictionary<Guid, byte[]> _keys = new();

    /// <summary>How many are being held. For a health check, and for a test that asserts a release.</summary>
    public int Count => _keys.Count;

    public void Hold(Guid fetchId, byte[] contentKey)
    {
        ArgumentNullException.ThrowIfNull(contentKey);

        _keys[fetchId] = contentKey;
    }

    /// <summary>The key for this fetch, or null when the process no longer has it.</summary>
    public byte[]? Get(Guid fetchId) => _keys.GetValueOrDefault(fetchId);

    /// <summary>
    /// Forgets it, and clears the bytes before letting go.
    ///
    /// <para>Zeroing a managed array is not a guarantee — the GC may have copied it already — and it
    /// is still worth doing: it shortens the window in which a heap dump of a long-running process
    /// contains a key for a file that finished uploading an hour ago.</para>
    /// </summary>
    public void Release(Guid fetchId)
    {
        if (_keys.TryRemove(fetchId, out var key)) Array.Clear(key);
    }
}
