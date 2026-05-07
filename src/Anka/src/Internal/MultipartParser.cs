using System.Buffers;

namespace Anka.Internal;

/// <summary>
/// A zero-allocation parser for multipart/form-data bodies.
/// Designed to work with <see cref="ReadOnlySequence{T}"/> to avoid large buffer copies.
/// </summary>
public ref struct MultipartParser
{
    private SequenceReader<byte> _reader;
    private readonly byte[] _boundary;
    private bool _finished;

    public MultipartParser(ReadOnlySequence<byte> body, ReadOnlySpan<byte> boundary)
    {
        _reader = new SequenceReader<byte>(body);
        // boundary in the body is prefixed with "--"
        _boundary = new byte[boundary.Length + 2];
        _boundary[0] = (byte)'-';
        _boundary[1] = (byte)'-';
        boundary.CopyTo(_boundary.AsSpan(2));
        _finished = false;
    }

    public bool TryReadNextPart(out MultipartPart part)
    {
        part = default;
        if (_finished) return false;

        // RFC 7578: parts are separated by boundary.
        // First boundary might be preceded by preamble (ignored).
        if (!_reader.TryReadTo(out ReadOnlySequence<byte> _, _boundary, advancePastDelimiter: true))
        {
            _finished = true;
            return false;
        }

        // Check if it's the end boundary (boundary + "--")
        if (_reader.Remaining >= 2 && _reader.IsNext("--"u8, advancePast: true))
        {
            _finished = true;
            return false;
        }

        // Must be followed by CRLF
        if (!_reader.TryReadTo(out ReadOnlySequence<byte> _, "\r\n"u8, advancePastDelimiter: true))
        {
            return false;
        }

        // Now we are at the start of part headers.
        // Headers end with double CRLF.
        var headersStart = _reader.Position;
        if (!_reader.TryReadTo(out ReadOnlySequence<byte> _, "\r\n\r\n"u8, advancePastDelimiter: false))
        {
            return false;
        }
        var headersEnd = _reader.Position;
        var headersSeq = _reader.Sequence.Slice(headersStart, headersEnd);
        _reader.Advance(4); // Skip \r\n\r\n

        // Content of the part ends at the next boundary.
        var remaining = _reader.UnreadSequence;
        var boundaryIndex = FindBoundary(remaining, _boundary);
        
        if (boundaryIndex == -1)
        {
            return false;
        }

        var contentSeq = remaining.Slice(0, boundaryIndex - 2); // -2 to remove CRLF before boundary
        _reader.Advance(boundaryIndex); // Position is now at the start of the boundary

        part = new MultipartPart(headersSeq, contentSeq);
        return true;
    }

    private static long FindBoundary(ReadOnlySequence<byte> seq, ReadOnlySpan<byte> boundary)
    {
        var reader = new SequenceReader<byte>(seq);
        if (reader.TryReadTo(out ReadOnlySequence<byte> _, boundary, advancePastDelimiter: false))
        {
            return reader.Consumed;
        }
        return -1;
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

    private static bool TryParseContentDisposition(ReadOnlySequence<byte> line, out ReadOnlySequence<byte> name, out ReadOnlySequence<byte> fileName)
    {
        name = default;
        fileName = default;

        var nameIdx = FindToken(line, "name="u8);
        if (nameIdx != -1)
        {
            name = ExtractQuotedValue(line.Slice(nameIdx + 5));
        }

        var fileIdx = FindToken(line, "filename="u8);
        if (fileIdx != -1)
        {
            fileName = ExtractQuotedValue(line.Slice(fileIdx + 9));
        }

        return !name.IsEmpty || !fileName.IsEmpty;
    }

    private static long FindToken(ReadOnlySequence<byte> seq, ReadOnlySpan<byte> token)
    {
        var reader = new SequenceReader<byte>(seq);
        if (reader.TryReadTo(out ReadOnlySequence<byte> _, token, advancePastDelimiter: false))
        {
            return reader.Consumed;
        }
        return -1;
    }

    private static ReadOnlySequence<byte> ExtractQuotedValue(ReadOnlySequence<byte> seq)
    {
        var reader = new SequenceReader<byte>(seq);
        if (reader.TryAdvanceTo((byte)'"', advancePastDelimiter: true))
        {
            var start = reader.Position;
            if (reader.TryAdvanceTo((byte)'"', advancePastDelimiter: false))
            {
                var end = reader.Position;
                return seq.Slice(start, end);
            }
        }
        return default;
    }
}
