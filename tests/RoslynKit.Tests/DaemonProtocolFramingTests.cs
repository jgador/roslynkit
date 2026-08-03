using System.Buffers.Binary;

namespace RoslynKit.Tests;

public sealed class DaemonProtocolFramingTests
{
    [Fact]
    public async Task WriteRequestAsync_WritesFourByteLittleEndianPayloadLength()
    {
        await using var stream = new MemoryStream();

        await DaemonProtocol.WriteRequestAsync(stream, CreateRequest(), TestContext.Current.CancellationToken);

        var frame = stream.ToArray();
        Assert.True(frame.Length > sizeof(int));
        Assert.Equal(frame.Length - sizeof(int), BinaryPrimitives.ReadInt32LittleEndian(frame));
    }

    [Fact]
    public async Task ReadRequestAsync_ReassemblesFragmentedHeaderAndPayload()
    {
        var request = CreateRequest();
        await using var encoded = new MemoryStream();
        await DaemonProtocol.WriteRequestAsync(encoded, request, TestContext.Current.CancellationToken);
        await using var fragmented = new FragmentedReadStream(encoded.ToArray(), maximumReadLength: 1);

        var decoded = Assert.IsType<DaemonCommandRequest>(
            await DaemonProtocol.ReadRequestAsync(fragmented, TestContext.Current.CancellationToken));

        Assert.Equal(request.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(request.RequestId, decoded.RequestId);
        Assert.Equal(request.CommandName, decoded.CommandName);
        Assert.Equal(request.Options, decoded.Options);
        Assert.Equal(request.DeadlineUtc, decoded.DeadlineUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(DaemonProtocol.MaxRequestFrameLength + 1)]
    public async Task ReadRequestAsync_RejectsInvalidLengthBeforeReadingPayload(int length)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await using var stream = new MemoryStream(header);

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => DaemonProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken));

        Assert.Equal(DaemonProtocolError.InvalidFrameLength, exception.Error);
    }

    [Theory]
    [MemberData(nameof(TruncatedFrames))]
    public async Task ReadRequestAsync_RejectsTruncatedFrames(byte[] frame)
    {
        await using var stream = new MemoryStream(frame);

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => DaemonProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken));

        Assert.Equal(DaemonProtocolError.UnexpectedEndOfStream, exception.Error);
    }

    [Fact]
    public async Task ReadRequestAsync_RejectsInvalidUtf8()
    {
        await using var stream = CreateFrame([0xff, 0xff]);

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => DaemonProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken));

        Assert.Equal(DaemonProtocolError.InvalidUtf8, exception.Error);
    }

    [Fact]
    public async Task ReadRequestAsync_RejectsQuotedNumericProperties()
    {
        const string json = "{\"messageType\":\"handshake\",\"protocolVersion\":\"1\",\"requestId\":\"10cb0757-c567-4276-a1a8-ec566d54cdcd\"}";
        await using var stream = CreateFrame(System.Text.Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => DaemonProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken));

        Assert.Equal(DaemonProtocolError.InvalidJson, exception.Error);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"messageType\":\"unknown\",\"protocolVersion\":1,\"requestId\":\"10cb0757-c567-4276-a1a8-ec566d54cdcd\"}")]
    public async Task ReadRequestAsync_RejectsInvalidJsonMessage(string json)
    {
        await using var stream = CreateFrame(System.Text.Encoding.UTF8.GetBytes(json));

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => DaemonProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken));

        Assert.Equal(DaemonProtocolError.InvalidJson, exception.Error);
    }

    [Fact]
    public async Task ReadRequestAsync_PropagatesCancellation()
    {
        await using var stream = new CancelableReadStream();
        using var cancellation = new CancellationTokenSource();
        var read = DaemonProtocol.ReadRequestAsync(stream, cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task ReadRequestAsync_DistinguishesCleanDisconnectBeforeNextFrame()
    {
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => DaemonProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken));

        Assert.Equal(DaemonProtocolError.EndOfStream, exception.Error);
    }

    [Fact]
    public async Task WriteRequestAsync_RejectsPayloadAboveRequestLimit()
    {
        var request = CreateRequest() with
        {
            Options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = new('x', DaemonProtocol.MaxRequestFrameLength),
            },
        };
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => DaemonProtocol.WriteRequestAsync(stream, request, TestContext.Current.CancellationToken));

        Assert.Equal(DaemonProtocolError.FrameTooLarge, exception.Error);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task ReadResponseAsync_RejectsLengthAboveResponseLimitBeforeReadingPayload()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, DaemonProtocol.MaxResponseFrameLength + 1);
        await using var stream = new MemoryStream(header);

        var exception = await Assert.ThrowsAsync<DaemonProtocolException>(
            () => DaemonProtocol.ReadResponseAsync(stream, TestContext.Current.CancellationToken));

        Assert.Equal(DaemonProtocolError.InvalidFrameLength, exception.Error);
    }

    public static TheoryData<byte[]> TruncatedFrames => new()
    {
        new byte[] { 1, 0 },
        CreateTruncatedPayload(),
    };

    private static DaemonCommandRequest CreateRequest()
    {
        return new DaemonCommandRequest(
            RoslynKitBuildInfo.DaemonProtocolVersion,
            Guid.Parse("10cb0757-c567-4276-a1a8-ec566d54cdcd"),
            "symbols",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["target"] = Path.GetFullPath("RoslynKit.slnx"),
                ["query"] = "DaemonProtocol",
            },
            DateTimeOffset.Parse("2026-07-30T12:00:00+00:00"));
    }

    private static MemoryStream CreateFrame(byte[] payload)
    {
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        return new MemoryStream(frame);
    }

    private static byte[] CreateTruncatedPayload()
    {
        var frame = new byte[sizeof(int) + 2];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 10);
        frame[4] = (byte)'{';
        frame[5] = (byte)'}';
        return frame;
    }

    private sealed class FragmentedReadStream(byte[] bytes, int maximumReadLength) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumReadLength)], cancellationToken);
        }
    }

    private sealed class CancelableReadStream : Stream
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

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
