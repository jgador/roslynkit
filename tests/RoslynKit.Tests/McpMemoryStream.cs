using System.Threading.Channels;

namespace RoslynKit.Tests;

/// <summary>
/// Connects real protocol transports through asynchronous in-memory byte streams.
/// </summary>
internal sealed class McpMemoryStream(ChannelReader<byte[]> reader, ChannelWriter<byte[]> writer) : Stream
{
    private byte[] _buffer = [];
    private int _offset;

    public static (McpMemoryStream Client, McpMemoryStream Server) CreatePair()
    {
        var incoming = Channel.CreateUnbounded<byte[]>();
        var outgoing = Channel.CreateUnbounded<byte[]>();
        return (new McpMemoryStream(incoming.Reader, outgoing.Writer), new McpMemoryStream(outgoing.Reader, incoming.Writer));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset == _buffer.Length)
        {
            if (!await reader.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }

            _buffer = await reader.ReadAsync(cancellationToken);
            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _buffer.Length - _offset);
        _buffer.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return writer.WriteAsync(buffer.ToArray(), cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => writer.TryWrite(buffer.AsSpan(offset, count).ToArray());
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        writer.TryComplete();
        base.Dispose(disposing);
    }
}
