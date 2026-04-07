namespace Anka;

/// <summary>
/// Provides utilities for parsing and converting HTTP methods represented as byte spans.
/// </summary>
internal static class HttpMethodParser
{
    /// Parses the given span of bytes and converts it into an <see cref="HttpMethod"/> enumeration value.
    /// <param name="span">The span of bytes representing the HTTP method string.</param>
    /// <returns>
    /// An <see cref="HttpMethod"/> enumeration value corresponding to the provided span.
    /// Returns <see cref="HttpMethod.Unknown"/> if the span does not match a known HTTP method.
    /// </returns>
    public static HttpMethod Parse(ReadOnlySpan<byte> span) => span switch
    {
        _ when span.SequenceEqual("GET"u8)     => HttpMethod.Get,
        _ when span.SequenceEqual("POST"u8)    => HttpMethod.Post,
        _ when span.SequenceEqual("PUT"u8)     => HttpMethod.Put,
        _ when span.SequenceEqual("DELETE"u8)  => HttpMethod.Delete,
        _ when span.SequenceEqual("HEAD"u8)    => HttpMethod.Head,
        _ when span.SequenceEqual("OPTIONS"u8) => HttpMethod.Options,
        _ when span.SequenceEqual("PATCH"u8)   => HttpMethod.Patch,
        _ when span.SequenceEqual("TRACE"u8)   => HttpMethod.Trace,
        _ when span.SequenceEqual("CONNECT"u8) => HttpMethod.Connect,
        _                                      => HttpMethod.Unknown,
    };

    /// <summary>
    /// Converts an HttpMethod enum value to its corresponding byte representation.
    /// </summary>
    /// <param name="method">
    /// The HTTP method to be converted to its byte representation. Must be a valid
    /// value from the <see cref="HttpMethod"/> enum.
    /// </param>
    /// <returns>
    /// A <see cref="ReadOnlySpan{byte}"/> containing the byte representation of the specified HTTP method.
    /// If the method is unknown, this returns the byte sequence corresponding to "UNKNOWN".
    /// </returns>
    public static ReadOnlySpan<byte> ToBytes(this HttpMethod method) => method switch
    {
        HttpMethod.Get     => "GET"u8,
        HttpMethod.Post    => "POST"u8,
        HttpMethod.Put     => "PUT"u8,
        HttpMethod.Delete  => "DELETE"u8,
        HttpMethod.Head    => "HEAD"u8,
        HttpMethod.Options => "OPTIONS"u8,
        HttpMethod.Patch   => "PATCH"u8,
        HttpMethod.Trace   => "TRACE"u8,
        HttpMethod.Connect => "CONNECT"u8,
        _                  => "UNKNOWN"u8,
    };
}