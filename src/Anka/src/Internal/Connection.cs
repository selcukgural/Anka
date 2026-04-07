using System.Buffers;
using System.Net.Sockets;

namespace Anka;

/// <summary>
/// Handles the full lifecycle of a single TCP connection: reading from the socket,
/// parsing HTTP/1.x requests, dispatching to the handler, and sending responses.
///
/// Uses a single-task, direct-socket approach — no <see cref="System.IO.Pipelines.Pipe"/>
/// in the middle.  On loopback (and often on LAN), <c>ReceiveAsync</c> and <c>SendAsync</c>
/// complete synchronously, so the entire request-response cycle runs without posting to the
/// thread pool.  This halves the async state-machine overhead compared to the previous
/// two-task (FillPipe + ReadPipe) architecture.
/// </summary>
internal sealed class Connection
{
    // Size of the per-connection receive buffer (64 KB)
    private const int BufferSize = 64 * 1024;

    private readonly Socket         _socket;
    private readonly RequestHandler _handler;
    private readonly CancellationToken _cancellationToken;

    private Connection(Socket socket, RequestHandler handler, CancellationToken cancellationToken)
    {
        _socket            = socket;
        _handler           = handler;
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
        var buf     = ArrayPool<byte>.Shared.Rent(BufferSize);
        var request = new HttpRequest();
        using var writer = new HttpResponseWriter(_socket);

        try
        {
            var filled = 0; // bytes in buf[0..filled)

            while (true)
            {
                // Receive more data into the unused portion of the buffer.
                var received = await _socket.ReceiveAsync(
                    buf.AsMemory(filled, buf.Length - filled),
                    SocketFlags.None,
                    _cancellationToken);

                if (received == 0)
                {
                    break; // client closed the connection
                }

                filled += received;

                // Parse and handle as many complete requests as the buffer holds.
                var offset = 0;

                while (offset < filled)
                {
                    // Reset for reuse — keeps buffers, clears fields.
                    request.ResetForReuse();

                    if (!TryParseNext(buf, offset, filled - offset, request, out var bytesConsumed))
                    {
                        break; // incomplete request — wait for more bytes
                    }

                    offset += bytesConsumed;

                    var keepAlive = request.IsKeepAlive;

                    await _handler(request, writer, _cancellationToken);

                    if (!keepAlive)
                    {
                        return;
                    }
                }

                // Compact: shift unprocessed bytes to the front.
                if (offset > 0)
                {
                    buf.AsSpan(offset, filled - offset).CopyTo(buf);
                    filled -= offset;
                }

                // Guard against a request that is larger than the buffer.
                if (filled == buf.Length)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        finally
        {
            request.Dispose();
            ArrayPool<byte>.Shared.Return(buf);
            _socket.Close();
            _socket.Dispose();
        }
    }

    /// <summary>
    /// Synchronous wrapper: keeps <see cref="SequenceReader{T}"/> (a ref struct) out of
    /// the async state machine. Returns the number of bytes consumed on success.
    /// </summary>
    private static bool TryParseNext(byte[] buf, int offset, int length,
        HttpRequest request, out int consumed)
    {
        var seq    = new ReadOnlySequence<byte>(buf, offset, length);
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
