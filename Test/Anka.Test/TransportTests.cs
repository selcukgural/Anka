using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anka.Test;

public class TransportTests
{
    private static readonly byte[] OkBody         = "OK"u8.ToArray();
    private static readonly byte[] TextPlainBytes = "text/plain"u8.ToArray();

    [Fact]
    public async Task FragmentedGetRequest_AcrossMultipleWrites_IsHandled()
    {
        await using var server = await TestServer.StartAsync(
            static (request, response, cancellationToken) =>
                response.WriteAsync(200, OkBody, TextPlainBytes, keepAlive: request.IsKeepAlive, cancellationToken: cancellationToken));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = new HttpResponseReader(stream);

        await stream.WriteAsync("GET / HTTP/1.1\r\nHo"u8.ToArray(), timeout.Token);
        await Task.Delay(50, timeout.Token);
        await stream.WriteAsync("st: example.com\r\n\r\n"u8.ToArray(), timeout.Token);

        var response = await reader.ReadResponseAsync(timeout.Token);

        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("Content-Length: 2", response);
        Assert.EndsWith("OK", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepAlivePipeline_TwoRequestsInSingleBuffer_ReturnsTwoResponses()
    {
        await using var server = await TestServer.StartAsync(
            static (request, response, cancellationToken) =>
                response.WriteAsync(200, OkBody, TextPlainBytes, keepAlive: request.IsKeepAlive, cancellationToken: cancellationToken));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = new HttpResponseReader(stream);

        const string pipelinedRequests =
            "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n" +
            "GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(pipelinedRequests), timeout.Token);

        var firstResponse  = await reader.ReadResponseAsync(timeout.Token);
        var secondResponse = await reader.ReadResponseAsync(timeout.Token);

        Assert.Contains("HTTP/1.1 200 OK", firstResponse);
        Assert.Contains("Connection: keep-alive", firstResponse);
        Assert.EndsWith("OK", firstResponse, StringComparison.Ordinal);

        Assert.Contains("HTTP/1.1 200 OK", secondResponse);
        Assert.Contains("Connection: close", secondResponse);
        Assert.EndsWith("OK", secondResponse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FragmentedPostBody_AcrossReceives_IsEchoedBack()
    {
        await using var server = await TestServer.StartAsync(
            static (request, response, cancellationToken) =>
                response.WriteAsync(200, request.Body, TextPlainBytes, keepAlive: request.IsKeepAlive, cancellationToken: cancellationToken));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = new HttpResponseReader(stream);

        const string payload = "hello world";
        var requestHead =
            "POST /echo HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Connection: close\r\n" +
            $"Content-Length: {payload.Length}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(requestHead + payload[..5]), timeout.Token);
        await Task.Delay(50, timeout.Token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(payload[5..]), timeout.Token);

        var response = await reader.ReadResponseAsync(timeout.Token);

        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("Connection: close", response);
        Assert.EndsWith(payload, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkedTransferEncoding_IsDecodedAndEchoedBack()
    {
        await using var server = await TestServer.StartAsync(
            static (request, response, cancellationToken) =>
                response.WriteAsync(200, request.Body, TextPlainBytes, keepAlive: false, cancellationToken: cancellationToken));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = new HttpResponseReader(stream);

        const string chunkedRequest =
            "POST /echo HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n" +
            "5\r\nhello\r\n0\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(chunkedRequest), timeout.Token);

        var response = await reader.ReadResponseAsync(timeout.Token);

        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("Content-Length: 5", response);
        Assert.EndsWith("hello", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expect100Continue_SendsInterimResponseBeforeReadingBody()
    {
        await using var server = await TestServer.StartAsync(
            static (request, response, cancellationToken) =>
                response.WriteAsync(200, request.Body, TextPlainBytes, keepAlive: false, cancellationToken: cancellationToken));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = new HttpResponseReader(stream);

        const string headers =
            "POST /echo HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Expect: 100-continue\r\n" +
            "Content-Length: 5\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), timeout.Token);

        var interim = await reader.ReadHeadersOnlyAsync(timeout.Token);
        Assert.Contains("HTTP/1.1 100 Continue", interim);

        await stream.WriteAsync("hello"u8.ToArray(), timeout.Token);

        var response = await reader.ReadResponseAsync(timeout.Token);
        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.EndsWith("hello", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadTimeout_ClosesStalledConnection()
    {
        await using var server = await TestServer.StartAsync(
            static (_, response, cancellationToken) =>
                response.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: cancellationToken),
            options: new ServerOptions { ReadTimeout = TimeSpan.FromMilliseconds(150) });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await stream.WriteAsync("GET / HTTP/1.1\r\nHost: example.com\r\n"u8.ToArray(), timeout.Token);
        await Task.Delay(400, timeout.Token);

        var buffer = new byte[1];
        var readException = await Record.ExceptionAsync(async () => await stream.ReadAsync(buffer, timeout.Token));
        if (readException is null)
        {
            return;
        }

        Assert.True(readException is IOException or SocketException || readException.InnerException is SocketException);
    }

    [Fact]
    public async Task ReadTimeout_AllowsTimelyFragmentedRequest()
    {
        await using var server = await TestServer.StartAsync(
            static (_, response, cancellationToken) =>
                response.WriteAsync(200, OkBody, TextPlainBytes, keepAlive: false, cancellationToken: cancellationToken),
            options: new ServerOptions { ReadTimeout = TimeSpan.FromSeconds(1) });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = new HttpResponseReader(stream);

        await stream.WriteAsync("GET / HTTP/1.1\r\nHo"u8.ToArray(), timeout.Token);
        await Task.Delay(100, timeout.Token);
        await stream.WriteAsync("st: example.com\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);

        var response = await reader.ReadResponseAsync(timeout.Token);

        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task HandlerException_ReturnsInternalServerError()
    {
        await using var server = await TestServer.StartAsync(
            static (_, _, _) => throw new InvalidOperationException("boom"));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = new HttpResponseReader(stream);

        await stream.WriteAsync("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);

        var response = await reader.ReadResponseAsync(timeout.Token);

        Assert.Contains("HTTP/1.1 500 Internal Server Error", response);
    }

    [Fact]
    public async Task OversizedRequestBody_LargerThanReceiveBuffer_IsHandled()
    {
        await using var server = await TestServer.StartAsync(
            static (_, response, cancellationToken) =>
                response.WriteAsync(200, OkBody, TextPlainBytes, keepAlive: false, cancellationToken: cancellationToken));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = new HttpResponseReader(stream);

        var payload = new string('x', 70_000);
        var request =
            "POST /upload HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Connection: close\r\n" +
            $"Content-Length: {payload.Length}\r\n\r\n" +
            payload;

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);

        var response = await reader.ReadResponseAsync(timeout.Token);

        Assert.Contains("HTTP/1.1 200 OK", response);
        Assert.Contains("Content-Length: 2", response);
    }

    [Fact]
    public async Task HeadRequest_SendsHeadersWithoutBody()
    {
        await using var server = await TestServer.StartAsync(
            static (request, response, cancellationToken) =>
                response.WriteAsync(200, OkBody, TextPlainBytes, keepAlive: request.IsKeepAlive, cancellationToken: cancellationToken));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = new HttpResponseReader(stream);

        const string request =
            "HEAD / HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);

        var responseHeaders = await reader.ReadHeadersOnlyAsync(timeout.Token);
        await Task.Delay(100, timeout.Token);

        Assert.Contains("HTTP/1.1 200 OK", responseHeaders);
        Assert.Contains("Content-Length: 2", responseHeaders);
        Assert.DoesNotContain("OK", responseHeaders.Split("\r\n\r\n")[^1], StringComparison.Ordinal);
        Assert.False(reader.HasBufferedData);
        Assert.False(stream.DataAvailable);
    }

    [Fact]
    public async Task NotModifiedResponse_SendsHeadersWithoutBody()
    {
        await using var server = await TestServer.StartAsync(
            static (_, response, cancellationToken) =>
                response.WriteAsync(304, OkBody, TextPlainBytes, keepAlive: false, cancellationToken: cancellationToken));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = new HttpResponseReader(stream);

        const string request =
            "GET / HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);

        var responseHeaders = await reader.ReadHeadersOnlyAsync(timeout.Token);
        await Task.Delay(100, timeout.Token);

        Assert.Contains("HTTP/1.1 304 Not Modified", responseHeaders);
        Assert.Contains("Content-Length: 2", responseHeaders);
        Assert.False(reader.HasBufferedData);
        Assert.False(stream.DataAvailable);
    }

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
            var port  = GetFreePort();
            var cts   = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
    }

    private sealed class HttpResponseReader(NetworkStream stream)
    {
        private readonly List<byte> _buffer = [];
        public bool HasBufferedData => _buffer.Count > 0;

        public async Task<string> ReadResponseAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (TryExtractResponse(out var responseBytes))
                {
                    return Encoding.ASCII.GetString(responseBytes);
                }

                var temp = new byte[1024];
                var read = await stream.ReadAsync(temp, cancellationToken);
                if (read == 0)
                {
                    throw new InvalidOperationException("Socket closed before a full HTTP response was received.");
                }

                _buffer.AddRange(temp.AsSpan(0, read).ToArray());
            }
        }

        public async Task<string> ReadHeadersOnlyAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var headerEnd = FindHeaderEnd(_buffer);
                if (headerEnd >= 0)
                {
                    var headerBytes = _buffer.GetRange(0, headerEnd).ToArray();
                    _buffer.RemoveRange(0, headerEnd);
                    return Encoding.ASCII.GetString(headerBytes);
                }

                var temp = new byte[1024];
                var read = await stream.ReadAsync(temp, cancellationToken);
                if (read == 0)
                {
                    throw new InvalidOperationException("Socket closed before HTTP response headers were received.");
                }

                _buffer.AddRange(temp.AsSpan(0, read).ToArray());
            }
        }

        private bool TryExtractResponse(out byte[] responseBytes)
        {
            responseBytes = [];

            var headerEnd = FindHeaderEnd(_buffer);
            if (headerEnd < 0)
            {
                return false;
            }

            var headerText = Encoding.ASCII.GetString(_buffer.GetRange(0, headerEnd).ToArray());
            var contentLength = ParseContentLength(headerText);
            var responseLength = headerEnd + contentLength;

            if (_buffer.Count < responseLength)
            {
                return false;
            }

            responseBytes = _buffer.GetRange(0, responseLength).ToArray();
            _buffer.RemoveRange(0, responseLength);
            return true;
        }

        private static int ParseContentLength(string headers)
        {
            foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line["Content-Length:".Length..].Trim();
                return int.TryParse(value, out var parsed) ? parsed : 0;
            }

            return 0;
        }

        private static int FindHeaderEnd(List<byte> bytes)
        {
            for (var i = 0; i <= bytes.Count - 4; i++)
            {
                if (bytes[i] == (byte)'\r' &&
                    bytes[i + 1] == (byte)'\n' &&
                    bytes[i + 2] == (byte)'\r' &&
                    bytes[i + 3] == (byte)'\n')
                {
                    return i + 4;
                }
            }

            return -1;
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
