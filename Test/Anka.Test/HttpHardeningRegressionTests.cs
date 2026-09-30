using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Anka.Internal;

namespace Anka.Test;

/// <summary>
/// Regression tests for protocol and response-lifecycle bugs: Connection header parsing, malformed
/// header lines, chunk extensions, Range parsing, the one-response-per-request rule, keep-alive
/// agreement between handler and server, streaming to HTTP/1.0 clients, response header
/// validation and multipart delimiters.
/// </summary>
public class HttpHardeningRegressionTests
{
    private static readonly byte[] OkBody         = "OK"u8.ToArray();
    private static readonly byte[] TextPlainBytes = "text/plain"u8.ToArray();

    // ── Connection header ────────────────────────────────────────────────────

    [Theory]
    [InlineData("HTTP/1.1", "Close", false)]
    [InlineData("HTTP/1.1", "CLOSE", false)]
    [InlineData("HTTP/1.1", "close, TE", false)]
    [InlineData("HTTP/1.1", "TE,close", false)]
    [InlineData("HTTP/1.1", "keep-alive, close", false)]
    [InlineData("HTTP/1.1", "upgrade", true)]
    [InlineData("HTTP/1.0", "Keep-Alive", true)]
    [InlineData("HTTP/1.0", "TE, keep-alive", true)]
    [InlineData("HTTP/1.0", "TE", false)]
    public void TryParse_ConnectionTokens_AreCaseInsensitiveAndListAware(string version, string connection, bool expected)
    {
        var result = Parse($"GET / {version}\r\nHost: example.com\r\nConnection: {connection}\r\n\r\n", out var req);

        Assert.Equal(HttpParseResult.Success, result);
        Assert.Equal(expected, req.IsKeepAlive);
        req.Return();
    }

    [Fact]
    public void TryParse_CloseInSecondConnectionHeader_DisablesKeepAlive()
    {
        var result = Parse("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: TE\r\nConnection: close\r\n\r\n", out var req);

        Assert.Equal(HttpParseResult.Success, result);
        Assert.False(req.IsKeepAlive);
        req.Return();
    }

