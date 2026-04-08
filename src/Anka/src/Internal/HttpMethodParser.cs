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
    /// <remarks>
    /// This uses a length/byte dispatch instead of chained <c>SequenceEqual</c> calls because
    /// HTTP methods are short fixed ASCII tokens on a very hot path. Narrowing by length and a
    /// few discriminating bytes reduces repeated comparisons and gives the JIT a simpler branch tree.
    /// </remarks>
    public static HttpMethod Parse(ReadOnlySpan<byte> span) => span.Length switch
    {
        3 => span[0] switch
        {
            (byte)'G' when span[1] == (byte)'E' && span[2] == (byte)'T' => HttpMethod.Get,
            (byte)'P' when span[1] == (byte)'U' && span[2] == (byte)'T' => HttpMethod.Put,
            _                                                            => HttpMethod.Unknown,
        },
        4 => span[0] switch
        {
            (byte)'P' when span[1] == (byte)'O' && span[2] == (byte)'S' && span[3] == (byte)'T' => HttpMethod.Post,
            (byte)'H' when span[1] == (byte)'E' && span[2] == (byte)'A' && span[3] == (byte)'D' => HttpMethod.Head,
            _                                                                                      => HttpMethod.Unknown,
        },
        5 => span[0] switch
        {
            (byte)'P' when span[1] == (byte)'A' && span[2] == (byte)'T' && span[3] == (byte)'C' && span[4] == (byte)'H' => HttpMethod.Patch,
            (byte)'T' when span[1] == (byte)'R' && span[2] == (byte)'A' && span[3] == (byte)'C' && span[4] == (byte)'E' => HttpMethod.Trace,
            _                                                                                                                => HttpMethod.Unknown,
        },
        6 when span[0] == (byte)'D' &&
               span[1] == (byte)'E' &&
               span[2] == (byte)'L' &&
               span[3] == (byte)'E' &&
               span[4] == (byte)'T' &&
               span[5] == (byte)'E' => HttpMethod.Delete,
        7 => span[0] switch
        {
            (byte)'O' when span[1] == (byte)'P' && span[2] == (byte)'T' && span[3] == (byte)'I' && span[4] == (byte)'O' && span[5] == (byte)'N' && span[6] == (byte)'S' => HttpMethod.Options,
            (byte)'C' when span[1] == (byte)'O' && span[2] == (byte)'N' && span[3] == (byte)'N' && span[4] == (byte)'E' && span[5] == (byte)'C' && span[6] == (byte)'T' => HttpMethod.Connect,
            _                                                                                                                                                              => HttpMethod.Unknown,
        },
        _ => HttpMethod.Unknown,
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