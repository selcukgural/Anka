using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Anka;

/// <summary>
/// Responsible for writing HTTP/1.1 responses directly to a network socket using ArrayPool buffers
/// to optimize memory usage. This writer avoids string allocations by formatting response headers
/// with Utf8Formatter and supports embedding small response bodies in the header buffer for
/// efficient transmission.
///
/// The writer holds a connection-scoped header buffer reused across keep-alive requests,
/// eliminating per-response <see cref="ArrayPool{T}"/> rent/return overhead. Call
/// <see cref="Dispose"/> when the connection closes to return the buffer.
/// </summary>
public sealed class HttpResponseWriter : IDisposable
{
    /// <summary>
    /// Default size of the connection-scoped header buffer (4 KB + 512 B for headers).
    /// Covers the single-send path for bodies ≤ 4 KB. Larger bodies fall back to a
    /// temporary rental.
    /// </summary>
    private const int DefaultBufSize = 4096 + 512;

    private readonly Socket _socket;
    private readonly IReadOnlyList<HttpHeader> _defaultHeaders;

    /// <summary>
    /// Connection-scoped buffer reused across keep-alive responses. Rented once in the
    /// constructor and returned in <see cref="Dispose"/>.
    /// </summary>
    private byte[] _buf;

    /// <summary>
    /// Responsible for writing HTTP responses to a specified socket.
    /// </summary>
    /// <remarks>
    /// This class manages buffer allocation and provides methods to
    /// asynchronously write HTTP response data to a socket.
    /// </remarks>
    internal HttpResponseWriter(Socket socket, IReadOnlyList<HttpHeader>? defaultHeaders = null)
    {
        _socket         = socket;
        _defaultHeaders = defaultHeaders ?? [];
        _buf            = ArrayPool<byte>.Shared.Rent(DefaultBufSize);
    }

    /// <summary>
    /// Releases resources used by the internal connection-scoped buffer
    /// and returns it to the shared pool for reuse.
    /// </summary>
    /// <remarks>
    /// This method ensures that the buffer rented during the lifetime
    /// of the <see cref="HttpResponseWriter"/> instance is returned to
    /// the <see cref="ArrayPool{T}"/>. It should be called when the connection
    /// is no longer active to prevent memory leaks.
    /// </remarks>
    public void Dispose()
    {
        var buf = Interlocked.Exchange(ref _buf, null!);

        ArrayPool<byte>.Shared.Return(buf);
    }

    /// <summary>
    /// Writes an HTTP response to the client asynchronously.
    /// </summary>
    /// <param name="statusCode">
    /// The HTTP status code to include in the response (e.g., 200 for OK, 404 for Not Found).
    /// </param>
    /// <param name="body">
    /// The response body to send, represented as a <see cref="ReadOnlyMemory{T}"/> of bytes.
    /// Defaults to an empty body if not provided.
    /// </param>
    /// <param name="contentType">
    /// The Content-Type header value for the response. If null, no Content-Type header is written.
    /// </param>
    /// <param name="keepAlive">
    /// Indicates whether the connection should remain open after the response.
    /// If true, the "Connection: keep-alive" header is included.
    /// If false, "Connection: close" is used. The default value is true.
    /// </param>
    /// <param name="cancellationToken">
    /// A token to monitor for cancellation requests. If the token is canceled,
    /// the operation is aborted. The default value is <see cref="CancellationToken.None"/>.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> that represents the asynchronous write operation.
    /// If the operation completes successfully, the task is marked as completed. If an error occurs,
    /// an exception is thrown.
    /// </returns>
    public ValueTask WriteAsync(int statusCode, ReadOnlyMemory<byte> body = default, ReadOnlyMemory<byte> contentType = default,
                                bool keepAlive = true, CancellationToken cancellationToken = default) =>
        WriteAsync(statusCode, body, contentType, keepAlive, ReadOnlySpan<HttpHeader>.Empty, cancellationToken);

