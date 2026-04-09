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
    private readonly ServerOptions  _options;

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
            throw new AnkaPortOutOfRageException(nameof(port), "Port must be between 1 and 65535.");
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            throw new AnkaArgumentException("Invalid IP address.", nameof(host));
        }

        _handler  = handler;
        _endPoint = new IPEndPoint(ip, port);
        _options  = options ?? new ServerOptions();
    }

    /// <summary>
    /// Starts the server asynchronously and begins listening for incoming connections on the configured endpoint.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the server's operation. Listening will terminate when the cancellation is requested.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> representing the asynchronous operation. The task completes when the server is shut down.
    /// </returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Pre-warm the thread pool so that burst workloads at high connection counts
        // (e.g. c=400) do not spend the first few seconds waiting for the pool to
        // slowly inject new threads (default hill-climb injects ~1 thread per 500 ms).
        // Only raise the minimum — never lower an already-higher host-level setting.
        ThreadPool.GetMinThreads(out var currentMin, out var currentMinIO);
        var desiredMin = _options.MinThreadPoolThreads ?? Environment.ProcessorCount * 2 + 2;
        if (desiredMin > currentMin)
            ThreadPool.SetMinThreads(desiredMin, Math.Max(desiredMin, currentMinIO));

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        socket.NoDelay = true;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(_endPoint);
        socket.Listen(_options.Backlog);

        Console.WriteLine($"Listening on {_endPoint}");
        ListeningStarted?.Invoke(_endPoint);

        // Run multiple accept loops in parallel to avoid serialization under burst traffic.
        var acceptorCount = _options.AcceptorCount ?? Math.Max(Environment.ProcessorCount / 2, 2);
        var acceptors     = new Task[acceptorCount];

        for (var i = 0; i < acceptorCount; i++)
        {
            acceptors[i] = AcceptLoopAsync(socket, cancellationToken);
        }

        await Task.WhenAll(acceptors);
    }

    /// <summary>
    /// Continuously accepts incoming client connections from a listening socket
    /// and starts a connection-handling task for each client.
    /// </summary>
    /// <param name="listener">The socket that is listening for incoming connections.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to stop the accept loop.</param>
    /// <returns>A task representing the asynchronous operation of accepting connections.</returns>
    private async Task AcceptLoopAsync(Socket listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(cancellationToken);

                // Fire & forget — accept loop never blocks on a connection
                _ = Connection.RunAsync(client, _handler, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }
}