    [Fact]
    public async Task Server_ConnectionCloseInAnyCase_ClosesAfterResponse()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, req.IsKeepAlive, ct));

        var response = await SendAndReadToEndAsync(server.Port,
            "GET / HTTP/1.1\r\nHost: example.com\r\nConnection: Close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Connection: close", response);
    }

    // ── Malformed header lines ───────────────────────────────────────────────

    [Theory]
    [InlineData("ThisHasNoColon")]
    [InlineData(": empty-name")]
    public void TryParse_HeaderLineWithoutName_ReturnsInvalid(string headerLine)
    {
        var result = Parse($"GET / HTTP/1.1\r\nHost: example.com\r\n{headerLine}\r\n\r\n", out var req);

        Assert.Equal(HttpParseResult.Invalid, result);
        req.Return();
    }

    // ── Chunk extensions ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("3;a\nb\r\n")]   // bare LF inside the extension
    [InlineData("3;a\0b\r\n")]   // NUL
    [InlineData("3;a\rb\r\n")]   // bare CR
    public void TryReadChunkSize_ControlByteInExtension_ReturnsInvalid(string line)
    {
        var result = ChunkedBodyParser.TryReadChunkSize(Encoding.ASCII.GetBytes(line), out _, out _);

        Assert.Equal(ChunkedBodyParseResult.Invalid, result);
    }

    [Fact]
    public void TryReadChunkSize_WellFormedExtension_IsIgnored()
    {
        var result = ChunkedBodyParser.TryReadChunkSize("1a;name=\"va lue\"\r\n"u8, out var size, out var consumed);

        Assert.Equal(ChunkedBodyParseResult.Success, result);
        Assert.Equal(0x1a, size);
        Assert.Equal(18, consumed);
    }

    [Fact]
    public async Task Server_ChunkExtensionWithBareLf_Returns400()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, req.Body, TextPlainBytes, req.IsKeepAlive, ct));

        var response = await SendAndReadToEndAsync(server.Port,
            "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n3;a\nb\r\nabc\r\n0\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 400 Bad Request", response);
    }

    // ── Range parsing ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("bytes=0-5abc")]
    [InlineData("bytes=--5")]
    [InlineData("bytes=1--5")]
    [InlineData("bytes=+1-2")]
    [InlineData("bytes=0-1,5-6")]
    [InlineData("bytes=-")]
    public void TryParseRange_Malformed_ReturnsFalse(string value)
    {
        Assert.False(HttpParser.TryParseRange(Encoding.ASCII.GetBytes(value), out var start, out var end));
        Assert.Equal(-1, start);
        Assert.Equal(-1, end);
    }

    [Theory]
    [InlineData("bytes=2-5", 2, 5)]
    [InlineData("bytes=5-", 5, -1)]
    [InlineData("bytes=-3", -1, 3)]
    [InlineData("bytes= 2 - 5 ", 2, 5)]
    public void TryParseRange_SingleRange_Parses(string value, long expectedStart, long expectedEnd)
    {
        Assert.True(HttpParser.TryParseRange(Encoding.ASCII.GetBytes(value), out var start, out var end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    // ── One response per request ─────────────────────────────────────────────

    [Fact]
    public async Task Server_HandlerWritesNothing_Sends200WithEmptyBody()
    {
        await using var server = await TestServer.StartAsync(static (_, _, _) => ValueTask.CompletedTask);

        var response = await SendAndReadToEndAsync(server.Port,
            "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Content-Length: 0", response);
    }

    [Fact]
    public async Task Server_HandlerWritesTwice_SecondWriteThrowsAndOnlyOneResponseIsSent()
    {
        var secondWrite = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestServer.StartAsync(async (req, res, ct) =>
        {
            await res.WriteAsync(200, OkBody, TextPlainBytes, req.IsKeepAlive, ct);
            try
            {
                await res.WriteAsync(404, keepAlive: req.IsKeepAlive, cancellationToken: ct);
                secondWrite.TrySetResult(null);
            }
            catch (Exception ex)
            {
                secondWrite.TrySetResult(ex);
            }
        });

        var response = await SendAndReadToEndAsync(server.Port,
            "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.IsType<InvalidOperationException>(await secondWrite.Task);
        Assert.Equal(1, CountOccurrences(response, "HTTP/1.1 "));
        Assert.DoesNotContain("404", response);
    }

    [Fact]
    public async Task Server_HandlerThrowsAfterWriting_DoesNotAppend500()
    {
        await using var server = await TestServer.StartAsync(async (req, res, ct) =>
        {
            await res.WriteAsync(200, OkBody, TextPlainBytes, req.IsKeepAlive, ct);
            throw new InvalidOperationException("boom");
        });

        var response = await SendAndReadToEndAsync(server.Port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Equal(1, CountOccurrences(response, "HTTP/1.1 "));
        Assert.DoesNotContain("500", response);
    }

    [Fact]
    public async Task Server_HandlerThrowsBeforeWriting_Sends500WithConnectionClose()
    {
        await using var server = await TestServer.StartAsync(static (_, _, _) => throw new InvalidOperationException("boom"));

        var response = await SendAndReadToEndAsync(server.Port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 500 Internal Server Error", response);
        Assert.Contains("Connection: close", response);
    }

    // ── Keep-alive agreement ─────────────────────────────────────────────────

    [Fact]
    public async Task Server_HandlerChoosesClose_ServerClosesConnection()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, keepAlive: false, cancellationToken: ct));

        // The request asks for keep-alive; the handler overrides it. Reading to end only completes if the server closes.
        var response = await SendAndReadToEndAsync(server.Port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task Server_RequestAsksClose_HandlerKeepAliveTrueStillAdvertisesClose()
    {
        await using var server = await TestServer.StartAsync(
            static (_, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, keepAlive: true, cancellationToken: ct));

        var response = await SendAndReadToEndAsync(server.Port,
            "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.Contains("Connection: close", response);
        Assert.DoesNotContain("keep-alive", response);
    }

    // ── Streaming ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Server_TrailersDoNotLeakIntoNextKeepAliveResponse()
    {
        await using var server = await TestServer.StartAsync(async (req, res, ct) =>
        {
            await using var stream = (HttpResponseStream)res.GetStream(ct);
            if (req.PathEquals("/with-trailer"u8))
            {
                stream.AddTrailer(new HttpHeader("x-trailer"u8.ToArray(), "1"u8.ToArray()));
            }

            await stream.WriteAsync("body"u8.ToArray(), ct);
        });

        var response = await SendAndReadToEndAsync(server.Port,
            "GET /with-trailer HTTP/1.1\r\nHost: x\r\n\r\n" +
            "GET /plain HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.Equal(2, CountOccurrences(response, "HTTP/1.1 200 OK"));
        Assert.Equal(1, CountOccurrences(response, "x-trailer: 1"));
    }

    [Fact]
    public async Task Server_StreamToHttp10Client_UsesCloseDelimitedBody()
    {
        await using var server = await TestServer.StartAsync(async (_, res, ct) =>
        {
            await using var stream = res.GetStream(ct);
            await stream.WriteAsync("hello "u8.ToArray(), ct);
            await stream.WriteAsync("world"u8.ToArray(), ct);
        });

        var response = await SendAndReadToEndAsync(server.Port, "GET / HTTP/1.0\r\nConnection: keep-alive\r\n\r\n");

        Assert.StartsWith("HTTP/1.0 200 OK", response);
        Assert.DoesNotContain("Transfer-Encoding", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content-Length", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection: close", response);
        Assert.EndsWith("\r\n\r\nhello world", response);
    }

    [Fact]
    public async Task Server_StreamNotDisposed_ServerSendsTerminatingChunk()
    {
        await using var server = await TestServer.StartAsync(async (_, res, ct) =>
        {
            var stream = res.GetStream(ct);
            await stream.WriteAsync("partial"u8.ToArray(), ct);
            // Intentionally not disposed.
        });

        var response = await SendAndReadToEndAsync(server.Port,
            "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.Contains("transfer-encoding: chunked", response, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("7\r\npartial\r\n0\r\n\r\n", response);
    }

    [Fact]
    public async Task Server_HeadWithChunkedWriterApi_SendsNoBody()
    {
        await using var server = await TestServer.StartAsync(async (req, res, ct) =>
        {
            await res.StartChunkedResponseAsync(200, TextPlainBytes, req.IsKeepAlive, cancellationToken: ct);
            await res.WriteChunkAsync("body"u8.ToArray(), ct);
            await res.FinishChunkedResponseAsync(cancellationToken: ct);
        });

        var response = await SendAndReadToEndAsync(server.Port,
            "HEAD / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.Contains("transfer-encoding: chunked", response, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("\r\n\r\n", response);
        Assert.DoesNotContain("body", response);
    }

    [Fact]
    public async Task Server_WriteChunkWithoutStart_Throws()
    {
        var thrown = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestServer.StartAsync(async (req, res, ct) =>
        {
            try
            {
                await res.WriteChunkAsync("x"u8.ToArray(), ct);
                thrown.TrySetResult(null);
            }
            catch (Exception ex)
            {
                thrown.TrySetResult(ex);
            }

            await res.WriteAsync(200, OkBody, TextPlainBytes, req.IsKeepAlive, ct);
        });

        await SendAndReadToEndAsync(server.Port, "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.IsType<InvalidOperationException>(await thrown.Task);
    }

    // ── Response header validation ───────────────────────────────────────────

    [Theory]
    [InlineData("x-test", "a\r\nSet-Cookie: evil=1")]
    [InlineData("x-test", "a\nb")]
    [InlineData("x-test", "a\0b")]
    [InlineData("x test", "value")]
    [InlineData("x-test\r\nevil", "value")]
    [InlineData("", "value")]
    public void HttpHeader_InvalidNameOrValue_Throws(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => new HttpHeader(Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value)));
        Assert.Throws<ArgumentException>(() => new HttpHeader(name, value));
    }

    [Fact]
    public void HttpHeader_ValueWithTabAndObsText_IsAccepted()
    {
        var header = new HttpHeader("x-test"u8.ToArray(), new byte[] { (byte)'a', (byte)'\t', 0xE9 });

        Assert.Equal(3, header.Value.Length);
    }

    [Fact]
    public async Task Server_ContentTypeWithCrLf_WriteThrowsAndNothingIsInjected()
    {
        var thrown = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestServer.StartAsync(async (req, res, ct) =>
        {
            try
            {
                await res.WriteAsync(200, OkBody, "text/plain\r\nSet-Cookie: evil=1"u8.ToArray(), req.IsKeepAlive, ct);
                thrown.TrySetResult(null);
            }
            catch (Exception ex)
            {
                thrown.TrySetResult(ex);
                throw;
            }
        });

        var response = await SendAndReadToEndAsync(server.Port, "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.IsType<ArgumentException>(await thrown.Task);
        Assert.StartsWith("HTTP/1.1 500", response);
        Assert.DoesNotContain("evil", response);
    }

    [Fact]
    public async Task Server_LongContentTypeWithNearlyFullInlineBody_IsWrittenCorrectly()
    {
        // Content-Type length used to be left out of the buffer-size estimate, so a long value plus a
        // body at the inline threshold overran the connection's header buffer (rented as 8 KB).
        var contentType = Encoding.ASCII.GetBytes("text/plain; x=" + new string('a', 4000));
        var body = Enumerable.Repeat((byte)'b', 4096).ToArray();

        await using var server = await TestServer.StartAsync(
            (req, res, ct) => res.WriteAsync(200, body, contentType, req.IsKeepAlive, ct));

        var response = await SendAndReadToEndAsync(server.Port, "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("Content-Length: 4096", response);
        Assert.EndsWith(new string('b', 4096), response);
    }

    // ── Multipart ────────────────────────────────────────────────────────────

    [Fact]
    public void Multipart_EmptyPartContent_DoesNotThrow()
    {
        var parser = Multipart(
            "--b\r\n" +
            "Content-Disposition: form-data; name=\"empty\"\r\n" +
            "\r\n" +
            "\r\n" +
            "--b--\r\n");

        Assert.True(parser.TryReadNextPart(out var part));
        Assert.True(part.Content.IsEmpty);
        Assert.False(parser.TryReadNextPart(out _));
    }

    [Fact]
    public void Multipart_BoundaryDirectlyAfterHeaders_IsRejectedWithoutThrowing()
    {
        // No CRLF between the header block and the next boundary: used to slice at a negative length.
        var parser = Multipart(
            "--b\r\n" +
            "Content-Disposition: form-data; name=\"x\"\r\n" +
            "\r\n" +
            "--b--\r\n");

        Assert.False(parser.TryReadNextPart(out _));
    }

    [Fact]
    public void Multipart_BoundaryTextInsideContent_IsNotADelimiter()
    {
        var parser = Multipart(
            "--b\r\n" +
            "Content-Disposition: form-data; name=\"x\"\r\n" +
            "\r\n" +
            "line --b not a delimiter\r\n" +
            "--b--\r\n");

        Assert.True(parser.TryReadNextPart(out var part));
        Assert.Equal("line --b not a delimiter", Encoding.ASCII.GetString(part.Content));
    }

    [Fact]
    public void Multipart_PartWithoutHeaders_IsParsed()
    {
        var parser = Multipart("--b\r\n\r\nraw\r\n--b--\r\n");

        Assert.True(parser.TryReadNextPart(out var part));
        Assert.True(part.Headers.IsEmpty);
        Assert.Equal("raw", Encoding.ASCII.GetString(part.Content));
    }

    [Theory]
    [InlineData("form-data; filename=\"a.txt\"; name=\"file\"", "file", "a.txt")]
    [InlineData("form-data; name=\"file\"; filename=\"a;b.txt\"", "file", "a;b.txt")]
    [InlineData("form-data; NAME=\"file\"; FileName=\"a.txt\"", "file", "a.txt")]
    [InlineData("form-data; name=file", "file", "")]
    [InlineData("form-data; filename=\"only.txt\"", "", "only.txt")]
    public void Multipart_ContentDisposition_MatchesParametersByName(string disposition, string expectedName, string expectedFileName)
    {
        var parser = Multipart($"--b\r\nContent-Disposition: {disposition}\r\n\r\ndata\r\n--b--\r\n");

        Assert.True(parser.TryReadNextPart(out var part));
        Assert.True(part.TryGetContentDisposition(out var name, out var fileName));
        Assert.Equal(expectedName, Encoding.ASCII.GetString(name));
        Assert.Equal(expectedFileName, Encoding.ASCII.GetString(fileName));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MultipartParser Multipart(string body)
        => new(new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(body)), "b"u8);

    private static HttpParseResult Parse(string raw, out HttpRequest req)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(raw)));
        req = new HttpRequest();
        return HttpParser.TryParse(ref reader, req);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Sends <paramref name="rawRequest"/> and reads until the server closes the connection.
    /// Fails (times out) if the server keeps the connection open.
    /// </summary>
    private static async Task<string> SendAndReadToEndAsync(int port, string rawRequest)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await stream.WriteAsync(Encoding.Latin1.GetBytes(rawRequest), timeout.Token);

        using var received = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            received.Write(buffer, 0, read);
        }

        return Encoding.Latin1.GetString(received.ToArray());
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
