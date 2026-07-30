using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynKit;

/// <summary>
/// Reads and writes bounded versioned daemon messages using length-prefixed strict UTF-8 JSON frames.
/// </summary>
internal static class DaemonProtocol
{
    public const int MaxRequestFrameLength = 1024 * 1024;
    public const int MaxResponseFrameLength = 64 * 1024 * 1024;

    private const int HeaderLength = sizeof(int);

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        MaxDepth = 32,
        NumberHandling = JsonNumberHandling.Strict,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static Task WriteRequestAsync(Stream stream, DaemonRequest request, CancellationToken cancellationToken)
    {
        return WriteAsync(stream, request, MaxRequestFrameLength, cancellationToken);
    }

    public static async Task<DaemonRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        return await ReadAsync<DaemonRequest>(stream, MaxRequestFrameLength, cancellationToken).ConfigureAwait(false);
    }

    public static Task WriteResponseAsync(Stream stream, DaemonResponse response, CancellationToken cancellationToken)
    {
        return WriteAsync(stream, response, MaxResponseFrameLength, cancellationToken);
    }

    public static async Task<DaemonResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        return await ReadAsync<DaemonResponse>(stream, MaxResponseFrameLength, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects a decoded message that cannot be dispatched by this exact protocol implementation.
    /// </summary>
    public static void EnsureCompatible(DaemonMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ProtocolVersion != RoslynKitBuildInfo.DaemonProtocolVersion)
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.UnsupportedVersion,
                $"Daemon protocol version {message.ProtocolVersion} is incompatible with version {RoslynKitBuildInfo.DaemonProtocolVersion}.");
        }
    }

    private static async Task WriteAsync<TMessage>(
        Stream stream,
        TMessage message,
        int maximumLength,
        CancellationToken cancellationToken)
        where TMessage : DaemonMessage
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateMessage(message);

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                "The daemon message could not be serialized.",
                exception);
        }

        if (payload.Length > maximumLength)
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.FrameTooLarge,
                $"The daemon message is {payload.Length} bytes; the frame limit is {maximumLength} bytes.");
        }

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TMessage> ReadAsync<TMessage>(
        Stream stream,
        int maximumLength,
        CancellationToken cancellationToken)
        where TMessage : DaemonMessage
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength];
        await ReadExactlyAsync(
            stream,
            header,
            "frame header",
            allowCleanEndOfStream: true,
            cancellationToken).ConfigureAwait(false);

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength <= 0 || payloadLength > maximumLength)
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.InvalidFrameLength,
                $"Daemon frame length {payloadLength} is outside the allowed range 1..{maximumLength}.");
        }

        var rentedPayload = ArrayPool<byte>.Shared.Rent(payloadLength);
        try
        {
            var payload = rentedPayload.AsMemory(0, payloadLength);
            await ReadExactlyAsync(
                stream,
                payload,
                "frame payload",
                allowCleanEndOfStream: false,
                cancellationToken).ConfigureAwait(false);

            try
            {
                _ = StrictUtf8.GetCharCount(payload.Span);
            }
            catch (DecoderFallbackException exception)
            {
                throw new DaemonProtocolException(
                    DaemonProtocolError.InvalidUtf8,
                    "Daemon frame payload is not valid UTF-8.",
                    exception);
            }

            TMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<TMessage>(payload.Span, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new DaemonProtocolException(
                    DaemonProtocolError.InvalidJson,
                    "Daemon frame payload is not a valid protocol JSON message.",
                    exception);
            }

            if (message is null)
            {
                throw new DaemonProtocolException(
                    DaemonProtocolError.InvalidMessage,
                    "Daemon frame payload produced a null protocol message.");
            }

            ValidateMessage(message);
            return message;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedPayload, clearArray: true);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        string part,
        bool allowCleanEndOfStream,
        CancellationToken cancellationToken)
    {
        var readTotal = 0;
        while (readTotal < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[readTotal..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowCleanEndOfStream && readTotal == 0)
                {
                    throw new DaemonProtocolException(
                        DaemonProtocolError.EndOfStream,
                        "The daemon connection ended between frames.");
                }

                throw new DaemonProtocolException(
                    DaemonProtocolError.UnexpectedEndOfStream,
                    $"The daemon connection ended while reading the {part}.");
            }

            readTotal += read;
        }
    }

    private static void ValidateMessage(DaemonMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.RequestId == Guid.Empty)
        {
            throw new DaemonProtocolException(
                DaemonProtocolError.InvalidMessage,
                "Daemon messages require a non-empty request ID.");
        }

        switch (message)
        {
            case DaemonCommandRequest request when string.IsNullOrWhiteSpace(request.CommandName) || request.Options is null:
                throw new DaemonProtocolException(
                    DaemonProtocolError.InvalidMessage,
                    "Daemon command requests require a command name and options.");
            case DaemonCommandRequest request when request.DeadlineUtc.Offset != TimeSpan.Zero:
                throw new DaemonProtocolException(
                    DaemonProtocolError.InvalidMessage,
                    "Daemon command request deadlines must use UTC.");
            case DaemonCommandResponse response when response.Stdout is null || response.Stderr is null:
                throw new DaemonProtocolException(
                    DaemonProtocolError.InvalidMessage,
                    "Daemon command responses require both buffered output streams.");
        }
    }
}

/// <summary>
/// Classifies daemon wire failures so later routing can distinguish infrastructure fallback from command errors.
/// </summary>
internal enum DaemonProtocolError
{
    InvalidFrameLength,
    FrameTooLarge,
    EndOfStream,
    UnexpectedEndOfStream,
    InvalidUtf8,
    InvalidJson,
    InvalidMessage,
    UnsupportedVersion,
}

/// <summary>
/// Reports invalid, incomplete, or incompatible daemon wire data.
/// </summary>
internal sealed class DaemonProtocolException : IOException
{
    public DaemonProtocolException(DaemonProtocolError error, string message)
        : base(message)
    {
        Error = error;
    }

    public DaemonProtocolException(DaemonProtocolError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public DaemonProtocolError Error { get; }
}
