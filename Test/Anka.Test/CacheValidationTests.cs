using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anka.Test;

public class CacheValidationTests
{
    private static async Task<string> SendRawAsync(int port, string rawRequest)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(rawRequest);
        await stream.WriteAsync(bytes, 0, bytes.Length);

        var buffer = new byte[8192];
        var totalRead = 0;
        using var cts = new CancellationTokenSource(2000);
        while (true)
        {
            int read;
            try { read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, cts.Token); }
            catch { break; }
            if (read == 0) break;
            totalRead += read;
        }
        return Encoding.ASCII.GetString(buffer, 0, totalRead);
    }

    [Fact]
    public async Task Get_WithMatchingIfNoneMatch_Returns304()
    {
        await using var server = await TestServer.StartAsync((req, res, ct) =>
        {
            var extraHeaders = new HttpHeader[] { new HttpHeader("etag"u8.ToArray(), "\"v1\""u8.ToArray()) };
            return res.WriteAsync(200, "Hello"u8.ToArray(), "text/plain"u8.ToArray(), true, extraHeaders, ct);
        });

        var request = "GET / HTTP/1.1\r\nHost: localhost\r\nIf-None-Match: \"v1\"\r\nConnection: close\r\n\r\n";
        var response = await SendRawAsync(server.Port, request);

        Assert.Contains("304 Not Modified", response);
        Assert.DoesNotContain("Hello", response);
    }

    [Fact]
    public async Task Get_WithMismatchingIfNoneMatch_Returns200()
    {
        await using var server = await TestServer.StartAsync((req, res, ct) =>
        {
            var extraHeaders = new HttpHeader[] { new HttpHeader("etag"u8.ToArray(), "\"v2\""u8.ToArray()) };
            return res.WriteAsync(200, "Hello"u8.ToArray(), "text/plain"u8.ToArray(), true, extraHeaders, ct);
        });

        var request = "GET / HTTP/1.1\r\nHost: localhost\r\nIf-None-Match: \"v1\"\r\nConnection: close\r\n\r\n";
        var response = await SendRawAsync(server.Port, request);

        Assert.Contains("200 OK", response);
        Assert.Contains("Hello", response);
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

        public static async Task<TestServer> StartAsync(RequestHandler handler)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();

            var cts = new CancellationTokenSource();
            var server = new Server(handler, port, "127.0.0.1");
            var runTask = server.StartAsync(cts.Token);
            await Task.Delay(50);
            return new TestServer(server, port, cts, runTask);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Task.WhenAny(_runTask, Task.Delay(100));
            _cts.Dispose();
        }
    }
}
