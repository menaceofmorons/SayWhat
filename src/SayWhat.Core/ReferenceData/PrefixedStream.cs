namespace SayWhat.Core.ReferenceData;

internal sealed class PrefixedStream : Stream
{
    private readonly byte[] prefix;
    private readonly Stream rest;
    private int position;

    public PrefixedStream(byte[] prefix, Stream rest)
    {
        this.prefix = prefix;
        this.rest = rest;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (position < prefix.Length)
        {
            var take = Math.Min(count, prefix.Length - position);
            Array.Copy(prefix, position, buffer, offset, take);
            position += take;
            return take;
        }

        return rest.Read(buffer, offset, count);
    }

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
            rest.Dispose();
        }

        base.Dispose(disposing);
    }
}
