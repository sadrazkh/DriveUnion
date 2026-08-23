using DriveUnion.Core.Abstractions;

namespace DriveUnion.Tests.Http;

/// <summary>
/// A Drive whose body dies partway through.
///
/// <see cref="Fakes.FakeDriveClient"/> hands back a <see cref="MemoryStream"/>, which cannot fail;
/// but spec §9 says a Drive stream that dies mid-response cannot change a status code already sent,
/// and that behaviour needs a stream that actually breaks. This wrapper serves a first read and then
/// throws, which is the shape of a connection Google dropped.
/// </summary>
internal sealed class DyingStreamDriveClient(IDriveClient inner, int bytesBeforeFailure) : IDriveClient
{
    public async Task<DriveDownload> OpenDownloadAsync(
        Guid accountId,
        string driveFileId,
        string? rangeHeader,
        CancellationToken cancellationToken)
    {
        var download = await inner.OpenDownloadAsync(accountId, driveFileId, rangeHeader, cancellationToken);

        // The declared length is left as Drive stated it. That is the honest reproduction: Google
        // promised n bytes in its headers and then failed to deliver them.
        return new DriveDownload(
            new DyingStream(download.Content, bytesBeforeFailure),
            download.ContentType,
            download.ContentLength,
            download.ContentRange,
            download.IsPartial,
            download);
    }

    public Task<DriveResumableSession> BeginResumableUploadAsync(
        Guid accountId,
        DriveUploadRequest request,
        CancellationToken cancellationToken) =>
        inner.BeginResumableUploadAsync(accountId, request, cancellationToken);

    public Task<DriveChunkOutcome> WriteChunkAsync(
        Uri sessionUri,
        Stream content,
        long offset,
        long length,
        long totalSize,
        CancellationToken cancellationToken) =>
        inner.WriteChunkAsync(sessionUri, content, offset, length, totalSize, cancellationToken);

    public Task<long> GetConfirmedLengthAsync(
        Uri sessionUri,
        long totalSize,
        CancellationToken cancellationToken) =>
        inner.GetConfirmedLengthAsync(sessionUri, totalSize, cancellationToken);

    public Task<string> EnsureFolderAsync(
        Guid accountId,
        string folderName,
        string? parentFolderId,
        CancellationToken cancellationToken) =>
        inner.EnsureFolderAsync(accountId, folderName, parentFolderId, cancellationToken);

    public Task<DriveStorageQuota> GetStorageQuotaAsync(Guid accountId, CancellationToken cancellationToken) =>
        inner.GetStorageQuotaAsync(accountId, cancellationToken);

    /// <summary>
    /// Serves at most <c>budget</c> bytes on the first read and throws on the next one, whatever
    /// buffer size the copy uses. A budget expressed as "fail after n total bytes" would be silently
    /// skipped by a copy whose buffer is larger than the file.
    /// </summary>
    private sealed class DyingStream(Stream inner, int budget) : Stream
    {
        private bool served;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (served) throw new IOException("The Drive response body was cut off.");

            served = true;

            return inner.Read(buffer, offset, Math.Min(budget, count));
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (served) throw new IOException("The Drive response body was cut off.");

            served = true;
            var take = Math.Min(budget, buffer.Length);

            return await inner.ReadAsync(buffer[..take], cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
