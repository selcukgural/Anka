using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anka.Test;

/// <summary>
/// Integration tests for Content-Length framing validation.
/// Verifies that malformed Content-Length values are rejected as bad request framing.
/// </summary>
public class ContentLengthValidationTests
{
    private static readonly byte[] OkBody         = "OK"u8.ToArray();
    private static readonly byte[] TextPlainBytes = "text/plain"u8.ToArray();

    // ── POST ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_WithValidContentLengthAndBody_Returns200()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\n\r\nhello");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Post_WithContentLengthZero_Returns200()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: 0\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Post_WithNoContentLength_Returns200()
    {
        // RFC 9110 §8.6: SHOULD NOT send Content-Length for zero-length body — must be accepted
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "POST /api HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Post_WithNegativeContentLength_Returns400()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: -1\r\n\r\n");

        Assert.Contains("HTTP/1.1 400 Bad Request", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task Post_WithNonNumericContentLength_Returns400()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: abc\r\n\r\n");

        Assert.Contains("HTTP/1.1 400 Bad Request", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task Post_WithDuplicateContentLengthSameValue_Returns200()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\nhello");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Post_WithConflictingDuplicateContentLength_Returns400()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "POST /api HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\nContent-Length: 7\r\nConnection: close\r\n\r\nhello!!");

        Assert.Contains("HTTP/1.1 400 Bad Request", response);
        Assert.Contains("Connection: close", response);
    }

    // ── PUT ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_WithValidContentLength_Returns200()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "PUT /resource HTTP/1.1\r\nHost: example.com\r\nContent-Length: 4\r\n\r\ndata");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Put_WithNoContentLength_Returns200()
    {
        // RFC 9110 §8.6: same rule applies to PUT
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "PUT /resource HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Put_WithNegativeContentLength_Returns400()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "PUT /resource HTTP/1.1\r\nHost: example.com\r\nContent-Length: -5\r\n\r\n");

        Assert.Contains("HTTP/1.1 400 Bad Request", response);
    }

    // ── PATCH ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_WithValidContentLength_Returns200()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "PATCH /resource HTTP/1.1\r\nHost: example.com\r\nContent-Length: 2\r\n\r\nok");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Patch_WithNoContentLength_Returns200()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "PATCH /resource HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Patch_WithNonNumericContentLength_Returns400()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "PATCH /resource HTTP/1.1\r\nHost: example.com\r\nContent-Length: bad\r\n\r\n");

        Assert.Contains("HTTP/1.1 400 Bad Request", response);
    }

    // ── Non-body methods (GET, DELETE, HEAD, OPTIONS) ────────────────────────

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task NonBodyMethod_WithNoContentLength_Returns200(string method)
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            $"{method} /resource HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task NonBodyMethod_WithInvalidContentLength_Returns400(string method)
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            $"{method} /resource HTTP/1.1\r\nHost: example.com\r\nContent-Length: -1\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 400 Bad Request", response);
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

        public static async Task<TestServer> StartAsync(RequestHandler handler)
        {
            var port   = GetFreePort();
            var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ready  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = new Server(handler, port);

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
