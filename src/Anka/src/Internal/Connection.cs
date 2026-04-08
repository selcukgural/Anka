using System.Buffers;
using System.Net.Sockets;

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
    private readonly CancellationToken _cancellationToken;

    private Connection(Socket socket, RequestHandler handler, CancellationToken cancellationToken)
    {
        _socket = socket;
        _handler = handler;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Creates a <see cref="Connection"/> for <paramref name="socket"/> and starts processing it.
    /// Returns a <see cref="Task"/> that completes when the connection closes.
    /// </summary>
    public static Task RunAsync(Socket socket, RequestHandler handler, CancellationToken cancellationToken)
    {
        socket.NoDelay = true;
        return new Connection(socket, handler, cancellationToken).ProcessAsync();
    }

    private async Task ProcessAsync()
    {
        var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        var request = HttpRequestPool.Rent();
        using var writer = new HttpResponseWriter(_socket);
        using var receiver = new SocketReceiver();

        // Closing the socket aborts any pending SocketAsyncEventArgs operation,
        // which causes ReceiveAsync to throw SocketException — caught below.
        await using var reg = _cancellationToken.Register(static s => ((Socket)s!).Close(), _socket);

        try
        {
            var start = 0; // first unread byte
            var end = 0;   // one past the last filled byte

            while (true)
            {
                // Avoid copying unread bytes after every request. Keep a sliding window and
                // compact only when the receive buffer has no writable tail left.
                if (end == buf.Length)
                {
                    if (start == 0)
                    {
                        break; // request larger than the receive buffer
                    }

                    buf.AsSpan(start, end - start).CopyTo(buf);
                    end -= start;
                    start = 0;
                }

                // Receive more data into the unused portion of the buffer.
                var received = await receiver.ReceiveAsync(_socket, buf.AsMemory(end, buf.Length - end));

                if (received == 0)
                {
                    break; // client closed the connection
                }

                end += received;

                // Parse and handle as many complete requests as the buffer holds.
                var parseOffset = start;

                while (parseOffset < end)
                {
                    // Reset for reuse — keeps buffers, clears fields.
                    request.ResetForReuse();

                    if (!TryParseNext(buf, parseOffset, end - parseOffset, request, out var bytesConsumed))
                    {
                        break; // incomplete request — wait for more bytes
                    }

                    parseOffset += bytesConsumed;

                    var keepAlive = request.IsKeepAlive;

                    if (request.HasChunkedTransferEncoding)
                    {
                        await writer.WriteAsync(501, keepAlive: false, cancellationToken: _cancellationToken);
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
            HttpRequestPool.Return(request);
            ArrayPool<byte>.Shared.Return(buf);
            _socket.Close();
            _socket.Dispose();
        }
    }

    /// <summary>
    /// Synchronous wrapper: keeps <see cref="SequenceReader{T}"/> (a ref struct) out of
    /// the async state machine. Returns the number of bytes consumed on success.
    /// </summary>
    private static bool TryParseNext(byte[] buf, int offset, int length, HttpRequest request, out int consumed)
    {
        var seq = new ReadOnlySequence<byte>(buf, offset, length);
        var reader = new SequenceReader<byte>(seq);

        if (HttpParser.TryParse(ref reader, request))
        {
            consumed = (int)reader.Consumed;
            return true;
        }

        consumed = 0;
        return false;
    }
}