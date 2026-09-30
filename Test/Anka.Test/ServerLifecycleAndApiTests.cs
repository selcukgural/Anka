using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anka.Test;

/// <summary>
/// Graceful shutdown, IPv6 listening, status-line reason phrases, status-code validation,
/// <see cref="HttpRequest.TryGetRange"/> and <see cref="MultipartParser.TryGetBoundary"/>.
/// </summary>
public class ServerLifecycleAndApiTests
{
    private static readonly byte[] OkBody         = "OK"u8.ToArray();
    private static readonly byte[] TextPlainBytes = "text/plain"u8.ToArray();

    // ── Graceful shutdown ────────────────────────────────────────────────────

    [Fact]
    public async Task Shutdown_InFlightRequest_CompletesWithConnectionClose()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await RunningServer.StartAsync(async (req, res, ct) =>
        {
            entered.TrySetResult();
            await release.Task;
            await res.WriteAsync(200, OkBody, TextPlainBytes, req.IsKeepAlive, ct);
        });

        using var client = await ConnectAsync(server.Port);
        await SendAsync(client, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await server.StopAsync();
        Assert.False(server.RunTask.IsCompleted, "StartAsync must wait for the in-flight request");

        release.SetResult();
        var response = await ReadToEndAsync(client);

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Connection: close", response);
        await server.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Shutdown_IdleKeepAliveConnection_IsClosedImmediately()
    {
        await using var server = await RunningServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, req.IsKeepAlive, ct),
            new ServerOptions { ShutdownTimeout = TimeSpan.FromSeconds(30) });

