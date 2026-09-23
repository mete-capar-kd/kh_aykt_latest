using Hackathon.Assessment.Api.Domain;

namespace Hackathon.Assessment.Api.Snapshot;

internal sealed class BoundedReadStream(Stream inner, long maximumBytes) : Stream
{
    private long _bytesRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, LimitReadSize(count));
        TrackRead(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var maximumRead = LimitReadSize(buffer.Length);
        var read = inner.Read(buffer[..maximumRead]);
        TrackRead(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var maximumRead = LimitReadSize(buffer.Length);
        var read = await inner.ReadAsync(buffer[..maximumRead], cancellationToken);
        TrackRead(read);
        return read;
    }

    public override void Flush() => throw new NotSupportedException();
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

    private int LimitReadSize(int requested)
    {
        var remaining = maximumBytes - _bytesRead;
        if (remaining < 0)
        {
            throw new SnapshotLimitExceededException(
                "The repository snapshot exceeds its configured limits.");
        }

        return remaining >= requested ? requested : (int)(remaining + 1);
    }

    private void TrackRead(int read)
    {
        _bytesRead += read;
        if (_bytesRead > maximumBytes)
        {
            throw new SnapshotLimitExceededException(
                "The repository snapshot exceeds its configured limits.");
        }
    }
}
