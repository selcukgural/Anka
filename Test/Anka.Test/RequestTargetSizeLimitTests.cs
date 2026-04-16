using System.Net;
using System.Net.Sockets;
using System.Text;
using Anka.Exceptions;

namespace Anka.Test;

/// <summary>
/// Integration tests for 414 URI Too Long validation.
/// Verifies that MaxRequestTargetSize is enforced against the request-target.
/// </summary>
public class RequestTargetSizeLimitTests
{
    private static readonly byte[] OkBody         = "OK"u8.ToArray();
    private static readonly byte[] TextPlainBytes = "text/plain"u8.ToArray();

    [Fact]
    public void MaxRequestTargetSize_SetNegative_ThrowsAnkaOutOfRangeException()
    {
        var options = new ServerOptions();
        Assert.Throws<AnkaOutOfRangeException>(() => options.MaxRequestTargetSize = -1);
    }

    [Fact]
    public void MaxRequestTargetSize_DefaultIsNull()
    {
        var options = new ServerOptions();
        Assert.Null(options.MaxRequestTargetSize);
    }

    [Fact]
    public async Task Get_RequestTargetWithinLimit_Returns200()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: new ServerOptions { MaxRequestTargetSize = 32 });

        var response = await SendRawAsync(server.Port,
            "GET /ok HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Get_RequestTargetExactlyAtLimit_Returns200()
    {
        const string target = "/hello?x=1";

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: new ServerOptions { MaxRequestTargetSize = target.Length });

        var response = await SendRawAsync(server.Port,
            $"GET {target} HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 200 OK", response);
    }

    [Fact]
    public async Task Get_PathExceedsLimit_Returns414()
    {
        var target = "/" + new string('a', 40);

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: new ServerOptions { MaxRequestTargetSize = 16 });

        var response = await SendRawAsync(server.Port,
            $"GET {target} HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 414 URI Too Long", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public async Task Get_QueryExceedsLimit_Returns414()
    {
        const string target = "/search?q=hello";

        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct),
            options: new ServerOptions { MaxRequestTargetSize = 8 });

        var response = await SendRawAsync(server.Port,
            $"GET {target} HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 414 URI Too Long", response);
    }

    [Fact]
    public async Task InvalidRequestLine_Returns400()
    {
        await using var server = await TestServer.StartAsync(
            static (req, res, ct) => res.WriteAsync(200, OkBody, TextPlainBytes, cancellationToken: ct));

        var response = await SendRawAsync(server.Port,
            "BREW /coffee HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        Assert.Contains("HTTP/1.1 400 Bad Request", response);
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
