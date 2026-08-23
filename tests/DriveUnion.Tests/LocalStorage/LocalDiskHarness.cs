using DriveUnion.Core.Abstractions;
using DriveUnion.Infrastructure.LocalStorage;
using DriveUnion.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveUnion.Tests.LocalStorage;

/// <summary>
/// A local-disk backend in a temporary directory, and the directory cleaned up afterwards.
///
/// Every test gets its own root. The backend is a real one — it writes real files and keeps real
/// upload sessions — so two tests sharing a directory would share upload sessions, and the first
/// flaky failure would be blamed on the code rather than on the harness.
/// </summary>
public sealed class LocalDiskHarness : IDisposable
{
    /// <summary>Drive's required chunk multiple, and the smallest chunk a test can send.</summary>
    public const int Chunk = 256 * 1024;

    public static readonly Guid AccountId = Guid.Parse("6f4a1d5e-0c2b-4a1f-9c3d-2b7e5a8f0d11");

    public LocalDiskHarness(TimeSpan? sessionLifetime = null)
    {
        Sandbox = Path.Combine(
            Path.GetTempPath(),
            "driveunion-localdisk-tests",
            Guid.NewGuid().ToString("N"));

        Root = Path.GetFullPath(Path.Combine(Sandbox, "root"));

        Settings = new LocalDiskDriveOptions
        {
            Enabled = true,
            RootPath = Root,
            SessionLifetime = sessionLifetime ?? TimeSpan.FromDays(7),
        };
    }

    /// <summary>
    /// One level above the storage root, and empty apart from it.
    ///
    /// It is what makes "nothing a customer typed escaped the root" an assertion rather than a hope:
    /// a name that traversed upwards has to land here first, and this directory belongs to one test.
    /// </summary>
    public string Sandbox { get; }

    public string Root { get; }

    public LocalDiskDriveOptions Settings { get; }

    /// <summary>Fixed, because session expiry is a rule the tests set rather than wait for.</summary>
    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// A client over this root. Calling it twice is how a test proves the backend survives the
    /// process that opened an upload: the second instance shares nothing with the first but the disk.
    /// </summary>
    public LocalDiskDriveClient Create() =>
        new(Options.Create(Settings), Clock, NullLogger<LocalDiskDriveClient>.Instance);

    /// <summary>Where the bytes of a finished file actually sit. The layout is part of the contract.</summary>
    public string ContentPath(Guid accountId, string driveFileId) => Path.GetFullPath(Path.Combine(
        Root,
        "accounts",
        accountId.ToString("N"),
        "files",
        driveFileId["ld-".Length..] + ".bin"));

    /// <summary>Deterministic bytes that make an off-by-one in a range visible.</summary>
    public static byte[] Content(int length, int seed = 20260823)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);

        return data;
    }

    /// <summary>The whole upload protocol, one chunk at a time, exactly as the coordinator drives it.</summary>
    public static async Task<DriveFileMetadata> UploadAsync(
        IDriveClient client,
        byte[] content,
        string fileName = "report.pdf",
        string mimeType = "application/pdf",
        int chunkSize = Chunk,
        Guid? accountId = null)
    {
        var account = accountId ?? AccountId;
        var session = await client.BeginResumableUploadAsync(
            account,
            new DriveUploadRequest(fileName, mimeType, content.LongLength, null),
            CancellationToken.None);

        DriveFileMetadata? completed = null;

        for (var offset = 0; offset < content.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, content.Length - offset);
            using var chunk = new MemoryStream(content, offset, length, writable: false);

            var outcome = await client.WriteChunkAsync(
                session.SessionUri, chunk, offset, length, content.LongLength, CancellationToken.None);

            completed ??= outcome.Completed;
        }

        return completed ?? throw new InvalidOperationException(
            "The upload never completed, so there is no metadata for the test to assert on.");
    }

    public static async Task<byte[]> ReadAllAsync(DriveDownload download)
    {
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer);

        return buffer.ToArray();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Sandbox)) Directory.Delete(Sandbox, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that will not delete is the operating system's business, not a
            // reason to fail a test that has already proved what it was written to prove.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A body that dies partway through, the way a chunk upload dies when the client's connection does.
/// </summary>
public sealed class DyingStream(byte[] data, int failAfter) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => data.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (_position >= failAfter)
        {
            throw new IOException("The connection carrying this chunk was reset.");
        }

        var take = Math.Min(count, failAfter - _position);
        Array.Copy(data, _position, buffer, offset, take);
        _position += take;

        return take;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
