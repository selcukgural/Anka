using Anka.Exceptions;

namespace Anka;

/// <summary>
/// Configuration options for <see cref="Server"/>.
/// All properties are optional; when left <see langword="null"/> the server picks a
/// sensible default that scales with the number of logical processors on the current machine.
/// </summary>
public sealed class ServerOptions
{
    private int? _maxRequestBodySize;

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
    /// and the connection will be closed. This property does not apply to requests
    /// that use chunked transfer encoding, which are rejected with a 501 (Not Implemented) response.
    /// </para>
    /// <para>
    /// A <see langword="null"/> value (the default) imposes no limit on the size of the request body.
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
}
