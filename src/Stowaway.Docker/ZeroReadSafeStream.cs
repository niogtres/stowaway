namespace Stowaway.Docker;

/// <summary>
/// Read-only wrapper that answers a zero-byte read with 0 instead of passing it on. Docker.DotNet's chunked response
/// stream throws "The connection closed in the middle of a chunk" on a zero-byte read, and <c>TarReader</c> issues one
/// at the end of every entry, so reading an archive from <c>GET /containers/{id}/archive</c> died after the first file.
/// </summary>
public sealed class ZeroReadSafeStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        count == 0 ? 0 : inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) =>
        buffer.IsEmpty ? 0 : inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        count == 0 ? Task.FromResult(0) : inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        buffer.IsEmpty ? ValueTask.FromResult(0) : inner.ReadAsync(buffer, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
