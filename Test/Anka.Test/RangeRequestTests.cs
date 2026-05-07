using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anka.Test;

public class RangeRequestTests
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
    public async Task Get_PartialRange_Returns206AndCorrectBody()
    {
        var fullBody = "0123456789"u8.ToArray();
        await using var server = await TestServer.StartAsync((req, res, ct) =>
        {
            if (req.Headers.TryGetValue("range"u8, out var rangeValue))
            {
                if (HttpParser.TryParseRange(rangeValue, out var start, out var end))
                {
                    // Handle offset range like bytes=5-
                    if (end == -1) end = fullBody.Length - 1;
                    // Handle suffix range like bytes=-5
                    if (start == -1) { start = fullBody.Length - end; end = fullBody.Length - 1; }

                    var length = (int)(end - start + 1);
                    var slice = fullBody.AsMemory((int)start, length);
                    return res.WritePartialAsync(start, end, fullBody.Length, slice, "text/plain"u8.ToArray(), true, default, ct);
                }
            }
            return res.WriteAsync(200, fullBody, "text/plain"u8.ToArray(), true, ct);
        });

        var request = "GET / HTTP/1.1\r\nHost: localhost\r\nRange: bytes=2-5\r\nConnection: close\r\n\r\n";
        var response = await SendRawAsync(server.Port, request);

        Assert.Contains("206 Partial Content", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Range: bytes 2-5/10", response, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("2345", response.TrimEnd());
    }

    [Fact]
    public async Task Get_FullContent_AdvertisesRangeSupport()
    {
        await using var server = await TestServer.StartAsync((req, res, ct) =>
        {
            return res.WriteAsync(200, "Hello"u8.ToArray(), "text/plain"u8.ToArray(), true, ct);
        });

        var request = "GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        var response = await SendRawAsync(server.Port, request);

        Assert.Contains("200 OK", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Accept-Ranges: bytes", response, StringComparison.OrdinalIgnoreCase);
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
