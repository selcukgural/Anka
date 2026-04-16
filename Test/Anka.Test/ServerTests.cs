using System.Net;
using System.Net.Sockets;
using Anka.Exceptions;

namespace Anka.Test;

public class ServerTests
{
    private static readonly RequestHandler NoopHandler =
        (_, _, _) => ValueTask.CompletedTask;
    
    [Fact]
    public void Constructor_ValidPortAndHost_DoesNotThrow()
    {
        var ex = Record.Exception(() => new Server(NoopHandler, 8080));
        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_LoopbackHost_DoesNotThrow()
    {
        var ex = Record.Exception(() => new Server(NoopHandler, 9090, "127.0.0.1"));
        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_ZeroZeroZeroZeroHost_DoesNotThrow()
    {
        var ex = Record.Exception(() => new Server(NoopHandler, 8000, "0.0.0.0"));
        Assert.Null(ex);
    }
    
    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void Constructor_BoundaryAndCommonPorts_DoesNotThrow(int port)
    {
        var ex = Record.Exception(() => new Server(NoopHandler, port));
        Assert.Null(ex);
    }
    
    [Fact]
    public void Constructor_PortZero_ThrowsAnkaPortOutOfRageException()
    {
        Assert.Throws<AnkaOutOfRangeException>(() => new Server(NoopHandler, 0));
    }

    [Fact]
    public void Constructor_PortNegative_ThrowsAnkaPortOutOfRageException()
    {
        Assert.Throws<AnkaOutOfRangeException>(() => new Server(NoopHandler, -1));
    }

    [Fact]
    public void Constructor_Port65536_ThrowsAnkaPortOutOfRageException()
    {
        Assert.Throws<AnkaOutOfRangeException>(() => new Server(NoopHandler, 65536));
    }

    [Fact]
    public void Constructor_PortMaxInt_ThrowsAnkaPortOutOfRageException()
    {
        Assert.Throws<AnkaOutOfRangeException>(() => new Server(NoopHandler, int.MaxValue));
    }

    [Fact]
    public void Constructor_InvalidHost_ThrowsAnkaArgumentException()
    {
        Assert.Throws<AnkaArgumentException>(() => new Server(NoopHandler, 8080, "not-an-ip"));
    }

    [Fact]
    public void Constructor_HostnameinsteadOfIp_ThrowsAnkaArgumentException()
    {
        Assert.Throws<AnkaArgumentException>(() => new Server(NoopHandler, 8080, "localhost"));
    }

    [Fact]
    public void Constructor_EmptyHost_ThrowsAnkaArgumentException()
    {
        Assert.Throws<AnkaArgumentException>(() => new Server(NoopHandler, 8080, ""));
    }

    [Fact]
    public async Task StartAsync_RaisesListeningStarted()
    {
        var port = GetFreePort();
        var signal = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new Server(NoopHandler, port);
        server.ListeningStarted += endPoint =>
        {
            signal.TrySetResult(endPoint);
            cts.Cancel();
        };

        await server.StartAsync(cts.Token);

        Assert.True(signal.Task.IsCompleted);
        var endPoint = await signal.Task;
        Assert.Equal(IPAddress.Loopback, endPoint.Address);
        Assert.Equal(port, endPoint.Port);
    }

    [Fact]
    public void Constructor_WithCustomOptions_DoesNotThrow()
    {
        var options = new ServerOptions { MinThreadPoolThreads = 8, AcceptorCount = 2, Backlog = 256 };
        var ex = Record.Exception(() => new Server(NoopHandler, 8080, "127.0.0.1", options));
        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_WithNullOptions_UsesDefaults()
    {
        var ex = Record.Exception(() => new Server(NoopHandler, 8080, "127.0.0.1", null));
        Assert.Null(ex);
    }

    [Fact]
    public void ServerOptions_Defaults_AreReasonable()
    {
        var options = new ServerOptions();
        Assert.Null(options.MinThreadPoolThreads);
        Assert.Null(options.AcceptorCount);
        Assert.Equal(512, options.Backlog);
        Assert.Null(options.ReadTimeout);
    }

    [Fact]
    public void ServerOptions_ReadTimeout_Negative_ThrowsAnkaOutOfRangeException()
    {
        var options = new ServerOptions();
        Assert.Throws<AnkaOutOfRangeException>(() => options.ReadTimeout = TimeSpan.FromMilliseconds(-1));
    }

    [Fact]
    public void ServerOptions_ReadTimeout_ZeroAndPositive_AreAccepted()
    {
        var options = new ServerOptions
        {
            ReadTimeout = TimeSpan.Zero
        };

        Assert.Equal(TimeSpan.Zero, options.ReadTimeout);

        options.ReadTimeout = TimeSpan.FromSeconds(5);
        Assert.Equal(TimeSpan.FromSeconds(5), options.ReadTimeout);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
