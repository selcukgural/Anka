using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anka.Test;

public class RfcComplianceTests
{
    private static HttpRequest CreateRequest() => new();
    private static HttpParseResult TryParseResult(string raw, HttpRequest request)
    {
        var bytes = Encoding.ASCII.GetBytes(raw);
        var seq = new ReadOnlySequence<byte>(bytes);
        var reader = new SequenceReader<byte>(seq);
        request.ResetForReuse();
        return HttpParser.TryParse(ref reader, request);
    }

    [Fact]
    public void TryParse_SkipLeadingCRLF_ReturnsSuccess()
    {
        const string raw = "\r\n\r\nGET / HTTP/1.1\r\nHost: example.com\r\n\r\n";
        var req = CreateRequest();
        var result = TryParseResult(raw, req);
        Assert.Equal(HttpParseResult.Success, result);
        Assert.Equal(HttpMethod.Get, req.Method);
        req.Return();
    }

    [Fact]
    public void TryParse_WhitespaceBeforeHeaderColon_ReturnsInvalid()
    {
        const string raw = "GET / HTTP/1.1\r\nHost : example.com\r\n\r\n";
        var req = CreateRequest();
        var result = TryParseResult(raw, req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Return();
    }

    [Fact]
    public void TryParse_ObsFoldHeader_ReturnsInvalid()
    {
        const string raw = "GET / HTTP/1.1\r\nHost: example.com\r\n X-Fold: value\r\n\r\n";
        var req = CreateRequest();
        var result = TryParseResult(raw, req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Return();
    }

    [Fact]
    public void TryParse_WhitespaceBeforeContentLengthColon_ReturnsInvalid()
    {
        const string raw = "POST / HTTP/1.1\r\nHost: example.com\r\nContent-Length : 10\r\n\r\n1234567890";
        var req = CreateRequest();
        var result = TryParseResult(raw, req);
        Assert.Equal(HttpParseResult.Invalid, result);
        req.Return();
    }

    [Fact]
    public async Task Post_MissingLengthHeaders_Returns411LengthRequired()
    {
        await using var server = await TestServer.StartAsync(async (req, res, ct) =>
        {
            await res.WriteAsync(200, "OK"u8.ToArray(), "text/plain"u8.ToArray(), true, ct);
        });

        var request = "POST / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        var response = await SendRawAsync(server.Port, request);

        Assert.Contains("411 Length Required", response);
    }

    [Fact]
    public async Task Request_BothContentLengthAndChunked_Returns400BadRequest()
    {
        await using var server = await TestServer.StartAsync(async (req, res, ct) =>
        {
            await res.WriteAsync(200, "OK"u8.ToArray(), "text/plain"u8.ToArray(), true, ct);
        });

        var request =
            "POST / HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n5\r\nhello\r\n0\r\n\r\n";
        var response = await SendRawAsync(server.Port, request);

        Assert.Contains("400 Bad Request", response);
    }

    [Fact]
    public async Task Request_Trailers_AreParsed()
    {
        ReadOnlyMemory<byte> trailerValueMemory = default;
        await using var server = await TestServer.StartAsync((req, res, ct) =>
        {
            if (req.Trailers.TryGetValue("x-trailer"u8, out var val))
            {
                trailerValueMemory = val.ToArray();
            }

            return res.WriteAsync(200, "OK"u8.ToArray(), "text/plain"u8.ToArray(), true, ct);
        });

        var request =
            "POST / HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n5\r\nhello\r\n0\r\n" +
            "X-Trailer: test-value\r\n\r\n";
        var response = await SendRawAsync(server.Port, request);

        Assert.Contains("200 OK", response);
        Assert.False(trailerValueMemory.IsEmpty);
        Assert.Equal("test-value", Encoding.ASCII.GetString(trailerValueMemory.Span));
    }

    [Fact]
    public async Task Response_Trailers_AreSent()
    {
        await using var server = await TestServer.StartAsync(async (req, res, ct) =>
        {
            var stream = res.GetStream(ct);
            await stream.WriteAsync("Hello"u8.ToArray(), ct);
            ((HttpResponseStream)stream).AddTrailer(new HttpHeader("X-Response-Trailer"u8.ToArray(),
                "trailer-ok"u8.ToArray()));
            await stream.DisposeAsync();
        });

        var request = "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        var response = await SendRawAsync(server.Port, request);

        Assert.Contains("transfer-encoding: chunked", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0\r\n", response); // End of chunks
        Assert.Contains("x-response-trailer: trailer-ok\r\n", response, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> SendRawAsync(int port, string rawRequest)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        stream.ReadTimeout = 2000;
        var bytes = Encoding.ASCII.GetBytes(rawRequest);
        await stream.WriteAsync(bytes, 0, bytes.Length);

        var buffer = new byte[8192];
        var totalRead = 0;
        using var cts = new CancellationTokenSource(2000);
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0) break;
            totalRead += read;
        }

        return Encoding.ASCII.GetString(buffer, 0, totalRead);
    }

    private class TestServer : IAsyncDisposable
    {
        private readonly Server _server;
        private readonly CancellationTokenSource _cts;
        private readonly Task _runTask;
        public int Port { get; }

        private TestServer(Server server, int port, CancellationTokenSource cts, Task runTask)
        {
            _server = server;
            Port = port;
            _cts = cts;
            _runTask = runTask;
        }

        public static async Task<TestServer> StartAsync(RequestHandler handler, ServerOptions? options = null)
        {
            var port = GetFreePort();
            var cts = new CancellationTokenSource();
            var server = new Server(handler, port, "127.0.0.1", options);
            var runTask = server.StartAsync(cts.Token);

            // Wait a bit for server to start
            await Task.Delay(100, cts.Token);

            return new TestServer(server, port, cts, runTask);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Task.WhenAny(_runTask, Task.Delay(1000));
            _cts.Dispose();
        }

        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}