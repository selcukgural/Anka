using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anka.Test;

/// <summary>
/// Framing hardening: inputs that a lenient upstream proxy could interpret differently from Anka
/// (bare LF / CR / control bytes in fields and targets, non-digit Content-Length values) must be
/// rejected instead of being silently accepted.
/// </summary>
public class RequestFramingHardeningTests
{
    private static readonly byte[] OkBody         = "OK"u8.ToArray();
    private static readonly byte[] TextPlainBytes = "text/plain"u8.ToArray();

    // ── Header field values ──────────────────────────────────────────────────

    [Theory]
    [InlineData("X-Test: a\nTransfer-Encoding: chunked")] // bare LF hides a header from Anka
    [InlineData("X-Test: a\rb")]                          // bare CR
    [InlineData("X-Test: a\0b")]                          // NUL
    [InlineData("X-Test: a\u0001b")]                      // other C0 control
    [InlineData("X-Test: a\u007Fb")]                      // DEL
    public void TryParse_HeaderValueWithControlCharacter_ReturnsInvalid(string headerLine)
    {
        var result = Parse($"GET / HTTP/1.1\r\nHost: example.com\r\n{headerLine}\r\n\r\n", out var req);

        Assert.Equal(HttpParseResult.Invalid, result);
        req.Return();
    }

    [Fact]
    public void TryParse_HeaderValueWithInnerTabAndObsText_IsAccepted()
    {
        var bytes = Encoding.Latin1.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nX-Test: a\tbé\r\n\r\n");

        var result = Parse(bytes, out var req);

        Assert.Equal(HttpParseResult.Success, result);
        Assert.True(req.Headers.TryGetValue("x-test"u8, out var value));
        Assert.Equal(new byte[] { (byte)'a', (byte)'\t', (byte)'b', 0xE9 }, value.ToArray());
        req.Return();
    }

    [Fact]
    public void TryParse_HeaderValueSurroundedByTabs_IsTrimmed()
    {
        var result = Parse("GET / HTTP/1.1\r\nHost: example.com\r\nX-Test:\t value \t\r\n\r\n", out var req);

        Assert.Equal(HttpParseResult.Success, result);
        Assert.True(req.Headers.TryGetValue("x-test"u8, out var value));
        Assert.Equal("value", Encoding.ASCII.GetString(value));
        req.Return();
    }

    // ── Request target ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("/a\tb")]
    [InlineData("/a\u0001b")]
    [InlineData("/a\u007Fb")]
    [InlineData("/a\nb")]
    public void TryParse_RequestTargetWithControlCharacter_ReturnsInvalid(string target)
    {
        var result = Parse($"GET {target} HTTP/1.1\r\nHost: example.com\r\n\r\n", out var req);

        Assert.Equal(HttpParseResult.Invalid, result);
        req.Return();
    }

    // ── Content-Length ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("+5")]
    [InlineData("5 5")]
    [InlineData("5,5")]
    [InlineData("0x5")]
    [InlineData("5.0")]
    [InlineData(":5")]                    // stray second colon
    [InlineData("")]                      // empty value
    [InlineData("99999999999999999999")]  // overflows long
    public void TryParse_NonDigitContentLength_ReturnsInvalid(string value)
    {
        var result = Parse($"POST / HTTP/1.1\r\nHost: example.com\r\nContent-Length: {value}\r\n\r\nhello", out var req);

        Assert.Equal(HttpParseResult.Invalid, result);
        req.Return();
    }

    [Fact]
    public void TryParse_ContentLengthWithTabWhitespace_IsAccepted()
    {
        var result = Parse("POST / HTTP/1.1\r\nHost: example.com\r\nContent-Length:\t5\t\r\n\r\nhello", out var req);

        Assert.Equal(HttpParseResult.Success, result);
        Assert.Equal(5, req.ContentLength);
        Assert.Equal("hello", Encoding.ASCII.GetString(req.Body.Span));
        req.Return();
    }

    [Fact]
    public void TryParse_HeaderPrefixedWithContentLength_IsNotTreatedAsContentLength()
    {
        var result = Parse("GET / HTTP/1.1\r\nHost: example.com\r\nContent-Length-Foo: bar\r\n\r\n", out var req);

        Assert.Equal(HttpParseResult.Success, result);
        Assert.False(req.HasContentLength);
        req.Return();
    }

    [Fact]
    public void TryParse_ContentLengthWithSpaceBeforeColon_ReturnsInvalid()
    {
        var result = Parse("POST / HTTP/1.1\r\nHost: example.com\r\nContent-Length : 5\r\n\r\nhello", out var req);

        Assert.Equal(HttpParseResult.Invalid, result);
        req.Return();
    }

    // ── Chunked trailers ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("X-Trailer: a\nb\r\n\r\n")]  // bare LF in value
    [InlineData("X-Trailer: a\0b\r\n\r\n")]  // NUL in value
    [InlineData("X-Trailer : a\r\n\r\n")]    // whitespace before colon
    [InlineData("X Trailer: a\r\n\r\n")]     // invalid token
    [InlineData(": a\r\n\r\n")]              // empty name
    public void TryConsumeTrailers_InvalidField_ReturnsInvalid(string trailerBlock)
    {
        var trailers = new HttpHeaders();
        var buffer = new byte[256];
        trailers.InitBuffer(buffer, 0);

        var result = ChunkedBodyParser.TryConsumeTrailers(Encoding.ASCII.GetBytes(trailerBlock), ref trailers, out _);

        Assert.Equal(ChunkedBodyParseResult.Invalid, result);
    }

    [Fact]
    public void TryConsumeTrailers_ValidField_IsStoredTrimmed()
    {
        var trailers = new HttpHeaders();
        var buffer = new byte[256];
        trailers.InitBuffer(buffer, 0);

        var result = ChunkedBodyParser.TryConsumeTrailers("X-Checksum:\tabc \r\n\r\n"u8, ref trailers, out var consumed);

        Assert.Equal(ChunkedBodyParseResult.Success, result);
        Assert.Equal(20, consumed);
        Assert.True(trailers.TryGetValue("x-checksum"u8, out var value));
        Assert.Equal("abc", Encoding.ASCII.GetString(value));
    }

    // ── End to end ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Server_BareLfHidingTransferEncoding_Returns400AndCloses()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "POST / HTTP/1.1\r\nHost: example.com\r\nContent-Length: 5\r\nX-Test: a\nTransfer-Encoding: chunked\r\n\r\nhello");

        Assert.StartsWith("HTTP/1.1 400 Bad Request", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task Server_SignedContentLength_Returns400AndCloses()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "POST / HTTP/1.1\r\nHost: example.com\r\nContent-Length: +5\r\n\r\nhello");

        Assert.StartsWith("HTTP/1.1 400 Bad Request", response);
        Assert.Contains("Connection: close", response);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HttpParseResult Parse(string raw, out HttpRequest req) => Parse(Encoding.ASCII.GetBytes(raw), out req);

    private static HttpParseResult Parse(byte[] bytes, out HttpRequest req)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));
        req = new HttpRequest();
        return HttpParser.TryParse(ref reader, req);
    }

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
