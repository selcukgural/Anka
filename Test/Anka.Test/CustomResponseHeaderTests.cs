using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anka.Test;

/// <summary>
/// Tests for <see cref="HttpResponseWriter.WriteAsync"/> with extra response headers.
/// </summary>
public class CustomResponseHeaderTests
{
    private static readonly byte[] EmptyBody     = [];
    private static readonly byte[] HelloBody     = "Hello"u8.ToArray();
    private static readonly byte[] AppJsonBytes  = "application/json"u8.ToArray();

    // ── Redirect (301 + Location) ─────────────────────────────────────────────

    [Fact]
    public async Task Redirect_WritesLocationHeader()
    {
        var locationHeader = new HttpHeader("location"u8.ToArray(), "/new-path"u8.ToArray());

        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res.WriteAsync(301, EmptyBody, default, false,
                new[] { locationHeader }, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("HTTP/1.1 301 Moved Permanently", response);
        Assert.Contains("location: /new-path", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection: close", response);
    }

    // ── CORS headers ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CorsHeaders_AllWrittenToResponse()
    {
        HttpHeader[] corsHeaders =
        [
            new(HttpHeaderNames.AccessControlAllowOrigin.ToArray(),  "*"u8.ToArray()),
            new(HttpHeaderNames.AccessControlAllowMethods.ToArray(), "GET, POST"u8.ToArray()),
            new(HttpHeaderNames.AccessControlMaxAge.ToArray(),       "86400"u8.ToArray()),
        ];

        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res.WriteAsync(200, HelloBody, AppJsonBytes, true, corsHeaders, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("access-control-allow-origin: *", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("access-control-allow-methods: GET, POST", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("access-control-max-age: 86400", response, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Hello", response, StringComparison.Ordinal);
    }

    // ── Set-Cookie ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetCookie_AppearsInResponse()
    {
        var cookieHeader = new HttpHeader(
            HttpHeaderNames.SetCookie.ToArray(),
            "session=abc123; HttpOnly; Path=/"u8.ToArray());

        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res.WriteAsync(200, HelloBody, default, true,
                new[] { cookieHeader }, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("set-cookie: session=abc123; HttpOnly; Path=/", response, StringComparison.OrdinalIgnoreCase);
    }

    // ── Multiple extra headers ────────────────────────────────────────────────

    [Fact]
    public async Task MultipleExtraHeaders_AllPresent()
    {
        HttpHeader[] headers =
        [
            new("x-request-id"u8.ToArray(), "abc-123"u8.ToArray()),
            new("x-rate-limit"u8.ToArray(), "100"u8.ToArray()),
            new("etag"u8.ToArray(),         "\"v1\""u8.ToArray()),
        ];

        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res.WriteAsync(200, HelloBody, AppJsonBytes, true, headers, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("x-request-id: abc-123", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-rate-limit: 100", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("etag: \"v1\"", response, StringComparison.OrdinalIgnoreCase);
    }

    // ── String-constructor convenience ────────────────────────────────────────

    [Fact]
    public async Task HttpHeader_StringConstructor_WritesCorrectly()
    {
        var header = new HttpHeader("Cache-Control", "no-store, max-age=0");

        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res.WriteAsync(200, HelloBody, default, true,
                new[] { header }, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("cache-control: no-store, max-age=0", response, StringComparison.OrdinalIgnoreCase);
    }

    // ── Backward compat: existing overload (no extra headers) ────────────────

    [Fact]
    public async Task ExistingOverload_NoExtraHeaders_StillWorks()
    {
        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res.WriteAsync(200, HelloBody, AppJsonBytes, true, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("Content-Type: application/json", response);
        Assert.EndsWith("Hello", response, StringComparison.Ordinal);
        Assert.DoesNotContain("location:", response, StringComparison.OrdinalIgnoreCase);
    }

    // ── Empty extra headers span ──────────────────────────────────────────────

    [Fact]
    public async Task EmptyExtraHeaders_ProducesNormalResponse()
    {
        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res.WriteAsync(200, HelloBody, AppJsonBytes, true,
                ReadOnlySpan<HttpHeader>.Empty, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("Content-Length: 5", response);
        Assert.EndsWith("Hello", response, StringComparison.Ordinal);
    }

    // ── Fluent extension: single AddHeader ───────────────────────────────────

    [Fact]
    public async Task FluentSingleHeader_WritesHeader()
    {
        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res
                .AddHeader(HttpHeaderNames.Location, "/new-path"u8.ToArray())
                .WriteAsync(301, default, default, keepAlive: false, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("HTTP/1.1 301 Moved Permanently", response);
        Assert.Contains("location: /new-path", response, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fluent extension: chained AddHeader ──────────────────────────────────

    [Fact]
    public async Task FluentChainedHeaders_AllWritten()
    {
        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res
                .AddHeader(HttpHeaderNames.AccessControlAllowOrigin,  "*"u8.ToArray())
                .AddHeader(HttpHeaderNames.AccessControlAllowMethods, "GET, POST"u8.ToArray())
                .AddHeader(HttpHeaderNames.AccessControlMaxAge,       "86400"u8.ToArray())
                .WriteAsync(200, HelloBody, AppJsonBytes, keepAlive: true, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("access-control-allow-origin: *",          response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("access-control-allow-methods: GET, POST", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("access-control-max-age: 86400",           response, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Hello", response, StringComparison.Ordinal);
    }

    // ── Fluent extension: string overload ────────────────────────────────────

    [Fact]
    public async Task FluentStringOverload_LowercasesName()
    {
        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res
                .AddHeader("Cache-Control", "no-cache")
                .WriteAsync(200, HelloBody, AppJsonBytes, keepAlive: true, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("cache-control: no-cache", response, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fluent extension: many headers (no limit) ────────────────────────────

    [Fact]
    public async Task FluentAddHeader_ManyHeaders_AllWritten()
    {
        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res
                .AddHeader("x-h1"u8, "v1"u8)
                .AddHeader("x-h2"u8, "v2"u8)
                .AddHeader("x-h3"u8, "v3"u8)
                .AddHeader("x-h4"u8, "v4"u8)
                .AddHeader("x-h5"u8, "v5"u8)
                .AddHeader("x-h6"u8, "v6"u8)
                .AddHeader("x-h7"u8, "v7"u8)
                .AddHeader("x-h8"u8, "v8"u8)
                .AddHeader("x-h9"u8, "v9"u8)
                .AddHeader("x-h10"u8, "v10"u8)
                .WriteAsync(200, HelloBody, AppJsonBytes, keepAlive: true, ct));

        var response = await GetResponseAsync(server.Port);

        for (var i = 1; i <= 10; i++)
            Assert.Contains($"x-h{i}: v{i}", response, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fluent extension: duplicate name (Set-Cookie style) ──────────────────

    [Fact]
    public async Task FluentAddHeader_DuplicateName_BothWritten()
    {
        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res
                .AddHeader(HttpHeaderNames.SetCookie, "session=abc; HttpOnly"u8)
                .AddHeader(HttpHeaderNames.SetCookie, "theme=dark; Path=/"u8)
                .WriteAsync(200, HelloBody, AppJsonBytes, keepAlive: true, ct));

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("set-cookie: session=abc; HttpOnly", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set-cookie: theme=dark; Path=/",    response, StringComparison.OrdinalIgnoreCase);
    }

    // ── ServerOptions.DefaultResponseHeaders ─────────────────────────────────

    [Fact]
    public async Task DefaultResponseHeaders_SentOnEveryResponse()
    {
        var options = new ServerOptions
        {
            DefaultResponseHeaders =
            [
                new HttpHeader("x-content-type-options"u8.ToArray(), "nosniff"u8.ToArray()),
                new HttpHeader("x-frame-options"u8.ToArray(),        "DENY"u8.ToArray()),
            ]
        };

        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res.WriteAsync(200, HelloBody, AppJsonBytes, keepAlive: true, ct),
            options);

        // First request
        var r1 = await GetResponseAsync(server.Port);
        Assert.Contains("x-content-type-options: nosniff", r1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-frame-options: DENY",           r1, StringComparison.OrdinalIgnoreCase);

        // Second request — same connection-scoped writer, headers still present
        var r2 = await GetResponseAsync(server.Port);
        Assert.Contains("x-content-type-options: nosniff", r2, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-frame-options: DENY",           r2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultResponseHeaders_DoNotConflictWithPerRequestHeaders()
    {
        var options = new ServerOptions
        {
            DefaultResponseHeaders =
            [
                new HttpHeader("x-default"u8.ToArray(), "always"u8.ToArray()),
            ]
        };

        await using var server = await TestServer.StartAsync(
            (_, res, ct) => res
                .AddHeader("x-per-request"u8, "once"u8)
                .WriteAsync(200, HelloBody, AppJsonBytes, keepAlive: true, ct),
            options);

        var response = await GetResponseAsync(server.Port);

        Assert.Contains("x-default: always",     response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-per-request: once",   response, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> GetResponseAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await stream.WriteAsync("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);

        var buf = new byte[4096];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buf.AsMemory(total), timeout.Token)) > 0)
            total += read;

        return Encoding.ASCII.GetString(buf, 0, total);
    }

    // ── Embedded TestServer ───────────────────────────────────────────────────

    private sealed class TestServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task                    _runTask;

        private TestServer(int port, CancellationTokenSource cts, Task runTask)
        {
            Port    = port;
            _cts    = cts;
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
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)s.LocalEndPoint!).Port;
        }
    }
}
