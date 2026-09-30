using System.Buffers;

namespace Anka.Internal;

/// <summary>
/// A zero-allocation parser for multipart/form-data bodies.
/// Designed to work with <see cref="ReadOnlySequence{T}"/> to avoid large buffer copies.
/// </summary>
public ref struct MultipartParser
{
    private SequenceReader<byte> _reader;

    /// <summary>
    /// "\r\n--" + boundary. RFC 2046 §5.1.1: the CRLF before "--boundary" belongs to the delimiter,
    /// not to the preceding part's content. The first boundary has no leading CRLF, so it is matched
    /// with <c>_delimiter[2..]</c>.
    /// </summary>
    private readonly byte[] _delimiter;
    private bool _started;
    private bool _finished;

    public MultipartParser(ReadOnlySequence<byte> body, ReadOnlySpan<byte> boundary)
    {
        _reader = new SequenceReader<byte>(body);
        _delimiter = new byte[boundary.Length + 4];
        "\r\n--"u8.CopyTo(_delimiter);
        boundary.CopyTo(_delimiter.AsSpan(4));
        _started = false;
        _finished = false;
    }

    public bool TryReadNextPart(out MultipartPart part)
    {
        part = default;
        if (_finished)
        {
            return false;
        }

        if (!_started)
        {
            // The first boundary may be preceded by a preamble, which is ignored.
            if (!_reader.TryReadTo(out ReadOnlySequence<byte> _, _delimiter.AsSpan(2), advancePastDelimiter: true))
            {
                _finished = true;
                return false;
            }

            _started = true;
        }

        // Close delimiter: boundary followed by "--".
        if (_reader.IsNext("--"u8, advancePast: true))
        {
            _finished = true;
            return false;
        }

        // Rest of the boundary line (optional transport padding) up to CRLF.
        if (!_reader.TryReadTo(out ReadOnlySequence<byte> _, "\r\n"u8, advancePastDelimiter: true))
        {
            _finished = true;
            return false;
        }

        // Part headers end with an empty line; a part may also have no headers at all.
        var headersStart = _reader.Position;
        ReadOnlySequence<byte> headersSeq;
        if (_reader.IsNext("\r\n"u8, advancePast: true))
        {
            headersSeq = _reader.Sequence.Slice(headersStart, headersStart);
        }
        else
        {
            if (!_reader.TryReadTo(out ReadOnlySequence<byte> _, "\r\n\r\n"u8, advancePastDelimiter: false))
            {
                _finished = true;
                return false;
            }

            headersSeq = _reader.Sequence.Slice(headersStart, _reader.Position);
            _reader.Advance(4);
        }

        // The content runs up to the next "\r\n--boundary".
        var contentStart = _reader.Position;
        if (!_reader.TryReadTo(out ReadOnlySequence<byte> contentSeq, _delimiter, advancePastDelimiter: true))
        {
            _finished = true;
            return false;
        }

        part = new MultipartPart(headersSeq, _reader.Sequence.Slice(contentStart, contentSeq.End));
        return true;
    }
}

