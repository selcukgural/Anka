using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anka.Test;

public class StreamingTests
{
    private static readonly byte[] TextPlainBytes = "text/plain"u8.ToArray();

    [Fact]
    public async Task ResponseStream_WriteAsync_IsCorrectlyEncoded()
    {
        await using var server = await TestServer.StartAsync(
            async (request, response, cancellationToken) =>
            {
                await using var stream = response.GetStream(cancellationToken);
                await stream.WriteAsync("stream data"u8.ToArray(), cancellationToken);
            });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await stream.WriteAsync("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
        
        var responseText = await ReadUntilFinalChunkAsync(stream, timeout.Token);

        Assert.Contains("HTTP/1.1 200 OK", responseText);
        Assert.Contains("transfer-encoding: chunked", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("B\r\nstream data\r\n", responseText);
        Assert.Contains("0\r\n\r\n", responseText);
    }

    [Fact]
    public async Task ResponseStream_CopyToAsync_FromMemoryStream_IsCorrectlyEncoded()
    {
        var content = "This is a large content to be streamed via CopyToAsync.";
        var sourceStream = new MemoryStream(Encoding.ASCII.GetBytes(content));

        await using var server = await TestServer.StartAsync(
            async (request, response, cancellationToken) =>
            {
                await using var stream = response.GetStream(cancellationToken);
                await sourceStream.CopyToAsync(stream, cancellationToken);
            });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await stream.WriteAsync("GET / HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
        
        var responseText = await ReadUntilFinalChunkAsync(stream, timeout.Token);

        Assert.Contains("HTTP/1.1 200 OK", responseText);
        Assert.Contains("transfer-encoding: chunked", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(content, responseText);
        Assert.Contains("0\r\n\r\n", responseText);
    }

    private static async Task<string> ReadUntilFinalChunkAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var total = new List<byte>();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            total.AddRange(buffer.AsSpan(0, read).ToArray());
            var text = Encoding.ASCII.GetString(total.ToArray());
            if (text.Contains("0\r\n\r\n")) return text;
        }
        return Encoding.ASCII.GetString(total.ToArray());
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
            try { await _runTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
