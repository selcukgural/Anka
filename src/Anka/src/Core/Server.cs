using System.Net;
using System.Net.Sockets;
using Anka.Exceptions;

namespace Anka;

/// <summary>
/// Represents a TCP server designed to handle incoming client connections
/// and process requests using a specified request handler.
/// </summary>
public sealed class Server
{
    /// <summary>
    /// Represents the endpoint configuration for the server, which includes the IP address and port
    /// the server will bind to and listen for incoming connections.
    /// </summary>
    /// <remarks>
    /// This field is initialized in the constructor using the provided host and port.
    /// It is used by the server socket to bind and start listening for incoming connections.
    /// </remarks>
    private readonly IPEndPoint _endPoint;

    /// <summary>
    /// Holds a reference to the <see cref="RequestHandler"/> delegate, which is responsible for
    /// processing incoming HTTP requests and constructing appropriate HTTP responses.
    /// </summary>
    /// <remarks>
    /// This member is initialized through the constructor of the <see cref="Server"/> class.
    /// It is used internally to process client connections and execute the request handling logic.
    /// </remarks>
    private readonly RequestHandler _handler;

    /// <summary>
    /// Specifies the configuration options for the server, used to tailor its behavior,
    /// such as thread pool settings, acceptor count, connection backlog, and default response headers.
    /// </summary>
    /// <remarks>
    /// This field is assigned during the server's instantiation. If no options are provided,
    /// it is initialized with default values in a new <see cref="ServerOptions"/> instance.
    /// It controls various operational parameters such as the minimum number of
    /// thread pool threads, the number of parallel accept loops, and connection handling capacity.
    /// </remarks>
    private readonly ServerOptions _options;

    /// <summary>
    /// Number of connections currently being served.
    /// </summary>
    private int _activeConnections;

    /// <summary>
    /// Completed when the last connection ends after shutdown has started.
    /// </summary>
    private TaskCompletionSource? _drained;

    /// <summary>
    /// Raised once the listening socket has been bound and started accepting connections.
    /// Useful for startup instrumentation and readiness reporting.
    /// </summary>
    public event Action<IPEndPoint>? ListeningStarted;

    /// <summary>
    /// Represents an HTTP server that listens for incoming requests and handles them with a specified request handler.
    /// </summary>
    public Server(RequestHandler handler, int port, string host = "127.0.0.1", ServerOptions? options = null)
    {
        if (port is < 1 or > 65535)
        {
            throw new AnkaOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            throw new AnkaArgumentException("Invalid IP address. Use an IPv4 or IPv6 literal such as \"0.0.0.0\" or \"::\".", nameof(host));
        }

        _handler = handler;
        _endPoint = new IPEndPoint(ip, port);
        _options = options ?? new ServerOptions();
    }

    /// <summary>
    /// Starts the server and serves connections until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancelling it starts a graceful shutdown: the listener closes, idle connections are closed, and requests
    /// already in the handler get up to <see cref="ServerOptions.ShutdownTimeout"/> to finish.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> that completes when the listener is closed and every connection has ended.
    /// </returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Pre-warm the thread pool so that burst workloads at high connection counts
        // (e.g. c=400) do not spend the first few seconds waiting for the pool to
        // slowly inject new threads (default hill-climb injects ~1 thread per 500 ms).
        // Only raise the minimum — never lower an already-higher host-level setting.
        ThreadPool.GetMinThreads(out var currentMin, out var currentMinIo);
        var desiredMin = _options.MinThreadPoolThreads ?? Environment.ProcessorCount * 2 + 2;

        if (desiredMin > currentMin)
        {
            ThreadPool.SetMinThreads(desiredMin, Math.Max(desiredMin, currentMinIo));
        }

        using var socket = new Socket(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        // "::" listens on IPv6 and, with dual mode, on IPv4 as well.
        if (_endPoint.AddressFamily == AddressFamily.InterNetworkV6 && _endPoint.Address.Equals(IPAddress.IPv6Any))
        {
            socket.DualMode = true;
        }

        socket.NoDelay = true;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(_endPoint);
        socket.Listen(_options.Backlog);

        // Cancelled when the shutdown timeout expires: aborts handlers and closes the remaining connections.
        using var abortCts = new CancellationTokenSource();

        ListeningStarted?.Invoke((IPEndPoint)socket.LocalEndPoint!);

        // Run multiple accept loops in parallel to avoid serialization under burst traffic.
        var acceptorCount = _options.AcceptorCount ?? Math.Max(Environment.ProcessorCount / 2, 2);
        var acceptors = new Task[acceptorCount];

        for (var i = 0; i < acceptorCount; i++)
        {
            acceptors[i] = AcceptLoopAsync(socket, cancellationToken, abortCts.Token);
        }

        await Task.WhenAll(acceptors);

        await DrainAsync(abortCts);
    }

    /// <summary>
    /// Waits for connections that are still running a handler, up to <see cref="ServerOptions.ShutdownTimeout"/>,
    /// then aborts whatever is left.
    /// </summary>
    private async Task DrainAsync(CancellationTokenSource abortCts)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _drained, drained);
        if (Volatile.Read(ref _activeConnections) == 0)
        {
            drained.TrySetResult();
        }

        if (_options.ShutdownTimeout > TimeSpan.Zero)
        {
            await Task.WhenAny(drained.Task, Task.Delay(_options.ShutdownTimeout));
        }

        await abortCts.CancelAsync();
        await drained.Task;
    }

    /// <summary>
    /// Continuously accepts incoming client connections from a listening socket
    /// and starts a connection-handling task for each client.
    /// </summary>
    /// <param name="listener">The socket that is listening for incoming connections.</param>
    /// <param name="stoppingToken">Stops accepting and starts the graceful shutdown of open connections.</param>
    /// <param name="abortToken">Aborts handlers and closes connections once the shutdown timeout expires.</param>
    /// <returns>A task representing the asynchronous operation of accepting connections.</returns>
    private async Task AcceptLoopAsync(Socket listener, CancellationToken stoppingToken, CancellationToken abortToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(stoppingToken);

                var active = Interlocked.Increment(ref _activeConnections);
                if (_options.MaxConcurrentConnections is { } maxConnections && active > maxConnections)
                {
                    client.Dispose();
                    ConnectionEnded();
                    continue;
                }

                // Fire & forget — accept loop never blocks on a connection
                _ = RunConnectionAsync(client, stoppingToken, abortToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    /// <summary>
    /// Runs one connection and releases its slot in the active-connection count when it ends.
    /// </summary>
    private async Task RunConnectionAsync(Socket client, CancellationToken stoppingToken, CancellationToken abortToken)
    {
        try
        {
            await Connection.RunAsync(client, _handler, _options, stoppingToken, abortToken);
        }
        finally
        {
            ConnectionEnded();
        }
    }

    private void ConnectionEnded()
    {
        if (Interlocked.Decrement(ref _activeConnections) == 0)
        {
            Volatile.Read(ref _drained)?.TrySetResult();
        }
    }
}
