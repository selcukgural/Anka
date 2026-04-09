namespace Anka;

/// <summary>
/// Extension methods on <see cref="HttpResponseWriter"/> for fluent response header building.
/// </summary>
/// <example>
/// <code>
/// // Redirect
/// await response
///     .AddHeader(HttpHeaderNames.Location, "/dashboard"u8)
///     .WriteAsync(301, default, default, keepAlive: false, ct);
///
/// // CORS + custom header
/// await response
///     .AddHeader(HttpHeaderNames.AccessControlAllowOrigin, "*"u8)
///     .AddHeader(HttpHeaderNames.AccessControlAllowMethods, "GET, POST"u8)
///     .AddHeader("x-request-id"u8, requestIdBytes)
///     .WriteAsync(200, body, contentType, keepAlive: true, ct);
/// </code>
/// </example>
public static class HttpResponseWriterExtensions
{
    /// <summary>
    /// Starts a fluent header chain.
    /// Accepts <see cref="ReadOnlySpan{T}"/>, <c>byte[]</c>, and <c>"..."u8</c> UTF-8 literals directly.
    /// </summary>
    /// <param name="writer">The response writer.</param>
    /// <param name="name">Header name as lowercase ASCII bytes (see <see cref="HttpHeaderNames"/>).</param>
    /// <param name="value">Header value as ASCII/UTF-8 bytes.</param>
    /// <returns>A <see cref="ResponseContext"/> with the first header queued.</returns>
    public static ResponseContext AddHeader(
        this HttpResponseWriter writer,
        ReadOnlySpan<byte>      name,
        ReadOnlySpan<byte>      value) =>
        new ResponseContext(writer).AddHeader(name, value);

    /// <summary>
    /// Starts a fluent header chain using strings.
    /// <paramref name="name"/> is lower-cased automatically. Allocates — use for startup-time
    /// or low-frequency paths only.
    /// </summary>
    /// <param name="writer">The response writer.</param>
    /// <param name="name">Header name string (will be lower-cased).</param>
    /// <param name="value">Header value string.</param>
    /// <returns>A <see cref="ResponseContext"/> with the first header queued.</returns>
    public static ResponseContext AddHeader(
        this HttpResponseWriter writer,
        string                  name,
        string                  value) =>
        new ResponseContext(writer).AddHeader(name, value);
}
