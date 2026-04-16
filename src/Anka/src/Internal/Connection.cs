using System.Buffers;
using System.Net.Sockets;
using Anka.Extensions;

namespace Anka;

/// <summary>
/// Handles the full lifecycle of a single TCP connection: reading from the socket,
/// parsing HTTP/1.x requests, dispatching to the handler, and sending responses.
///
/// Uses a single-task, direct-socket approach backed by <see cref="SocketReceiver"/>.
/// On loopback and LAN, <c>ReceiveAsync</c> completes synchronously (data already in
/// kernel buffer) — no thread-pool post, no allocation.  On real networks where data
/// arrives asynchronously, the OS kqueue/epoll thread posts the resumption to the
/// thread pool and immediately returns to the I/O loop, keeping I/O and processing
/// work on separate threads so both can scale independently.
/// </summary>
internal sealed class Connection
{
    // Size of the per-connection receive buffer (64 KB)
    private const int BufferSize = 64 * 1024;

    private readonly Socket _socket;
    private readonly RequestHandler _handler;
    private readonly ServerOptions _serverOptions;
    private readonly CancellationToken _cancellationToken;

    /// <summary>
    /// Represents a network connection and encapsulates the logic for processing incoming requests.
    /// </summary>
    /// <remarks>
    /// This class is responsible for managing a network connection using a <see cref="Socket"/>,
    /// handling incoming HTTP requests with a specified <see cref="RequestHandler"/>,
    /// and utilizing <see cref="ServerOptions"/> for server configuration.
    /// It operates asynchronously and supports cancellation through a <see cref="CancellationToken"/>.
    /// </remarks>
    private Connection(Socket socket, RequestHandler handler, ServerOptions serverOptions, CancellationToken cancellationToken)
    {
        _socket         = socket;
        _handler        = handler;
        _serverOptions  = serverOptions;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Creates a <see cref="Connection"/> for the specified <paramref name="socket"/> and starts processing it asynchronously.
    /// </summary>
    /// <param name="socket">The network socket associated with the connection.</param>
    /// <param name="handler">The delegate responsible for handling incoming HTTP requests.</param>
    /// <param name="serverOptions">The configuration options for the server.</param>
    /// <param name="cancellationToken">A token to cancel the processing of the connection.</param>
    /// <returns>A <see cref="Task"/> that completes when the connection is closed.</returns>
    public static Task RunAsync(Socket socket, RequestHandler handler, ServerOptions serverOptions, CancellationToken cancellationToken)
    {
        socket.NoDelay = true;
        return new Connection(socket, handler, serverOptions, cancellationToken).ProcessAsync();
    }

    /// <summary>
    /// Processes the established connection asynchronously, handling data transfer,
    /// request parsing, and response generation. Manages resource cleanup
    /// and connection lifecycle, ensuring proper socket handling and disposal.
    /// </summary>
    /// <returns>A <see cref="Task"/> that completes when the connection processing finishes or the connection is closed.</returns>
    private async Task ProcessAsync()
    {
        var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        var request = HttpRequestPool.Rent();
        using var writer = new HttpResponseWriter(_socket, _serverOptions.DefaultResponseHeaders);
        using var receiver = new SocketReceiver();
        using var readTimeoutCts = _serverOptions.ReadTimeout is not null ? new CancellationTokenSource() : null;

        // Closing the socket aborts any pending SocketAsyncEventArgs operation,
        // which causes ReceiveAsync to throw SocketException — caught below.
        await using var reg = _cancellationToken.Register(static s => ((Socket)s!).Close(), _socket);
        var readTimeoutReg = readTimeoutCts?.Token.Register(static s => ((Socket)s!).Close(), _socket);
        
        try
        {
            var start = 0; // first unread byte
            var end = 0;   // one past the last filled byte

            while (true)
            {
                var receiveState = await ReceiveMoreIntoBufferAsync(receiver, buf, start, end, readTimeoutCts);
                start = receiveState.Start;
                end = receiveState.End;
                if (!receiveState.Success)
                {
                    break; // client closed the connection
                }

                // Parse and handle as many complete requests as the buffer holds.
                var parseOffset = start;

                while (parseOffset < end)
                {
                    // Reset for reuse — keeps buffers, clears fields.
                    request.ResetForReuse();
                    writer.SetSuppressResponseBody(false);

                    var parseResult = TryParseHeadersNext(
                        buf,
                        parseOffset,
                        end - parseOffset,
                        request,
                        _serverOptions.MaxRequestTargetSize,
                        _serverOptions.MaxRequestHeadersSize,
                        out var bytesConsumed);

                    if (parseResult == HttpParseResult.Incomplete)
                    {
                        break; // incomplete request — wait for more bytes
                    }

                    switch (parseResult)
                    {
                        case HttpParseResult.Invalid or HttpParseResult.ConflictingContentLength or HttpParseResult.MissingHostHeader:
                            await writer.WriteAsync(400, keepAlive: false, cancellationToken: _cancellationToken);
                            return;
                        case HttpParseResult.RequestTargetTooLong:
                            await writer.WriteAsync(414, keepAlive: false, cancellationToken: _cancellationToken);
                            return;
                        case HttpParseResult.HeaderFieldsTooLarge:
                            await writer.WriteAsync(431, keepAlive: false, cancellationToken: _cancellationToken);
                            return;
                        case HttpParseResult.HttpVersionNotSupported:
                            await writer.WriteAsync(505, keepAlive: false, cancellationToken: _cancellationToken);
                            return;
                    }

                    parseOffset += bytesConsumed;

                    var keepAlive = request.IsKeepAlive;
                    writer.SetSuppressResponseBody(request.Method == HttpMethod.Head);
                    
                    if (!request.ValidateContentLengthFor411())
                    {
                        await writer.WriteAsync(411, keepAlive: false, cancellationToken: _cancellationToken);
                        return;
                    }
                    
                    if (!request.IsRequestBodySizeWithinLimit(_serverOptions.MaxRequestBodySize))
                    {
                        await writer.WriteAsync(413, keepAlive: false, cancellationToken: _cancellationToken);
                        return;
                    }

                    if (ShouldSend100Continue(request))
                    {
                        await writer.WriteContinueAsync(_cancellationToken);
                    }

                    var bodyReadState = await ReadRequestBodyAsync(receiver, buf, parseOffset, end, request, readTimeoutCts);
                    parseOffset = bodyReadState.ParseOffset;
                    end = bodyReadState.End;
                    switch (bodyReadState.Result)
                    {
                        case RequestBodyReadResult.ClientClosed:
                            return;
                        case RequestBodyReadResult.Invalid:
                            await writer.WriteAsync(400, keepAlive: false, cancellationToken: _cancellationToken);
                            return;
                        case RequestBodyReadResult.TooLarge:
                            await writer.WriteAsync(413, keepAlive: false, cancellationToken: _cancellationToken);
                            return;
                    }
                    
                    try
                    {
                        await _handler(request, writer, _cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (SocketException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await Console.Error.WriteLineAsync($"[Anka] Unhandled handler exception: {ex.GetType().Name}: {ex.Message}");

                        try
                        {
                            await writer.WriteAsync(500, cancellationToken: _cancellationToken);
                        }
                        catch
                        {
                            /* best-effort */
                        }

                        return;
                    }

                    if (!keepAlive)
                    {
                        return;
                    }
                }

                if (parseOffset == end)
                {
                    start = 0;
                    end = 0;
                    continue;
                }

                start = parseOffset;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        finally
        {
            if (readTimeoutReg.HasValue)
            {
                await readTimeoutReg.Value.DisposeAsync();
            }
            
            HttpRequestPool.Return(request);
            ArrayPool<byte>.Shared.Return(buf);
            _socket.Close();
            _socket.Dispose();
        }
    }

    /// <summary>
    /// Attempts to parse the next HTTP request from the provided buffer.
    /// Returns the HTTP parsing result and the number of bytes consumed on success.
    /// </summary>
    /// <param name="buf">The byte buffer containing the raw HTTP data to parse.</param>
    /// <param name="offset">The starting position within the buffer to begin parsing.</param>
    /// <param name="length">The number of bytes available for parsing starting from the offset.</param>
    /// <param name="request">An instance of <see cref="HttpRequest"/> where the parsed HTTP request data will be populated.</param>
    /// <param name="maxRequestTargetSize">The maximum allowed size of the request target. If null, no limit is applied.</param>
    /// <param name="maxRequestHeadersSize">The maximum allowed aggregate size, in bytes, for request headers.</param>
    /// <param name="consumed">The number of bytes consumed during parsing, set to 0 on failure.</param>
    /// <returns>
    /// A <see cref="HttpParseResult"/> value indicating the result of the parsing operation.
    /// Possible values are <see cref="HttpParseResult.Success"/>, <see cref="HttpParseResult.Incomplete"/>,
    /// <see cref="HttpParseResult.Invalid"/>, or <see cref="HttpParseResult.RequestTargetTooLong"/>.
    /// </returns>
    private static HttpParseResult TryParseHeadersNext(
        byte[] buf,
        int offset,
        int length,
        HttpRequest request,
        int? maxRequestTargetSize,
        int maxRequestHeadersSize,
        out int consumed)
    {
        var seq = new ReadOnlySequence<byte>(buf, offset, length);
        var reader = new SequenceReader<byte>(seq);
        var result = HttpParser.TryParseHeaders(ref reader, request, maxRequestTargetSize, maxRequestHeadersSize);

        if (result == HttpParseResult.Success)
        {
            consumed = (int)reader.Consumed;
            return HttpParseResult.Success;
        }

        consumed = 0;
        return result;
    }

    /// <summary>
    /// Represents the result of attempting to read the body of an HTTP request
    /// within the connection handling process. This enumeration identifies
    /// various outcomes of the read operation, including success and specific
    /// failure conditions.
    /// </summary>
    private enum RequestBodyReadResult : byte
    {
        Success = 0,
        ClientClosed = 1,
        Invalid = 2,
        TooLarge = 3
    }

    /// <summary>
    /// Represents the result of reading the body of an HTTP request, including metadata
    /// such as the parsing offset and the end position of the data within the buffer.
    /// </summary>
    /// <remarks>
    /// This structure is used to encapsulate the state of request body processing in the
    /// connection lifecycle. It helps track the parsing position and validate the body
    /// read operation's success or failure. The state includes information about the
    /// parsing offset, the buffer end position, and the outcome of the read operation,
    /// which is captured using <see cref="RequestBodyReadResult"/>.
    /// </remarks>
    private readonly struct BodyReadState(RequestBodyReadResult result, int parseOffset, int end)
    {
        /// <summary>
        /// Gets the outcome of reading the request body, indicating the result of the operation.
        /// </summary>
        /// <remarks>
        /// The possible outcomes include successful reading of the body, closure by the client,
        /// invalid data, or a body that exceeds the allowable size.
        /// </remarks>
        public RequestBodyReadResult Result { get; } = result;

        /// <summary>
        /// Gets the offset value indicating the position in the buffer where parsing should begin.
        /// </summary>
        /// <remarks>
        /// This property is used to track the starting point for parsing data within a buffer.
        /// The value is typically updated as the data is read and processed.
        /// </remarks>
        public int ParseOffset { get; } = parseOffset;

        /// <summary>
        /// Gets the end position of the body read operation in the given context.
        /// </summary>
        /// <remarks>
        /// The <c>End</c> property specifies the position indicating where the body read operation ends.
        /// It is used to track the completion of the parsing process and to manage the data boundaries.
        /// </remarks>
        public int End { get; } = end;
    }

    /// <summary>
    /// Represents the state of a buffer read operation, encapsulating the success status
    /// and positional markers within the buffer.
    /// This struct is used internally to track the outcome of data reading attempts during
    /// processing, helping manage buffer positions and signaling success or termination
    /// conditions to the caller.
    /// </summary>
    private readonly struct BufferReadState(bool success, int start, int end)
    {
        /// <summary>
        /// Gets a value indicating whether the data was successfully read into the buffer.
        /// </summary>
        /// <remarks>
        /// This property is used to determine if the buffer read operation was successful.
        /// A value of <c>true</c> indicates that the operation completed successfully,
        /// while <c>false</c> indicates that the client closed the connection or the operation failed.
        /// </remarks>
        public bool Success { get; } = success;

        /// <summary>
        /// Represents the starting position of the relevant segment in the buffer.
        /// </summary>
        /// <remarks>
        /// The <c>Start</c> property indicates the index in the buffer where parsing or processing begins.
        /// This is particularly useful in operations involving partial reads or buffering,
        /// allowing subsequent operations to determine the starting point of the unprocessed data.
        /// </remarks>
        public int Start { get; } = start;

        /// <summary>
        /// Gets the ending position within the buffer for the current read operation.
        /// </summary>
        /// <remarks>
        /// This property is used to indicate the last valid byte index in the buffer
        /// after data has been read or parsed. It helps define the boundary for
        /// operations such as data processing and advancing the buffer.
        /// </remarks>
        public int End { get; } = end;
    }

    /// <summary>
    /// Reads the request body from the specified <see cref="SocketReceiver"/> into the provided buffer.
    /// Depending on the transfer encoding type of the request, this can process chunked or content-length specified bodies.
    /// Returns a <see cref="ValueTask{TResult}"/> that completes with the current body read state, parse offset, and end position.
    /// </summary>
    /// <param name="receiver">The <see cref="SocketReceiver"/> responsible for reading data from the socket.</param>
    /// <param name="buf">The buffer to store the incoming request body data.</param>
    /// <param name="parseOffset">The current offset in the buffer from which data parsing begins.</param>
    /// <param name="end">The current end position in the buffer containing valid data.</param>
    /// <param name="request">The <see cref="HttpRequest"/> object representing the incoming HTTP request.</param>
    /// <param name="readTimeoutCts">An optional <see cref="CancellationTokenSource"/> to enforce a timeout while reading the request body.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> representing the operation that completes with the state of the read process, the updated parse offset, and the end position in the buffer.</returns>
    private async ValueTask<BodyReadState> ReadRequestBodyAsync(
        SocketReceiver receiver,
        byte[] buf,
        int parseOffset,
        int end,
        HttpRequest request,
        CancellationTokenSource? readTimeoutCts)
    {
        if (request.HasChunkedTransferEncoding)
        {
            return await ReadChunkedBodyAsync(receiver, buf, parseOffset, end, request, readTimeoutCts);
        }

        if (request is not { HasContentLength: true, HasInvalidContentLength: false, ContentLength: > 0 })
        {
            request.Body = default;
            return new BodyReadState(RequestBodyReadResult.Success, parseOffset, end);
        }

        if (request.ContentLength > int.MaxValue)
        {
            return new BodyReadState(RequestBodyReadResult.TooLarge, parseOffset, end);
        }

        EnsureBodyBufferCapacity(request, (int)request.ContentLength, 0);

        var bodyLength = (int)request.ContentLength;
        var copied = 0;
        if (parseOffset < end)
        {
            copied = Math.Min(bodyLength, end - parseOffset);
            buf.AsSpan(parseOffset, copied).CopyTo(request.BodyBuffer!.AsSpan(0, copied));
            parseOffset += copied;
        }

        while (copied < bodyLength)
        {
            var read = await ReceiveAsync(receiver, request.BodyBuffer!.AsMemory(copied, bodyLength - copied), readTimeoutCts);
            if (read == 0)
            {
                return new BodyReadState(RequestBodyReadResult.ClientClosed, parseOffset, end);
            }

            copied += read;
        }

        request.Body = request.BodyBuffer!.AsMemory(0, bodyLength);
        return new BodyReadState(RequestBodyReadResult.Success, parseOffset, end);
    }

    /// <summary>
    /// Reads and processes the body of an HTTP request that is encoded using chunked transfer encoding.
    /// Returns a <see cref="BodyReadState"/> containing the result of the operation, the current parse offset, and the end position in the buffer.
    /// </summary>
    /// <param name="receiver">The <see cref="SocketReceiver"/> used to receive data from the network stream.</param>
    /// <param name="buf">The buffer containing the raw data to parse and process.</param>
    /// <param name="parseOffset">The starting position in the buffer to begin parsing the chunked body.</param>
    /// <param name="end">The end position in the buffer up to which data can be processed.</param>
    /// <param name="request">The <see cref="HttpRequest"/> representing the incoming HTTP request.</param>
    /// <param name="readTimeoutCts">An optional <see cref="CancellationTokenSource"/> for managing read timeout behavior.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> that resolves to a <see cref="BodyReadState"/> containing the results of the read operation.</returns>
    private async ValueTask<BodyReadState> ReadChunkedBodyAsync(SocketReceiver receiver, byte[] buf, int parseOffset, int end, HttpRequest request,
                                                                CancellationTokenSource? readTimeoutCts)
    {
        var bodyLength = 0;

        while (true)
        {
            while (true)
            {
                var sizeResult = ChunkedBodyParser.TryReadChunkSize(buf.AsSpan(parseOffset, end - parseOffset), out var chunkSize, out var sizeBytes);

                switch (sizeResult)
                {
                    case ChunkedBodyParseResult.Incomplete:
                    {
                        var receiveState = await ReceiveMoreIntoBufferAsync(receiver, buf, parseOffset, end, readTimeoutCts);
                        parseOffset = receiveState.Start;
                        end = receiveState.End;

                        if (!receiveState.Success)
                        {
                            return new BodyReadState(RequestBodyReadResult.ClientClosed, parseOffset, end);
                        }

                        continue;
                    }
                    case ChunkedBodyParseResult.Invalid:
                        return new BodyReadState(RequestBodyReadResult.Invalid, parseOffset, end);
                }

                parseOffset += sizeBytes;

                if (chunkSize == 0)
                {
                    while (true)
                    {
                        var trailerResult = ChunkedBodyParser.TryConsumeTrailers(buf.AsSpan(parseOffset, end - parseOffset), out var trailerBytes);
                        switch (trailerResult)
                        {
                            case ChunkedBodyParseResult.Incomplete:
                            {
                                var receiveState = await ReceiveMoreIntoBufferAsync(receiver, buf, parseOffset, end, readTimeoutCts);
                                parseOffset = receiveState.Start;
                                end = receiveState.End;
                                if (!receiveState.Success)
                                {
                                    return new BodyReadState(RequestBodyReadResult.ClientClosed, parseOffset, end);
                                }

                                continue;
                            }
                            case ChunkedBodyParseResult.Invalid:
                                return new BodyReadState(RequestBodyReadResult.Invalid, parseOffset, end);
                        }

                        parseOffset += trailerBytes;
                        request.Body = bodyLength == 0
                            ? default
                            : request.BodyBuffer!.AsMemory(0, bodyLength);
                        return new BodyReadState(RequestBodyReadResult.Success, parseOffset, end);
                    }
                }

                if (_serverOptions.MaxRequestBodySize is { } maxRequestBodySize &&
                    bodyLength + chunkSize > maxRequestBodySize)
                {
                    return new BodyReadState(RequestBodyReadResult.TooLarge, parseOffset, end);
                }

                EnsureBodyBufferCapacity(request, bodyLength + chunkSize, bodyLength);

                var copied = 0;
                if (parseOffset < end)
                {
                    copied = Math.Min(chunkSize, end - parseOffset);
                    buf.AsSpan(parseOffset, copied).CopyTo(request.BodyBuffer!.AsSpan(bodyLength, copied));
                    parseOffset += copied;
                }

                while (copied < chunkSize)
                {
                    var read = await ReceiveAsync(
                        receiver,
                        request.BodyBuffer!.AsMemory(bodyLength + copied, chunkSize - copied),
                        readTimeoutCts);
                    if (read == 0)
                    {
                        return new BodyReadState(RequestBodyReadResult.ClientClosed, parseOffset, end);
                    }

                    copied += read;
                }

                bodyLength += chunkSize;

                while (true)
                {
                    if (end - parseOffset >= 2)
                    {
                        if (buf[parseOffset] != (byte)'\r' || buf[parseOffset + 1] != (byte)'\n')
                        {
                            return new BodyReadState(RequestBodyReadResult.Invalid, parseOffset, end);
                        }

                        parseOffset += 2;
                        break;
                    }

                    var receiveState = await ReceiveMoreIntoBufferAsync(receiver, buf, parseOffset, end, readTimeoutCts);
                    parseOffset = receiveState.Start;
                    end = receiveState.End;
                    if (!receiveState.Success)
                    {
                        return new BodyReadState(RequestBodyReadResult.ClientClosed, parseOffset, end);
                    }
                }

                break;
            }
        }
    }

    /// <summary>
    /// Ensures that the <paramref name="request"/> has a body buffer with sufficient capacity to accommodate the desired length.
    /// If the current buffer is too small, a larger buffer is allocated, and existing data is copied into the new buffer.
    /// </summary>
    /// <param name="request">The <see cref="HttpRequest"/> associated with the body buffer.</param>
    /// <param name="requiredLength">The minimum required length of the buffer.</param>
    /// <param name="existingLength">The length of the existing data in the current buffer.</param>
    private static void EnsureBodyBufferCapacity(HttpRequest request, int requiredLength, int existingLength)
    {
        if (request.BodyBuffer is not null && request.BodyBuffer.Length >= requiredLength)
        {
            return;
        }

        var newSize = request.BodyBuffer is null
            ? requiredLength
            : Math.Max(requiredLength, request.BodyBuffer.Length * 2);

        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        if (request.BodyBuffer is not null)
        {
            request.BodyBuffer.AsSpan(0, existingLength).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(request.BodyBuffer);
        }

        request.BodyBuffer = newBuffer;
    }

    /// <summary>
    /// Receives data asynchronously from the specified <see cref="SocketReceiver"/> into the given memory buffer.
    /// This method handles optional read timeout logic using the provided cancellation token source.
    /// </summary>
    /// <param name="receiver">The <see cref="SocketReceiver"/> responsible for receiving data from the socket.</param>
    /// <param name="memory">The memory buffer where the received data will be stored.</param>
    /// <param name="readTimeoutCts">
    /// An optional <see cref="CancellationTokenSource"/> used to enforce a read timeout.
    /// If no timeout is required, this parameter can be null.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask{TResult}"/> representing the asynchronous operation.
    /// The result of the task is an <see cref="int"/> indicating the number of bytes received.
    /// </returns>
    private async ValueTask<int> ReceiveAsync(SocketReceiver receiver, Memory<byte> memory, CancellationTokenSource? readTimeoutCts)
    {
        ArmReadTimeout(readTimeoutCts);
        try
        {
            return await receiver.ReceiveAsync(_socket, memory);
        }
        finally
        {
            DisarmReadTimeout(readTimeoutCts);
        }
    }

    /// <summary>
    /// Reads additional data into the specified buffer from the provided <paramref name="receiver"/>.
    /// Adjusts the buffer indices and returns the updated state of the read operation.
    /// </summary>
    /// <param name="receiver">The <see cref="SocketReceiver"/> responsible for receiving data.</param>
    /// <param name="buf">The buffer where data will be stored.</param>
    /// <param name="start">The starting index of unread data in the buffer.</param>
    /// <param name="end">The ending index of unread data in the buffer.</param>
    /// <param name="readTimeoutCts">
    /// An optional <see cref="CancellationTokenSource"/> used to enforce a read timeout during the operation.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask"/> representing the completion of the operation, containing a <see cref="BufferReadState"/>
    /// that indicates whether the operation succeeded and the updated buffer indices.
    /// </returns>
    private async ValueTask<BufferReadState> ReceiveMoreIntoBufferAsync(
        SocketReceiver receiver,
        byte[] buf,
        int start,
        int end,
        CancellationTokenSource? readTimeoutCts)
    {
        if (end == buf.Length)
        {
            if (start == 0)
            {
                return new BufferReadState(false, start, end);
            }

            buf.AsSpan(start, end - start).CopyTo(buf);
            end -= start;
            start = 0;
        }

        var received = await ReceiveAsync(receiver, buf.AsMemory(end, buf.Length - end), readTimeoutCts);
        if (received == 0)
        {
            return new BufferReadState(false, start, end);
        }

        end += received;
        return new BufferReadState(true, start, end);
    }

    /// <summary>
    /// Configures the specified <paramref name="readTimeoutCts"/> to trigger a timeout
    /// after the duration defined by <see cref="ServerOptions.ReadTimeout"/> if it is not null.
    /// </summary>
    /// <param name="readTimeoutCts">
    /// The <see cref="CancellationTokenSource"/> to set the timeout for. If null, no action is taken.
    /// </param>
    private void ArmReadTimeout(CancellationTokenSource? readTimeoutCts)
    {
        readTimeoutCts?.CancelAfter(_serverOptions.ReadTimeout!.Value);
    }

    /// <summary>
    /// Disarms the read timeout for the given <paramref name="readTimeoutCts"/> by canceling any previously set timeout.
    /// </summary>
    /// <param name="readTimeoutCts">The <see cref="CancellationTokenSource"/> associated with the read timeout. If null, no action is taken.</param>
    private static void DisarmReadTimeout(CancellationTokenSource? readTimeoutCts)
    {
        readTimeoutCts?.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Determines whether a 100-Continue response should be sent based on the "Expect" header
    /// in the provided <see cref="HttpRequest"/>.
    /// </summary>
    /// <param name="request">The HTTP request to evaluate.</param>
    /// <returns>
    /// <see langword="true"/> if the "Expect" header exists with the value "100-continue"
    /// and the request either has chunked transfer encoding or a valid, positive content length;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    private static bool ShouldSend100Continue(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HttpHeaderNames.Expect, out var expectValue))
        {
            return false;
        }

        if (!AsciiEqualsIgnoreCase(expectValue.Trim((byte)' '), "100-continue"u8))
        {
            return false;
        }

        return request.HasChunkedTransferEncoding ||
               (request is { HasContentLength: true, HasInvalidContentLength: false, ContentLength: > 0 });
    }

    /// <summary>
    /// Compares two ASCII byte spans for equality, ignoring case.
    /// Returns <c>true</c> if the spans are equal in a case-insensitive manner; otherwise, <c>false</c>.
    /// </summary>
    /// <param name="a">The first ASCII <see cref="ReadOnlySpan{T}"/> to compare.</param>
    /// <param name="b">The second ASCII <see cref="ReadOnlySpan{T}"/> to compare.</param>
    /// <returns>
    /// <c>true</c> if the spans are equal, ignoring case; otherwise, <c>false</c>.
    /// </returns>
    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            byte x = a[i], y = b[i];
            if (x == y)
            {
                continue;
            }

            if ((uint)(x - 'A') <= 'Z' - 'A')
            {
                x |= 0x20;
            }

            if ((uint)(y - 'A') <= 'Z' - 'A')
            {
                y |= 0x20;
            }

            if (x != y)
            {
                return false;
            }
        }

        return true;
    }
}