    /// <summary>
    /// Writes an HTTP response with optional extra headers to the client asynchronously.
    /// </summary>
    /// <param name="statusCode">The HTTP status code (e.g., 200, 301, 404).</param>
    /// <param name="body">The response body bytes. Defaults to empty.</param>
    /// <param name="contentType">The Content-Type header value bytes. Omitted if empty.</param>
    /// <param name="keepAlive">Whether to emit <c>Connection: keep-alive</c> or <c>Connection: close</c>.</param>
    /// <param name="extraHeaders">
    /// Additional response headers written after the fixed headers.
    /// For zero-allocation hot paths, pass a <c>static readonly HttpHeader[]</c> allocated at startup.
    /// Use <c>stackalloc HttpHeader[n]</c> for small per-request sets.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask WriteAsync(int statusCode, ReadOnlyMemory<byte> body, ReadOnlyMemory<byte> contentType, bool keepAlive,
                                ReadOnlySpan<HttpHeader> extraHeaders, CancellationToken cancellationToken = default)
    {
        const int headerEstimate = 512;
        var smallBodyThreshold = body.Length <= 4096 ? body.Length : 0;

        // Account for default + extra headers: "name: value\r\n" per entry
        var extraSize = 0;

        foreach (var h in _defaultHeaders)
        {
            extraSize += h.Name.Length + 2 + h.Value.Length + 2;
        }

        foreach (var h in extraHeaders)
        {
            extraSize += h.Name.Length + 2 + h.Value.Length + 2;
        }

        var needed = headerEstimate + extraSize + smallBodyThreshold;

        byte[]? tempBuf = null;
        var buf = _buf;

        if (needed > buf.Length)
        {
            tempBuf = ArrayPool<byte>.Shared.Rent(needed);
            buf = tempBuf;
        }

        var pos = BuildHeaderBlock(buf, statusCode, body, keepAlive, contentType, _defaultHeaders, extraHeaders, smallBodyThreshold);

        // Fast synchronous path: try sending without async state machine.
        if (smallBodyThreshold > 0 || body.IsEmpty)
        {
            // Single buffer send it (headers + inline body, or headers only).
            return SendSingleBuffer(buf, pos, tempBuf, cancellationToken);
        }

        // Large body: headers + separate body send.
        return SendHeadersThenBody(buf, pos, body, tempBuf, cancellationToken);
    }

    /// <summary>
    /// Sends a single buffer containing the HTTP response headers and optional embedded body data
    /// directly through the underlying socket. Optimized for scenarios where the entire payload
    /// (headers + small body, or headers only) fits into a single buffer.
    /// </summary>
    /// <param name="buf">The buffer containing the data to send, including headers and optional embedded body.</param>
    /// <param name="pos">The length of data in the buffer that needs to be sent.</param>
    /// <param name="tempBuf">
    /// A temporary buffer allocated for additional capacity if the connection-scoped buffer was not enough.
    /// This will be returned to the pool after the send operation completes.
    /// </param>
    /// <param name="ct">A token to monitor for cancellation requests during the send operation.</param>
    /// <returns>
    /// A <see cref="ValueTask"/> representing the asynchronous send operation. Completes immediately if the
    /// send operation finishes synchronously. Otherwise, awaits the completion of the entire sent process.
    /// </returns>
    private ValueTask SendSingleBuffer(byte[] buf, int pos, byte[]? tempBuf, CancellationToken ct)
    {
        var payload = buf.AsMemory(0, pos);
        var sendTask = _socket.SendAsync(payload, SocketFlags.None, ct);

        // Start the sending first and only await if needed. On loopback or when the kernel send
        // buffer has room, SendAsync often completes synchronously; returning a completed
        // ValueTask here avoids building an async state machine for the common fast path.
        if (!sendTask.IsCompletedSuccessfully)
        {
            return AwaitSendAllAndCleanup(sendTask, payload, tempBuf, ct);
        }

        var sent = sendTask.Result;

        if (sent != payload.Length)
        {
            return AwaitSendAllAndCleanup(sendTask, payload, tempBuf, ct);
        }

        if (tempBuf is not null)
        {
            ArrayPool<byte>.Shared.Return(tempBuf);
        }

        return default;
    }

    /// <summary>
    /// Awaits the completion of a socket send operation and ensures that the entire payload
    /// is transmitted. Cleans up any temporary buffer used during the operation.
    /// </summary>
    /// <param name="sendTask">The initial task for the socket send operation.</param>
    /// <param name="payload">The remaining payload data to be sent if the initial operation is incomplete.</param>
    /// <param name="tempBuf">A temporary buffer used during the operation, if any. This will be returned to the buffer pool upon completion.</param>
    /// <param name="ct">A cancellation token to observe cancellation requests.</param>
    /// <returns>A ValueTask that represents the asynchronous operation.</returns>
    private async ValueTask AwaitSendAllAndCleanup(ValueTask<int> sendTask, ReadOnlyMemory<byte> payload, byte[]? tempBuf, CancellationToken ct)
    {
        try
        {
            var sent = await sendTask;
            await SendRemainingAsync(payload, sent, ct);
        }
        finally
        {
            if (tempBuf is not null)
            {
                ArrayPool<byte>.Shared.Return(tempBuf);
            }
        }
    }

    /// <summary>
    /// Sends the HTTP response headers followed by the response body to the connected network socket.
    /// </summary>
    /// <param name="buf">
    /// The buffer containing the formatted HTTP response headers.
    /// </param>
    /// <param name="pos">
    /// The position in the buffer indicating the end of the header block to be sent.
    /// </param>
    /// <param name="body">
    /// The memory region containing the response body, if any, to be sent after the headers.
    /// </param>
    /// <param name="tempBuf">
    /// A temporarily allocated buffer used for additional response data, if needed. Can be null.
    /// </param>
    /// <param name="ct">
    /// A <see cref="CancellationToken"/> to observe for cancellation requests during the send operation.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> representing the asynchronous operation of sending the headers and body.
    /// </returns>
    private ValueTask SendHeadersThenBody(byte[] buf, int pos, ReadOnlyMemory<byte> body, byte[]? tempBuf, CancellationToken ct)
    {
        var headerBlock = buf.AsMemory(0, pos);
        var headerSend = _socket.SendAsync(headerBlock, SocketFlags.None, ct);

        // Same idea here: keep the synchronous-success path allocation-free and only fall
        // back to async continuations when the socket cannot flush the whole payload inline.
        if (!headerSend.IsCompletedSuccessfully)
        {
            return AwaitHeadersThenBody(headerSend, headerBlock, body, tempBuf, ct);
        }

        var sent = headerSend.Result;

        if (sent != headerBlock.Length)
        {
            return AwaitHeadersThenBody(headerSend, headerBlock, body, tempBuf, ct);
        }

        if (tempBuf is not null)
        {
            ArrayPool<byte>.Shared.Return(tempBuf);
        }

        return SendBody(body, ct);
    }

    /// <summary>
    /// Sends the specified body content to the socket asynchronously.
    /// </summary>
    /// <param name="body">The body content to be sent, represented as a read-only memory block of bytes.</param>
    /// <param name="ct">The <see cref="CancellationToken"/> used to observe cancellation requests.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation. The task completes
    /// when the entire body content has been successfully sent or an error occurs during transmission.</returns>
    private ValueTask SendBody(ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var bodySend = _socket.SendAsync(body, SocketFlags.None, ct);

        if (!bodySend.IsCompletedSuccessfully)
        {
            return AwaitSendAll(bodySend, body, ct);
        }

        var sent = bodySend.Result;
        return sent == body.Length ? default : AwaitSendAll(bodySend, body, ct);
    }

    /// <summary>
    /// Awaits the completion of header data transmission and subsequently sends the body data
    /// as part of an HTTP response.
    /// </summary>
    /// <param name="headerSend">A task representing the asynchronous transmission of header data.</param>
    /// <param name="headerBlock">The memory block containing the serialized HTTP headers.</param>
    /// <param name="body">The memory block containing the HTTP response body to be sent after headers.</param>
    /// <param name="tempBuf">A temporary buffer used for intermediate operations, if applicable.</param>
    /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A ValueTask that represents the asynchronous operation of sending headers and body.</returns>
    private async ValueTask AwaitHeadersThenBody(ValueTask<int> headerSend, ReadOnlyMemory<byte> headerBlock, ReadOnlyMemory<byte> body,
                                                 byte[]? tempBuf, CancellationToken ct)
    {
        try
        {
            var sent = await headerSend;
            await SendRemainingAsync(headerBlock, sent, ct);
        }
        finally
        {
            if (tempBuf is not null)
            {
                ArrayPool<byte>.Shared.Return(tempBuf);
            }
        }

        await SendBody(body, ct);
    }

    /// <summary>
    /// Ensures that the entire payload is sent to the socket asynchronously, even if the
    /// initial send operation does not complete the transmission.
    /// </summary>
    /// <param name="sendTask">A task representing the initial sending operation.</param>
    /// <param name="payload">The data to be transmitted to the socket.</param>
    /// <param name="ct">A cancellation token to monitor for cancellation requests.</param>
    /// <returns>A ValueTask that completes when the entire payload has been sent.</returns>
    private ValueTask AwaitSendAll(ValueTask<int> sendTask, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        AwaitSendAllCore(sendTask, payload, ct);

    /// <summary>
    /// Ensures that the entirety of the provided payload is sent across the socket,
    /// asynchronously awaiting and handling partial sends as necessary.
    /// </summary>
    /// <param name="sendTask">The initial task representing the first send operation.</param>
    /// <param name="payload">The data to send, represented as a read-only memory buffer.</param>
    /// <param name="ct">The cancellation token to observe for canceling the operation.</param>
    /// <returns>A task that represents the asynchronous operation of sending the complete payload.</returns>
    private async ValueTask AwaitSendAllCore(ValueTask<int> sendTask, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var sent = await sendTask;
        await SendRemainingAsync(payload, sent, ct);
    }

    /// <summary>
    /// Sends the remaining portion of a payload over the socket until fully transmitted.
    /// </summary>
    /// <param name="payload">The payload to send, represented as a read-only memory block of bytes.</param>
    /// <param name="bytesSent">The number of bytes already sent from the payload.</param>
    /// <param name="ct">A cancellation token to observe for operation cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation of sending the remaining payload.</returns>
    private async ValueTask SendRemainingAsync(ReadOnlyMemory<byte> payload, int bytesSent, CancellationToken ct)
    {
        while (bytesSent < payload.Length)
        {
            var sent = await _socket.SendAsync(payload[bytesSent..], SocketFlags.None, ct);

            if (sent <= 0)
            {
                throw new SocketException((int)SocketError.ConnectionReset);
            }

            bytesSent += sent;
        }
    }

    // Pre-cached header prefix for the most common case: 200 OK + keep-alive.
    // "HTTP/1.1 200 OK\r\nServer: Anka\r\n" — 30 bytes, never changes.
    private static ReadOnlySpan<byte> Ok200Prefix => "HTTP/1.1 200 OK\r\nServer: Anka\r\n"u8;
    private static ReadOnlySpan<byte> KeepAliveHeader => "Connection: keep-alive\r\n"u8;
    private static ReadOnlySpan<byte> CloseHeader => "Connection: close\r\n"u8;
    private static ReadOnlySpan<byte> ContentLengthName => "Content-Length: "u8;
    private static ReadOnlySpan<byte> ContentTypeName => "Content-Type: "u8;

    // Date header: cached and refreshed at most once per second.
    // TFB General Requirement #5 mandates a Date header on every response.
    private static volatile byte[] _cachedDateLine = BuildDateLine();
    private static long _cachedDateTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// Constructs a byte array representing the HTTP Date header with the current UTC timestamp.
    /// </summary>
    /// <remarks>
    /// The method generates a properly formatted Date header line, including the "Date: " prefix
    /// followed by the timestamp in RFC1123 format, ending with a carriage return and line feed.
    /// This is used to ensure each HTTP response includes a valid Date header as mandated by
    /// HTTP specifications.
    /// </remarks>
    /// <returns>
    /// A byte array containing the Date header with the current timestamp in RFC1123 format.
    /// </returns>
    private static byte[] BuildDateLine()
    {
        var dateStr = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture);
        return Encoding.ASCII.GetBytes($"Date: {dateStr}\r\n");
    }

    /// <summary>
    /// Retrieves the current HTTP date line in a byte array format.
    /// </summary>
    /// <remarks>
    /// The method ensures that the date line is refreshed no more than once per second.
    /// It compares the current UTC timestamp with the cached timestamp to determine
    /// whether to use the precomputed date line or generate a new one.
    /// </remarks>
    /// <returns>
    /// A read-only span of bytes representing the current HTTP date line.
    /// </returns>
    private static ReadOnlySpan<byte> GetCurrentDateLine()
    {
        var nowTicks = DateTime.UtcNow.Ticks;

        if (nowTicks - Interlocked.Read(ref _cachedDateTicks) < TimeSpan.TicksPerSecond)
        {
            return _cachedDateLine;
        }

        Interlocked.Exchange(ref _cachedDateTicks, nowTicks);
        _cachedDateLine = BuildDateLine();
        return _cachedDateLine;
    }

    /// <summary>
    /// Writes the header block (and optionally an inline small body) into <paramref name="buf"/>
    /// and returns the number of bytes written.
    /// </summary>
    private static int BuildHeaderBlock(byte[] buf, int statusCode, ReadOnlyMemory<byte> body, bool keepAlive, ReadOnlyMemory<byte> contentType,
                                        IReadOnlyList<HttpHeader> defaultHeaders, ReadOnlySpan<HttpHeader> extraHeaders, int smallBodyThreshold)
    {
        var span = buf.AsSpan();
        var pos = 0;

        // Fast path: 200 OK is overwhelmingly common — single copy for status + server.
        if (statusCode == 200)
        {
            Ok200Prefix.CopyTo(span);
            pos = Ok200Prefix.Length;
        }
        else
        {
            WriteStatusLine(statusCode, span, ref pos);
            WriteLiteral("Server: Anka\r\n"u8, span, ref pos);
        }

        // Content-Length
        WriteLiteral(ContentLengthName, span, ref pos);
        Utf8Formatter.TryFormat(body.Length, span[pos..], out var written);
        pos += written;
        span[pos++] = (byte)'\r';
        span[pos++] = (byte)'\n';

        // Date (TFB General Requirement #5) — refreshed at most once per second
        var dateLine = GetCurrentDateLine();
        dateLine.CopyTo(span[pos..]);
        pos += dateLine.Length;

        // Connection
        var connHeader = keepAlive ? KeepAliveHeader : CloseHeader;
        connHeader.CopyTo(span[pos..]);
        pos += connHeader.Length;

        if (!contentType.IsEmpty)
        {
            WriteLiteral(ContentTypeName, span, ref pos);
            contentType.Span.CopyTo(span[pos..]);
            pos += contentType.Length;
            span[pos++] = (byte)'\r';
            span[pos++] = (byte)'\n';
        }

        // Server-wide default headers (security headers, CORS, branding, etc.)
        foreach (var h in defaultHeaders)
        {
            WriteHeader(h, span, ref pos);
        }

        // Per-request extra headers (Location, Set-Cookie, Cache-Control, etc.)
        foreach (var h in extraHeaders)
        {
            WriteHeader(h, span, ref pos);
        }

        // End of headers
        span[pos++] = (byte)'\r';
        span[pos++] = (byte)'\n';

        if (smallBodyThreshold <= 0)
        {
            return pos;
        }

        // Copy small bodies into the header buffer for a single sending.
        body.Span.CopyTo(span[pos..]);
        pos += body.Length;

        return pos;
    }

    /// <summary>
    /// Writes an HTTP header into the provided span buffer at the specified position.
    /// </summary>
    /// <param name="h">The <see cref="HttpHeader"/> containing the header name and value to be written.</param>
    /// <param name="span">The target <see cref="Span{T}"/> of bytes where the header will be written.</param>
    /// <param name="pos">A reference to the current position in the span buffer, which will be updated after writing.</param>
    private static void WriteHeader(in HttpHeader h, Span<byte> span, ref int pos)
    {
        h.Name.Span.CopyTo(span[pos..]);
        pos += h.Name.Length;
        span[pos++] = (byte)':';
        span[pos++] = (byte)' ';
        h.Value.Span.CopyTo(span[pos..]);
        pos += h.Value.Length;
        span[pos++] = (byte)'\r';
        span[pos++] = (byte)'\n';
    }

    /// <summary>
    /// Writes the HTTP status line to the provided buffer with the specified status code.
    /// The status line includes the HTTP version, status code, and reason phrase.
    /// </summary>
    /// <param name="statusCode">
    /// The HTTP status code to be included in the status line (e.g., 200 for OK, 404 for Not Found).
    /// </param>
    /// <param name="buf">
    /// The buffer where the status line is written. It should have enough capacity
    /// to accommodate the formatted status line.
    /// </param>
    /// <param name="pos">
    /// A reference to the current writing position in the buffer. This value will be updated
    /// to reflect the new position after the write operation.
    /// </param>
    private static void WriteStatusLine(int statusCode, Span<byte> buf, ref int pos)
    {
        WriteLiteral("HTTP/1.1 "u8, buf, ref pos);
        Utf8Formatter.TryFormat(statusCode, buf[pos..], out var written);
        pos += written;
        buf[pos++] = (byte)' ';
        var reason = GetReasonPhrase(statusCode);
        reason.CopyTo(buf[pos..]);
        pos += reason.Length;
        buf[pos++] = (byte)'\r';
        buf[pos++] = (byte)'\n';
    }

    /// <summary>
    /// Copies the specified literal byte sequence into the provided buffer and advances the position.
    /// </summary>
    /// <param name="literal">The byte sequence to copy into the buffer.</param>
    /// <param name="buf">The buffer where the literal will be written.</param>
    /// <param name="pos">The current position in the buffer, which will be advanced by the length of the literal.</param>
    private static void WriteLiteral(ReadOnlySpan<byte> literal, Span<byte> buf, ref int pos)
    {
        literal.CopyTo(buf[pos..]);
        pos += literal.Length;
    }

    /// <summary>
    /// Retrieves the reason phrase associated with a given HTTP status code.
    /// </summary>
    /// <param name="code">The HTTP status code for which the reason phrase is requested.</param>
    /// <returns>A read-only span of bytes representing the reason phrase corresponding to the provided status code.</returns>
    private static ReadOnlySpan<byte> GetReasonPhrase(int code) => code switch
    {
        200 => "OK"u8,
        201 => "Created"u8,
        204 => "No Content"u8,
        301 => "Moved Permanently"u8,
        302 => "Found"u8,
        304 => "Not Modified"u8,
        400 => "Bad Request"u8,
        401 => "Unauthorized"u8,
        403 => "Forbidden"u8,
        404 => "Not Found"u8,
        405 => "Method Not Allowed"u8,
        500 => "Internal Server Error"u8,
        501 => "Not Implemented"u8,
        503 => "Service Unavailable"u8,
        _   => "Unknown"u8,
    };
}