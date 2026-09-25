using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Anka.Exceptions;

namespace Anka.Test;

/// <summary>
/// Resource-exhaustion defences: safe defaults, the absolute header deadline, the concurrent
/// connection cap, incremental body buffering and releasing body buffers on pool return.
/// </summary>
public class ConnectionResourceLimitTests
{
    private static readonly byte[] OkBody         = "OK"u8.ToArray();
    private static readonly byte[] TextPlainBytes = "text/plain"u8.ToArray();

    // ── Defaults and validation ──────────────────────────────────────────────

    [Fact]
    public void ServerOptions_Defaults_AreSafe()
    {
        var options = new ServerOptions();

        Assert.Equal(30_000_000, options.MaxRequestBodySize);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ReadTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.RequestHeadersTimeout);
        Assert.Null(options.MaxConcurrentConnections);
    }

    [Fact]
    public void ServerOptions_LimitsCanBeDisabledExplicitly()
    {
        var options = new ServerOptions
        {
            MaxRequestBodySize = null,
            ReadTimeout = null,
            RequestHeadersTimeout = null
        };

        Assert.Null(options.MaxRequestBodySize);
        Assert.Null(options.ReadTimeout);
        Assert.Null(options.RequestHeadersTimeout);
    }

    [Fact]
    public void RequestHeadersTimeout_Negative_ThrowsAnkaOutOfRangeException()
    {
        var options = new ServerOptions();
        Assert.Throws<AnkaOutOfRangeException>(() => options.RequestHeadersTimeout = TimeSpan.FromMilliseconds(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxConcurrentConnections_LessThanOne_ThrowsAnkaOutOfRangeException(int value)
    {
        var options = new ServerOptions();
        Assert.Throws<AnkaOutOfRangeException>(() => options.MaxConcurrentConnections = value);
    }

    // ── Header deadline (Slowloris) ──────────────────────────────────────────

    [Fact]
    public async Task SlowHeaderDrip_ExceedingRequestHeadersTimeout_ClosesConnection()
    {
        // The per-read timeout is generous and every byte arrives well within it, so only
        // the absolute header deadline can end this connection.
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            new ServerOptions { ReadTimeout = TimeSpan.FromSeconds(5), RequestHeadersTimeout = TimeSpan.FromMilliseconds(500) });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        var stopwatch = Stopwatch.StartNew();

        var readTask = ReadUntilClosedAsync(stream, TimeSpan.FromSeconds(5));

        try
        {
            await stream.WriteAsync("GET / HTTP/1.1\r\nHost: example.com\r\nX-Slow: "u8.ToArray());
            while (!readTask.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(100);
                await stream.WriteAsync("a"u8.ToArray());
            }
        }
        catch (IOException)
        {
            // Expected once the server has closed the socket.
        }

        var response = await readTask;
        stopwatch.Stop();

        Assert.Equal(string.Empty, response);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task KeepAliveIdleTime_DoesNotCountTowardRequestHeadersTimeout()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, req.IsKeepAlive, ct),
            new ServerOptions { ReadTimeout = TimeSpan.FromSeconds(5), RequestHeadersTimeout = TimeSpan.FromMilliseconds(300) });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();

        var first = await SendAndReadAsync(stream, "GET /1 HTTP/1.1\r\nHost: example.com\r\n\r\n");
        await Task.Delay(700); // idle between requests, longer than the header deadline
        var second = await SendAndReadAsync(stream, "GET /2 HTTP/1.1\r\nHost: example.com\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", first);
        Assert.StartsWith("HTTP/1.1 200 OK", second);
    }

    [Fact]
    public async Task IdleConnection_ExceedingReadTimeout_ClosesConnection()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            new ServerOptions { ReadTimeout = TimeSpan.FromMilliseconds(300), RequestHeadersTimeout = null });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);

        var response = await ReadUntilClosedAsync(client.GetStream(), TimeSpan.FromSeconds(5));

        Assert.Equal(string.Empty, response);
    }

    // ── Concurrent connection cap ────────────────────────────────────────────

    [Fact]
    public async Task MaxConcurrentConnections_Reached_ClosesNewConnectionsUntilSlotFrees()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, req.IsKeepAlive, ct),
            new ServerOptions { MaxConcurrentConnections = 1 });

        var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, server.Port);
        var firstResponse = await SendAndReadAsync(first.GetStream(), "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 OK", firstResponse);

        using (var rejected = new TcpClient())
        {
            await rejected.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = rejected.GetStream();
            try
            {
                await stream.WriteAsync("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray());
            }
            catch (IOException)
            {
                // The server may already have closed the socket.
            }

            Assert.Equal(string.Empty, await ReadUntilClosedAsync(stream, TimeSpan.FromSeconds(5)));
        }

        first.Dispose();

        // The slot is released asynchronously once the server notices the close.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string response;
        do
        {
            await Task.Delay(50);
            using var next = new TcpClient();
            await next.ConnectAsync(IPAddress.Loopback, server.Port);
            response = await SendAndReadAsync(next.GetStream(), "GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");
        }
        while (response.Length == 0 && DateTime.UtcNow < deadline);

        Assert.StartsWith("HTTP/1.1 200 OK", response);
    }

    // ── Incremental body buffering ───────────────────────────────────────────

    [Fact]
    public async Task LargeContentLengthBody_SentInPieces_ArrivesIntact()
    {
        // Larger than both the receive buffer and the initial body reservation, so the body
        // buffer has to grow several times while bytes are arriving.
        var body = new byte[1_000_003];
        new Random(42).NextBytes(body);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) =>
            {
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(req.Body.Span));
                return res.WriteAsync(200, Encoding.ASCII.GetBytes($"{req.Body.Length}:{hash}"), TextPlainBytes, false, ct);
            });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST / HTTP/1.1\r\nHost: example.com\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
        for (var offset = 0; offset < body.Length; offset += 100_000)
        {
            await stream.WriteAsync(body.AsMemory(offset, Math.Min(100_000, body.Length - offset)));
            await Task.Delay(5);
        }

        var response = await ReadUntilClosedAsync(stream, TimeSpan.FromSeconds(5));

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.EndsWith($"{body.Length}:{expected}", response);
    }

    [Fact]
    public async Task LargeChunk_ArrivesIntact()
    {
        var chunk = new byte[300_000];
        new Random(7).NextBytes(chunk);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            chunk.Concat("tail"u8.ToArray()).ToArray()));

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) =>
            {
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(req.Body.Span));
                return res.WriteAsync(200, Encoding.ASCII.GetBytes($"{req.Body.Length}:{hash}"), TextPlainBytes, false, ct);
            });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"POST / HTTP/1.1\r\nHost: example.com\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n{chunk.Length:X}\r\n"));
        await stream.WriteAsync(chunk);
        await stream.WriteAsync("\r\n4\r\ntail\r\n0\r\n\r\n"u8.ToArray());

        var response = await ReadUntilClosedAsync(stream, TimeSpan.FromSeconds(5));

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.EndsWith($"{chunk.Length + 4}:{expected}", response);
    }

    [Fact]
    public async Task ChunkedBody_TotalSizeOverflowingInt_Returns413EvenWithoutLimit()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            new ServerOptions { MaxRequestBodySize = null });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();

        await stream.WriteAsync(
            "POST / HTTP/1.1\r\nHost: example.com\r\nTransfer-Encoding: chunked\r\n\r\n10\r\n0123456789abcdef\r\n7FFFFFFF\r\n"u8.ToArray());

        var response = await ReadUntilClosedAsync(stream, TimeSpan.FromSeconds(5));

        Assert.StartsWith("HTTP/1.1 413 Payload Too Large", response);
    }

    [Fact]
    public async Task DefaultBodyLimit_RejectsOversizedContentLength()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "POST / HTTP/1.1\r\nHost: example.com\r\nContent-Length: 30000001\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 413 Payload Too Large", response);
    }

    // ── Pool return ──────────────────────────────────────────────────────────

    [Fact]
    public void HttpRequestPool_Return_ReleasesBodyBufferButKeepsHeaderBuffer()
    {
        var req = new HttpRequest
        {
            Buffer = new byte[1024],
            BodyBuffer = new byte[4 * 1024 * 1024]
        };
        req.Body = req.BodyBuffer.AsMemory(0, 10);

        HttpRequestPool.Return(req);

        Assert.Null(req.BodyBuffer);
        Assert.True(req.Body.IsEmpty);
        Assert.NotNull(req.Buffer);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<string> SendRawAsync(int port, string rawRequest)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await stream.WriteAsync(Encoding.ASCII.GetBytes(rawRequest), timeout.Token);

        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, timeout.Token);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    /// <summary>Sends a request on an existing connection and reads one response; empty if the server closed.</summary>
    private static async Task<string> SendAndReadAsync(NetworkStream stream, string rawRequest)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(rawRequest), timeout.Token);
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            return Encoding.ASCII.GetString(buffer, 0, read);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Reads until the server closes the connection; throws if it is still open after <paramref name="limit"/>.</summary>
    private static async Task<string> ReadUntilClosedAsync(NetworkStream stream, TimeSpan limit)
    {
        using var timeout = new CancellationTokenSource(limit);
        var sb = new StringBuilder();
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }
        catch (IOException)
        {
            // Connection reset counts as closed.
        }

        return sb.ToString();
    }

    private sealed class TestServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _runTask;

        private TestServer(int port, CancellationTokenSource cts, Task runTask)
        {
            Port     = port;
            _cts     = cts;
            _runTask = runTask;
        }

        public int Port { get; }

        public static async Task<TestServer> StartAsync(RequestHandler handler, ServerOptions? options = null)
        {
            var port   = GetFreePort();
            var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ready  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new Server(handler, port, options: options);

            server.ListeningStarted += _ => ready.TrySetResult();

            var runTask = server.StartAsync(cts.Token);
            await ready.Task;

            return new TestServer(port, cts, runTask);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _runTask;
            _cts.Dispose();
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
