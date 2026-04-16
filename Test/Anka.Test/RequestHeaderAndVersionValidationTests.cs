using System.Net;
using System.Net.Sockets;
using System.Text;
using Anka.Exceptions;

namespace Anka.Test;

/// <summary>
/// Integration tests for 431 Request Header Fields Too Large and
/// 505 HTTP Version Not Supported behavior.
/// </summary>
public class RequestHeaderAndVersionValidationTests
{
    private static readonly byte[] OkBody         = "OK"u8.ToArray();
    private static readonly byte[] TextPlainBytes = "text/plain"u8.ToArray();

    [Fact]
    public void MaxRequestHeadersSize_SetNegative_ThrowsAnkaOutOfRangeException()
    {
        var options = new ServerOptions();
        Assert.Throws<AnkaOutOfRangeException>(() => options.MaxRequestHeadersSize = -1);
    }

    [Fact]
    public void MaxRequestHeadersSize_DefaultIsEightKilobytes()
    {
        var options = new ServerOptions();
        Assert.Equal(8 * 1024, options.MaxRequestHeadersSize);
    }

    [Fact]
    public async Task Get_TooManyHeaders_Returns431()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var request = new StringBuilder("GET / HTTP/1.1\r\n");
        for (var i = 0; i < 65; i++)
        {
            request.Append("X-Header-").Append(i.ToString("D2")).Append(": value\r\n");
        }
        request.Append("\r\n");

        var response = await SendRawAsync(server.Port, request.ToString());

        Assert.Contains("HTTP/1.1 431 Request Header Fields Too Large", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task Get_HeaderBytesExceedConfiguredLimit_Returns431()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: new ServerOptions { MaxRequestHeadersSize = 24 });

        const string request =
            "GET / HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "X-Long: abcdefghijklmnopqrstuvwxyz\r\n" +
            "\r\n";

        var response = await SendRawAsync(server.Port, request);

        Assert.Contains("HTTP/1.1 431 Request Header Fields Too Large", response);
    }

    [Fact]
    public async Task Get_UnsupportedHttpVersion_Returns505()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        const string request =
            "GET / HTTP/2.0\r\n" +
            "Host: example.com\r\n" +
            "Connection: close\r\n" +
            "\r\n";

        var response = await SendRawAsync(server.Port, request);

        Assert.Contains("HTTP/1.1 505 HTTP Version Not Supported", response);
        Assert.Contains("Connection: close", response);
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
