namespace CrossVault.FoxDbf;

/// <summary>
/// A read-only, seekable view of an inner stream that starts at a fixed base offset: position 0
/// of the view is <c>baseOffset</c> of the inner stream, and the view runs to the inner stream's end
/// (<see cref="Length"/> excludes only the bytes before the base; there is no end bound). Lets <see cref="DbfTable.Open(Stream, Stream?, DbfOptions?, bool)"/> honour the
/// caller's start position while the header parser and the memo readers keep seeking from 0.
/// Disposing the view disposes the inner stream; ownership is decided by whoever holds the view.
/// </summary>
internal sealed class OffsetReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _base;

    internal OffsetReadStream(Stream inner, long baseOffset)
    {
        _inner = inner;
        _base = baseOffset;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => Math.Max(0L, _inner.Length - _base);

    public override long Position
    {
        get => _inner.Position - _base;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        _inner.Seek(_base + target, SeekOrigin.Begin);
        return target;
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override int ReadByte() => _inner.ReadByte();

    // Forwards so DbfTable.RefreshView still drops a wrapped FileStream's read buffer.
    public override void Flush() => _inner.Flush();

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
