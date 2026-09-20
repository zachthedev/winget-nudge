namespace WingetNudge.Core.Tools;

/// <summary>
/// Reads at most a fixed number of bytes from another stream. A response body is attacker-shaped
/// once a user can register the URL it comes from, and a client timeout bounds duration rather
/// than size.
/// </summary>
/// <param name="inner">Stream to read from.</param>
/// <param name="limit">Largest number of bytes to pass through.</param>
public sealed class BoundedStream(Stream inner, long limit) : Stream
{
    private long _read;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        int allowed = Allowed(buffer.Length);
        if (allowed == 0)
        {
            return 0;
        }

        int read = inner.Read(buffer[..allowed]);
        _read += read;
        return read;
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int allowed = Allowed(buffer.Length);
        if (allowed == 0)
        {
            return 0;
        }

        int read = await inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        _read += read;
        return read;
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Bytes still inside the limit, capped at what the caller asked for.</summary>
    /// <param name="requested">Bytes the caller wants.</param>
    /// <returns>Bytes to read, zero once the limit is reached.</returns>
    private int Allowed(int requested) => (int)Math.Max(0, Math.Min(requested, limit - _read));
}
