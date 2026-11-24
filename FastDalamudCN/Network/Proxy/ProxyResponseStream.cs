namespace FastDalamudCN.Network.Proxy;

internal sealed class ProxyResponseStream(Stream stream, HttpContent originalContent, ProxyStatistics statistics) : Stream
{
    public override bool CanRead => stream.CanRead;
    public override bool CanSeek => stream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => stream.Length;

    public override long Position
    {
        get => stream.Position;
        set => stream.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => RecordRead(stream.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => RecordRead(stream.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        RecordRead(await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        RecordRead(await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);

    public override void Flush() => stream.Flush();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            originalContent.Dispose();
        }

        base.Dispose(disposing);
    }

    private int RecordRead(int bytesRead)
    {
        statistics.RecordDownloadedBytes(bytesRead);
        return bytesRead;
    }
}