        using var client = await ConnectAsync(server.Port);
        await SendAsync(client, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        var first = await ReadSomeAsync(client);
        Assert.StartsWith("HTTP/1.1 200 OK", first);

        var stopwatch = Stopwatch.StartNew();
        await server.StopAsync();
        await server.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"shutdown took {stopwatch.Elapsed}");
        Assert.Equal(string.Empty, await ReadToEndAsync(client));
    }

    [Fact]
    public async Task Shutdown_HandlerOutlivesTimeout_IsCancelledAndAborted()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerToken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await RunningServer.StartAsync(async (_, _, ct) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                handlerToken.TrySetResult(true);
                throw;
            }
        }, new ServerOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(200) });

        using var client = await ConnectAsync(server.Port);
        await SendAsync(client, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        await server.StopAsync();
        await server.RunTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(await handlerToken.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(150), $"shutdown returned after {stopwatch.Elapsed}");
    }

    [Fact]
    public void ShutdownTimeout_Negative_Throws()
    {
        Assert.Throws<Anka.Exceptions.AnkaOutOfRangeException>(() => new ServerOptions { ShutdownTimeout = TimeSpan.FromSeconds(-1) });
    }

    // ── IPv6 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Server_IPv6Loopback_AcceptsIPv6Clients()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        await using var server = await RunningServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, req.IsKeepAlive, ct), host: "::1");

        using var client = new TcpClient(AddressFamily.InterNetworkV6);
        await client.ConnectAsync(IPAddress.IPv6Loopback, server.Port);
        await SendAsync(client, "GET / HTTP/1.1\r\nHost: [::1]\r\nConnection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", await ReadToEndAsync(client));
    }

    [Fact]
    public async Task Server_IPv6Any_IsDualStackAndAcceptsIPv4Clients()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        await using var server = await RunningServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, req.IsKeepAlive, ct), host: "::");

        using var client = await ConnectAsync(server.Port);
        await SendAsync(client, "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", await ReadToEndAsync(client));
    }

    // ── Status line ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(202, "HTTP/1.1 202 Accepted\r\n")]
    [InlineData(307, "HTTP/1.1 307 Temporary Redirect\r\n")]
    [InlineData(409, "HTTP/1.1 409 Conflict\r\n")]
    [InlineData(416, "HTTP/1.1 416 Range Not Satisfiable\r\n")]
    [InlineData(422, "HTTP/1.1 422 Unprocessable Content\r\n")]
    [InlineData(502, "HTTP/1.1 502 Bad Gateway\r\n")]
    [InlineData(599, "HTTP/1.1 599 \r\n")] // unknown code: empty reason phrase (RFC 9112 §4)
    public async Task WriteAsync_StatusCode_HasStandardReasonPhrase(int statusCode, string expectedStatusLine)
    {
        await using var server = await RunningServer.StartAsync(
            (req, res, ct) => res.WriteAsync(statusCode, OkBody, TextPlainBytes, req.IsKeepAlive, ct));

        var response = await RequestAsync(server.Port, "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.StartsWith(expectedStatusLine, response);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(199)]
    [InlineData(1000)]
    public async Task WriteAsync_InvalidStatusCode_Throws(int statusCode)
    {
        var thrown = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await RunningServer.StartAsync(async (req, res, ct) =>
        {
            try
            {
                await res.WriteAsync(statusCode, OkBody, TextPlainBytes, req.IsKeepAlive, ct);
                thrown.TrySetResult(null);
            }
            catch (Exception ex)
            {
                thrown.TrySetResult(ex);
            }
        });

        var response = await RequestAsync(server.Port, "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.IsType<ArgumentOutOfRangeException>(await thrown.Task);
        Assert.StartsWith("HTTP/1.1 200 OK", response); // nothing was written, so Anka sends the default 200
    }

    // ── HttpRequest.TryGetRange ──────────────────────────────────────────────

    [Theory]
    [InlineData("bytes=0-9", 100, 0, 9)]
    [InlineData("bytes=10-", 100, 10, 99)]
    [InlineData("bytes=-5", 100, 95, 99)]
    [InlineData("bytes=-500", 100, 0, 99)]   // suffix longer than the body: whole body
    [InlineData("bytes=90-200", 100, 90, 99)] // end clamped
    public void TryGetRange_SatisfiableRange_Resolves(string range, long length, long expectedStart, long expectedEnd)
    {
        var req = Parse($"GET / HTTP/1.1\r\nHost: x\r\nRange: {range}\r\n\r\n");

        Assert.True(req.TryGetRange(length, out var start, out var end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
        req.Return();
    }

    [Theory]
    [InlineData("GET", "bytes=100-", 100)]  // starts past the end
    [InlineData("GET", "bytes=-0", 100)]    // empty suffix
    [InlineData("GET", "bytes=0-1,5-6", 100)]
    [InlineData("GET", "items=0-1", 100)]
    [InlineData("GET", "bytes=0-9", 0)]     // empty representation
    [InlineData("HEAD", "bytes=0-9", 100)]  // Range only applies to GET
    public void TryGetRange_UnusableRange_ReturnsFalse(string method, string range, long length)
    {
        var req = Parse($"{method} / HTTP/1.1\r\nHost: x\r\nRange: {range}\r\n\r\n");

        Assert.False(req.TryGetRange(length, out _, out _));
        req.Return();
    }

    [Fact]
    public void TryGetRange_NoRangeHeader_ReturnsFalse()
    {
        var req = Parse("GET / HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.False(req.TryGetRange(100, out _, out _));
        req.Return();
    }

    // ── MultipartParser.TryGetBoundary ───────────────────────────────────────

    [Theory]
    [InlineData("multipart/form-data; boundary=abc", "abc")]
    [InlineData("multipart/form-data; boundary=\"a b;c\"", "a b;c")]
    [InlineData("Multipart/Form-Data;charset=utf-8; BOUNDARY=xyz", "xyz")]
    [InlineData("multipart/form-data ;  boundary = ----WebKitFormBoundary7MA4", "----WebKitFormBoundary7MA4")]
    public void TryGetBoundary_ValidContentType_ReturnsBoundary(string contentType, string expected)
    {
        Assert.True(MultipartParser.TryGetBoundary(Encoding.ASCII.GetBytes(contentType), out var boundary));
        Assert.Equal(expected, Encoding.ASCII.GetString(boundary));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("multipart/form-data")]
    [InlineData("multipart/mixed; boundary=abc")]
    [InlineData("multipart/form-data; boundary=")]
    [InlineData("multipart/form-data; boundary=\"\"")]
    public void TryGetBoundary_InvalidContentType_ReturnsFalse(string contentType)
    {
        Assert.False(MultipartParser.TryGetBoundary(Encoding.ASCII.GetBytes(contentType), out _));
    }

    [Fact]
    public void TryGetBoundary_TooLong_ReturnsFalse()
    {
        var contentType = "multipart/form-data; boundary=" + new string('a', 71);

        Assert.False(MultipartParser.TryGetBoundary(Encoding.ASCII.GetBytes(contentType), out _));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HttpRequest Parse(string raw)
    {
        var reader = new System.Buffers.SequenceReader<byte>(new System.Buffers.ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(raw)));
        var req = new HttpRequest();
        Assert.Equal(HttpParseResult.Success, HttpParser.TryParse(ref reader, req));
        return req;
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static Task SendAsync(TcpClient client, string raw)
        => client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(raw)).AsTask();

    private static async Task<string> ReadSomeAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        var read = await client.GetStream().ReadAsync(buffer, timeout.Token);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private static async Task<string> ReadToEndAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var received = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await client.GetStream().ReadAsync(buffer, timeout.Token)) > 0)
        {
            received.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(received.ToArray());
    }

    private static async Task<string> RequestAsync(int port, string raw)
    {
        using var client = await ConnectAsync(port);
        await SendAsync(client, raw);
        return await ReadToEndAsync(client);
    }

    /// <summary>A server whose shutdown the test controls, to observe draining.</summary>
    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;

        private RunningServer(int port, CancellationTokenSource cts, Task runTask)
        {
            Port    = port;
            _cts    = cts;
            RunTask = runTask;
        }

        public int Port { get; }

        public Task RunTask { get; }

        public static async Task<RunningServer> StartAsync(RequestHandler handler, ServerOptions? options = null, string host = "127.0.0.1")
        {
            var port   = GetFreePort();
            var cts    = new CancellationTokenSource();
            var ready  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new Server(handler, port, host, options);

            server.ListeningStarted += _ => ready.TrySetResult();

            var runTask = server.StartAsync(cts.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

            return new RunningServer(port, cts, runTask);
        }

        public Task StopAsync() => _cts.CancelAsync();

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await RunTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                _cts.Dispose();
            }
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
