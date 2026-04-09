namespace Anka;

/// <summary>
/// Configuration options for <see cref="Server"/>.
/// All properties are optional; when left <see langword="null"/> the server picks a
/// sensible default that scales with the number of logical processors on the current machine.
/// </summary>
public sealed class ServerOptions
{
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
}
