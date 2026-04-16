namespace Anka;

/// <summary>
/// Represents the result of parsing a chunked body during HTTP message processing.
/// </summary>
internal enum ChunkedBodyParseResult : byte
{
    Success = 0,
    Incomplete = 1,
    Invalid = 2
}

/// <summary>
/// Provides methods to parse chunked transfer-encoded HTTP response or request bodies.
/// </summary>
internal static class ChunkedBodyParser
{
    /// <summary>
    /// Attempts to read the chunk size from the provided buffer, as defined
    /// in the chunked transfer encoding for HTTP messages.
    /// </summary>
    /// <param name="buffer">
    /// The input buffer to process, containing the chunk size and possibly additional data.
    /// </param>
    /// <param name="chunkSize">
    /// When the method returns, contains the size of the chunk
    /// if the operation is successful; otherwise, zero.
    /// </param>
    /// <param name="consumed">
    /// When the method returns, contains the total number of bytes consumed
    /// from the <paramref name="buffer"/> if the operation is successful; otherwise, zero.
    /// </param>
    /// <returns>
    /// A <see cref="ChunkedBodyParseResult"/> value indicating the result of the chunk size parsing:
    /// <list type="bullet">
    /// <item><term>Success</term>: The chunk size was successfully parsed.</item>
    /// <item><term>Incomplete</term>: The buffer does not contain enough data to determine the chunk size.</item>
    /// <item><term>Invalid</term>: The chunk size could not be parsed due to invalid format or overflow.</item>
    /// </list>
    /// </returns>
    public static ChunkedBodyParseResult TryReadChunkSize(ReadOnlySpan<byte> buffer, out int chunkSize, out int consumed)
    {
        chunkSize = 0;
        consumed = 0;

        var lineEnd = buffer.IndexOf("\r\n"u8);
        if (lineEnd < 0)
        {
            return ChunkedBodyParseResult.Incomplete;
        }

        var sizeToken = buffer[..lineEnd];
        var extensionStart = sizeToken.IndexOf((byte)';');
        if (extensionStart >= 0)
        {
            sizeToken = sizeToken[..extensionStart];
        }

        if (sizeToken.IsEmpty)
        {
            return ChunkedBodyParseResult.Invalid;
        }

        var value = 0;
        foreach (var b in sizeToken)
        {
            int digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1
            };

            if (digit < 0 || value > (int.MaxValue - digit) / 16)
            {
                return ChunkedBodyParseResult.Invalid;
            }

            value = value * 16 + digit;
        }

        chunkSize = value;
        consumed = lineEnd + 2;
        return ChunkedBodyParseResult.Success;
    }

    /// <summary>
    /// Attempts to consume the trailers from the provided buffer, as defined in the chunked transfer encoding
    /// for HTTP messages. Trailers are additional metadata fields at the end of a chunked body.
    /// </summary>
    /// <param name="buffer">
    /// The input buffer containing the potential trailer headers and other data.
    /// </param>
    /// <param name="consumed">
    /// When the method returns, contains the total number of bytes consumed from the <paramref name="buffer"/>
    /// if the operation is successful or partially complete; otherwise, zero.
    /// </param>
    /// <returns>
    /// A <see cref="ChunkedBodyParseResult"/> value indicating the result of the trailer parsing:
    /// - <c>Success</c>: The trailers were successfully consumed.
    /// - <c>Incomplete</c>: The buffer does not contain enough data to complete the trailer parsing.
    /// - <c>Invalid</c>: The trailers could not be parsed due to an invalid format.
    /// </returns>
    public static ChunkedBodyParseResult TryConsumeTrailers(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;
        while (true)
        {
            var lineEnd = buffer[consumed..].IndexOf("\r\n"u8);
            switch (lineEnd)
            {
                case < 0:
                    return ChunkedBodyParseResult.Incomplete;
                case 0:
                    consumed += 2;
                    return ChunkedBodyParseResult.Success;
            }

            var line = buffer.Slice(consumed, lineEnd);
            if (line.IndexOf((byte)':') <= 0)
            {
                return ChunkedBodyParseResult.Invalid;
            }

            consumed += lineEnd + 2;
        }
    }
}
