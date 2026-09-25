using Anka.Exceptions;

namespace Anka;

/// <summary>
/// Configuration options for <see cref="Server"/>.
/// All properties are optional; when left <see langword="null"/> the server picks a
/// sensible default that scales with the number of logical processors on the current machine.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>Default for <see cref="MaxRequestBodySize"/>: 30,000,000 bytes (≈28.6 MB), matching Kestrel.</summary>
    public const int DefaultMaxRequestBodySize = 30_000_000;

    /// <summary>Default for <see cref="ReadTimeout"/> and <see cref="RequestHeadersTimeout"/>: 30 seconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private int? _maxRequestBodySize = DefaultMaxRequestBodySize;
    private int? _maxRequestTargetSize;
    private int _maxRequestHeadersSize = 8 * 1024;
    private TimeSpan? _readTimeout = DefaultTimeout;
    private TimeSpan? _requestHeadersTimeout = DefaultTimeout;
    private int? _maxConcurrentConnections;

    /// <summary>
    /// The minimum number of worker and I/O-completion threads that
    /// <see cref="System.Threading.ThreadPool"/> should keep alive.
    /// <para>
    /// When <see langword="null"/> (the default) the server calculates
    /// <c>Environment.ProcessorCount * 2 + 2</c>, which is sufficient for
    /// async I/O continuations without over-allocating on many-core machines.
    /// The value is only applied when it exceeds the pool's current minimum,
    /// so existing host-level configurations are never overridden downward.
    /// </para>
    /// </summary>
    public int? MinThreadPoolThreads { get; init; }

    /// <summary>
    /// The number of concurrent accept loops to run.
    /// <para>
    /// When <see langword="null"/> (the default) the server uses
    /// <c>Math.Max(Environment.ProcessorCount / 2, 2)</c>.
    /// </para>
    /// </summary>
    public int? AcceptorCount { get; init; }

    /// <summary>
    /// The backlog size passed to <see cref="System.Net.Sockets.Socket.Listen(int)"/>.
    /// Defaults to <c>512</c>.
    /// </summary>
    public int Backlog { get; init; } = 512;

    /// <summary>
    /// Extra response headers sent on every HTTP response (e.g., security headers,
    /// CORS headers, server branding). Applied before any per-request extra headers.
    /// </summary>
    /// <remarks>
    /// Build the list once at startup for zero per-request allocation:
    /// <code>
    /// var options = new ServerOptions
    /// {
    ///     DefaultResponseHeaders =
    ///     [
    ///         new HttpHeader("x-content-type-options"u8.ToArray(), "nosniff"u8.ToArray()),
    ///         new HttpHeader("x-frame-options"u8.ToArray(),        "DENY"u8.ToArray()),
    ///     ]
    /// };
    /// </code>
    /// </remarks>
    public IReadOnlyList<HttpHeader> DefaultResponseHeaders { get; init; } = [];

    /// <summary>
    /// Specifies the maximum allowed size, in bytes, for the HTTP request body.
    /// <para>
    /// When set to a non-<see langword="null"/> value, requests with a body size
    /// exceeding the specified limit will receive a 413 (Payload Too Large) response,
    /// and the connection will be closed. Chunked request bodies are measured against
    /// this limit using their decoded body length.
    /// </para>
    /// <para>
    /// Defaults to <see cref="DefaultMaxRequestBodySize"/>. Set to <see langword="null"/> to
    /// impose no limit — only do this behind a proxy that enforces its own limit, because the
    /// body is buffered in memory before the handler runs.
    /// </para>
    /// <exception cref="AnkaOutOfRangeException">
    /// Thrown when an attempt is made to set a negative value.
    /// </exception>
    /// </summary>
    public int? MaxRequestBodySize  
    {
        get => _maxRequestBodySize;
        set
        {
            if (value < 0)
            {
                throw new AnkaOutOfRangeException(nameof(MaxRequestBodySize), "Value must be non-negative.");
            }
            
            _maxRequestBodySize = value;
        }
    }

    /// <summary>
    /// Specifies the maximum allowed size, in bytes, for the HTTP request target
    /// from the request line (for example, <c>/path?query=value</c>).
    /// <para>
    /// When set to a non-<see langword="null"/> value, requests whose target exceeds
    /// the specified limit will receive a 414 (URI Too Long) response, and the
    /// connection will be closed.
    /// </para>
    /// <para>
    /// A <see langword="null"/> value (the default) imposes no limit on request-target size.
    /// </para>
    /// <exception cref="AnkaOutOfRangeException">
    /// Thrown when an attempt is made to set a negative value.
    /// </exception>
    /// </summary>
    public int? MaxRequestTargetSize
    {
        get => _maxRequestTargetSize;
        set
        {
            if (value < 0)
            {
                throw new AnkaOutOfRangeException(nameof(MaxRequestTargetSize), "Value must be non-negative.");
            }
            
            _maxRequestTargetSize = value;
        }
    }

    /// <summary>
    /// Specifies the maximum allowed total size, in bytes, for request header
    /// names and values stored by the parser.
    /// <para>
    /// Requests whose headers exceed this limit, or that exceed the built-in
    /// header-count limit, will receive a 431 (Request Header Fields Too Large)
    /// response, and the connection will be closed.
    /// </para>
    /// </summary>
    public int MaxRequestHeadersSize
    {
        get => _maxRequestHeadersSize;
        set
        {
            if (value < 0)
            {
                throw new AnkaOutOfRangeException(nameof(MaxRequestHeadersSize), "Value must be non-negative.");
            }

            _maxRequestHeadersSize = value;
        }
    }

    /// <summary>
    /// Specifies the maximum amount of idle time allowed while waiting for the next
    /// read from a client connection.
    /// <para>
    /// When set, connections that stop making forward progress during request reads
    /// are closed. This also acts as the keep-alive idle timeout between requests.
    /// </para>
    /// <para>
    /// Defaults to <see cref="DefaultTimeout"/>. Set to <see langword="null"/> to disable.
    /// A per-read timeout alone does not stop a client that trickles one byte just under the
    /// limit; <see cref="RequestHeadersTimeout"/> covers that case for the header block.
    /// </para>
    /// </summary>
    public TimeSpan? ReadTimeout
    {
        get => _readTimeout;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new AnkaOutOfRangeException(nameof(ReadTimeout), "Value must be non-negative.");
            }

            _readTimeout = value;
        }
    }

    /// <summary>
    /// Specifies the maximum total time allowed to receive the complete request line and
    /// header block, measured from the first byte of the request.
    /// <para>
    /// Connections that exceed it are closed. Unlike <see cref="ReadTimeout"/>, which resets on
    /// every received byte, this is an absolute deadline, so it defeats Slowloris-style clients
    /// that send headers one byte at a time.
    /// </para>
    /// <para>
    /// Defaults to <see cref="DefaultTimeout"/>. Set to <see langword="null"/> to disable.
    /// </para>
    /// <exception cref="AnkaOutOfRangeException">
    /// Thrown when an attempt is made to set a negative value.
    /// </exception>
    /// </summary>
    public TimeSpan? RequestHeadersTimeout
    {
        get => _requestHeadersTimeout;
        set
        {
            if (value < TimeSpan.Zero)
            {
                throw new AnkaOutOfRangeException(nameof(RequestHeadersTimeout), "Value must be non-negative.");
            }

            _requestHeadersTimeout = value;
        }
    }

    /// <summary>
    /// Specifies the maximum number of connections served at the same time.
    /// <para>
    /// When the limit is reached, newly accepted connections are closed immediately without
    /// a response. A <see langword="null"/> value (the default) imposes no limit.
    /// </para>
    /// <exception cref="AnkaOutOfRangeException">
    /// Thrown when an attempt is made to set a value less than 1.
    /// </exception>
    /// </summary>
    public int? MaxConcurrentConnections
    {
        get => _maxConcurrentConnections;
        set
        {
            if (value < 1)
            {
                throw new AnkaOutOfRangeException(nameof(MaxConcurrentConnections), "Value must be at least 1.");
            }

            _maxConcurrentConnections = value;
        }
    }
}
