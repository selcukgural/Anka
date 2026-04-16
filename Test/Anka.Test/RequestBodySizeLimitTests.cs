using System.Net;
using System.Net.Sockets;
using System.Text;
using Anka.Exceptions;

namespace Anka.Test;

/// <summary>
/// Integration tests for 413 Payload Too Large validation.
/// Verifies that MaxRequestBodySize is enforced when set, and bypassed when null.
/// </summary>
public class RequestBodySizeLimitTests
{
    private static readonly byte[] OkBody         = "OK"u8.ToArray();
    private static readonly byte[] TextPlainBytes = "text/plain"u8.ToArray();

    // ── ServerOptions validation (unit tests) ─────────────────────────────

    [Fact]
    public void MaxRequestBodySize_SetNegative_ThrowsAnkaOutOfRangeException()
    {
        var options = new ServerOptions();
        Assert.Throws<AnkaOutOfRangeException>(() => options.MaxRequestBodySize = -1);
    }

    [Fact]
    public void MaxRequestBodySize_SetZero_DoesNotThrow()
    {
        var options = new ServerOptions();
        options.MaxRequestBodySize = 0;
        Assert.Equal(0, options.MaxRequestBodySize);
    }

    [Fact]
    public void MaxRequestBodySize_DefaultIsNull()
    {
        var options = new ServerOptions();
        Assert.Null(options.MaxRequestBodySize);
    }

    // ── No limit (null) ───────────────────────────────────────────────────

    [Fact]
    public async Task Post_LargeBody_NoLimitConfigured_Returns200()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: new ServerOptions { MaxRequestBodySize = null });

        var body = new string('x', 10_000);
        var response = await SendRawAsync(server.Port,
            $"POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    // ── Within limit ──────────────────────────────────────────────────────

    [Fact]
    public async Task Post_BodyWithinLimit_Returns200()
    {
        var options = new ServerOptions();
        options.MaxRequestBodySize = 100;

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: options);

        var response = await SendRawAsync(server.Port,
            "POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Post_BodyExactlyAtLimit_Returns200()
    {
        var body = new string('a', 50);
        var options = new ServerOptions();
        options.MaxRequestBodySize = 50;

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: options);

        var response = await SendRawAsync(server.Port,
            $"POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: 50\r\nConnection: close\r\n\r\n{body}");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    // ── Exceeds limit ─────────────────────────────────────────────────────

    [Fact]
    public async Task Post_BodyExceedsLimit_Returns413()
    {
        var options = new ServerOptions();
        options.MaxRequestBodySize = 10;

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: options);

        var response = await SendRawAsync(server.Port,
            "POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: 100\r\nConnection: close\r\n\r\n" + new string('x', 100));

        Assert.Contains("HTTP/1.1 413 Payload Too Large", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task Put_BodyExceedsLimit_Returns413()
    {
        var options = new ServerOptions();
        options.MaxRequestBodySize = 5;

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: options);

        var response = await SendRawAsync(server.Port,
            "PUT /resource HTTP/1.1\r\nHost: example.com\r\nContent-Length: 50\r\nConnection: close\r\n\r\n" + new string('y', 50));

        Assert.Contains("HTTP/1.1 413 Payload Too Large", response);
    }

    [Fact]
    public async Task Patch_BodyExceedsLimit_Returns413()
    {
        var options = new ServerOptions();
        options.MaxRequestBodySize = 5;

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: options);

        var response = await SendRawAsync(server.Port,
            "PATCH /resource HTTP/1.1\r\nHost: example.com\r\nContent-Length: 20\r\nConnection: close\r\n\r\n" + new string('z', 20));

        Assert.Contains("HTTP/1.1 413 Payload Too Large", response);
    }

    // ── Content-Length: 0 always passes ───────────────────────────────────

    [Fact]
    public async Task Post_ContentLengthZero_LimitIsZero_Returns200()
    {
        // CL: 0 means no body — must not be blocked even when limit is 0
        var options = new ServerOptions();
        options.MaxRequestBodySize = 0;

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: options);

        var response = await SendRawAsync(server.Port,
            "POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

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
            var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