public readonly ref struct MultipartPart(ReadOnlySequence<byte> headers, ReadOnlySequence<byte> content)
{
    public ReadOnlySequence<byte> Headers { get; } = headers;
    public ReadOnlySequence<byte> Content { get; } = content;

    public bool TryGetContentDisposition(out ReadOnlySequence<byte> name, out ReadOnlySequence<byte> fileName)
    {
        name = default;
        fileName = default;

        var reader = new SequenceReader<byte>(Headers);
        while (reader.TryReadTo(out ReadOnlySequence<byte> headerName, (byte)':', advancePastDelimiter: true))
        {
            if (HttpParser.AsciiEqualsIgnoreCase(headerName.IsSingleSegment ? headerName.FirstSpan : headerName.ToArray(), "content-disposition"u8))
            {
                var headersRemaining = Headers.Slice(reader.Position);
                var endOfLineIdx = FindToken(headersRemaining, "\r\n"u8);
                
                var line = endOfLineIdx == -1 ? headersRemaining : headersRemaining.Slice(0, endOfLineIdx);
                if (TryParseContentDisposition(line, out name, out fileName))
                {
                    return true;
                }
            }
            
            // Advance to next line if not matched
            if (!reader.TryReadTo(out ReadOnlySequence<byte> _, "\r\n"u8, advancePastDelimiter: true))
            {
                break;
            }
        }
        return false;
    }

    /// <summary>
    /// Reads the <c>name</c> and <c>filename</c> parameters from a Content-Disposition value such as
    /// <c>form-data; name="field"; filename="a.txt"</c>. Parameters are matched by their exact name,
    /// so <c>filename=</c> is never mistaken for <c>name=</c>, and a <c>;</c> inside a quoted value
    /// does not split it.
    /// </summary>
    private static bool TryParseContentDisposition(ReadOnlySequence<byte> line, out ReadOnlySequence<byte> name, out ReadOnlySequence<byte> fileName)
    {
        name = default;
        fileName = default;

        ReadOnlySpan<byte> span = line.IsSingleSegment ? line.FirstSpan : line.ToArray();

        // Skip the disposition type ("form-data").
        var offset = IndexOfUnquoted(span, (byte)';');
        while (offset >= 0)
        {
            offset++;
            var rest = span[offset..];
            var segmentLength = IndexOfUnquoted(rest, (byte)';');
            var segment = segmentLength < 0 ? rest : rest[..segmentLength];

            var eq = segment.IndexOf((byte)'=');
            if (eq > 0)
            {
                var parameterName = HttpParser.TrimOws(segment[..eq]);
                if (TryGetParameterValue(segment, eq + 1, out var valueStart, out var valueLength))
                {
                    if (HttpParser.AsciiEqualsIgnoreCase(parameterName, "name"u8))
                    {
                        name = line.Slice(offset + valueStart, valueLength);
                    }
                    else if (HttpParser.AsciiEqualsIgnoreCase(parameterName, "filename"u8))
                    {
                        fileName = line.Slice(offset + valueStart, valueLength);
                    }
                }
            }

            offset = segmentLength < 0 ? -1 : offset + segmentLength;
        }

        return !name.IsEmpty || !fileName.IsEmpty;
    }

    private static long FindToken(ReadOnlySequence<byte> seq, ReadOnlySpan<byte> token)
    {
        var reader = new SequenceReader<byte>(seq);
        return reader.TryReadTo(out ReadOnlySequence<byte> _, token, advancePastDelimiter: false) ? reader.Consumed : -1;
    }

    /// <summary>
    /// Locates a parameter value starting at <paramref name="start"/>: the inside of a quoted-string,
    /// or a bare token with surrounding whitespace removed.
    /// </summary>
    private static bool TryGetParameterValue(ReadOnlySpan<byte> segment, int start, out int valueStart, out int valueLength)
    {
        while (start < segment.Length && segment[start] is (byte)' ' or (byte)'\t')
        {
            start++;
        }

        if (start < segment.Length && segment[start] == (byte)'"')
        {
            var close = IndexOfUnquoted(segment[(start + 1)..], (byte)'"', insideQuotes: true);
            if (close < 0)
            {
                valueStart = valueLength = 0;
                return false;
            }

            valueStart = start + 1;
            valueLength = close;
            return true;
        }

        var token = HttpParser.TrimOws(segment[start..]);
        valueStart = start;
        valueLength = token.Length;
        return valueLength > 0;
    }

    /// <summary>
    /// Index of <paramref name="value"/> outside quoted-strings (honouring backslash escapes), or -1.
    /// With <paramref name="insideQuotes"/> the scan starts inside a quoted-string, so the first
    /// unescaped quote is returned.
    /// </summary>
    private static int IndexOfUnquoted(ReadOnlySpan<byte> span, byte value, bool insideQuotes = false)
    {
        var quoted = insideQuotes;
        for (var i = 0; i < span.Length; i++)
        {
            var b = span[i];
            if (quoted && b == (byte)'\\')
            {
                i++;
                continue;
            }

            if (b == (byte)'"')
            {
                if (insideQuotes && value == (byte)'"')
                {
                    return i;
                }

                quoted = !quoted;
                continue;
            }

            if (!quoted && b == value)
            {
                return i;
            }
        }

        return -1;
    }
}
