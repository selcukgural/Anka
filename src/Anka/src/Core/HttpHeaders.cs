using System.Runtime.CompilerServices;

namespace Anka;

/// <summary>
/// Represents a collection of HTTP headers designed for zero-allocation storage and manipulation.
/// Header names are normalized to lowercase upon insertion, enabling case-insensitive lookups using a
/// plain sequence comparison without additional case-folding operations during retrieval.
/// </summary>
public struct HttpHeaders
{
    /// <summary>
    /// Represents the maximum number of entries that can be stored in the HTTP header collection.
    /// </summary>
    private const int MaxEntries = 64;

    /// <summary>
    /// Inline array structure used for storing metadata of HTTP header entries inside the HttpHeaders struct.
    /// This enables efficient, zero-allocation storage and lookup of headers while maintaining maximum
    /// entry capacity constraints within the struct boundary.
    /// </summary>
    [InlineArray(MaxEntries)]
    private struct EntryArray
    {
        /// <summary>
        /// Represents a storage field within the inline array structure
        /// for header entries in the <see cref="HttpHeaders"/> class.
        /// </summary>
        private HeaderEntry _;
    }

    /// <summary>
    /// Represents an entry in the HTTP header collection, storing the offsets and lengths
    /// of the header name and value within a shared byte buffer. This structure facilitates
    /// zero-allocation storage and retrieval of header information.
    /// </summary>
    private readonly struct HeaderEntry(
        ushort nameOffset, ushort nameLength,
        ushort valueOffset, ushort valueLength)
    {
        /// <summary>
        /// Represents the offset within the underlying buffer where an HTTP header name begins.
        /// This offset points to the starting byte of the header name, stored in a zero-allocation
        /// HTTP header collection. Used for efficient access to header names without additional parsing.
        /// </summary>
        public readonly ushort NameOffset  = nameOffset;

        /// <summary>
        /// Represents the length of an HTTP header name in bytes within the internal buffer.
        /// This value is used in conjunction with <see cref="NameOffset"/> to locate
        /// and validate header names in the buffer during operations such as lookups or additions.
        /// </summary>
        public readonly ushort NameLength  = nameLength;

        /// <summary>
        /// Represents the offset, within the internal byte buffer, where the header value associated
        /// with a specific HTTP header begins. The <c>ValueOffset</c> is used in conjunction with
        /// <c>ValueLength</c> to extract the value of the header stored within the buffer.
        /// </summary>
        public readonly ushort ValueOffset = valueOffset;

        /// <summary>
        /// Represents the length of the HTTP header value in bytes.
        /// </summary>
        /// <remarks>
        /// This field is part of the <c>HeaderEntry</c> structure, which defines
        /// the metadata for an individual HTTP header within the <c>HttpHeaders</c> collection.
        /// The <c>ValueLength</c> identifies the size of the header value within
        /// the internal buffer so that the appropriate section can be accessed or parsed.
        /// </remarks>
        public readonly ushort ValueLength = valueLength;
    }
    
    /// <summary>
    /// The byte array that serves as the backing buffer for storing HTTP header names and values.
    /// This buffer is shared and rented by the parent HTTP request, with no internal allocation
    /// or return handled within this structure. It provides a storage mechanism for the
    /// header data using a zero-allocation approach.
    /// </summary>
    private byte[]    _buf;

    /// <summary>
    /// Tracks the current number of HTTP header entries stored in the collection.
    /// </summary>
    /// <remarks>
    /// This variable represents the count of headers currently added to the
    /// <see cref="HttpHeaders"/> instance. It is incremented when a new header is
    /// successfully added and is capped at a predefined maximum number of entries
    /// to prevent excessive storage.
    /// </remarks>
    private int       _count;

    /// <summary>
    /// Tracks the current writing position within the backing buffer for HTTP header storage.
    /// Used to allocate space for header names and values as they are added to the buffer.
    /// The value represents the next available byte offset for writing data.
    /// </summary>
    private int       _writePos;
    private int       _limit;

    /// <summary>
    /// Represents an internal array used to store header entries in the <see cref="HttpHeaders"/> struct.
    /// This field acts as the backing store for managing HTTP header name-value pairs efficiently.
    /// </summary>
    private EntryArray _entries;

    /// <summary>Initializes the internal buffer for the HTTP headers, setting up the shared byte array and starting offset.</summary>
    /// <param name="sharedBuffer">The shared buffer to store the header data.</param>
    /// <param name="startOffset">The starting position within the buffer for writing header data.</param>
    internal void InitBuffer(byte[] sharedBuffer, int startOffset)
        => InitBuffer(sharedBuffer, startOffset, sharedBuffer.Length - startOffset);

    internal void InitBuffer(byte[] sharedBuffer, int startOffset, int maxBytes)
    {
        _buf      = sharedBuffer;
        _writePos = startOffset;
        _limit    = Math.Min(sharedBuffer.Length, startOffset + maxBytes);
        _count    = 0;
    }

    /// <summary>
    /// Adds a header to the collection, storing the name in lowercase and the value verbatim.
    /// </summary>
    /// <param name="name">The header name to add. Must be in lowercase or convertible to lowercase ASCII.</param>
    /// <param name="value">The header value to associate with the name.</param>
    /// <returns><see langword="true"/> when the header was stored; otherwise, <see langword="false"/>.</returns>
    internal bool Add(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        if (_count >= MaxEntries)
        {
            return false;
        }

        var needed = name.Length + value.Length;
        if (_writePos + needed > _limit)
        {
            return false;
        }

        // Store name in lowercase
        var nameOffset = (ushort)_writePos;
        AsciiToLower(name, _buf.AsSpan(_writePos, name.Length));
        _writePos += name.Length;

        // Store value verbatim
        var valueOffset = (ushort)_writePos;
        value.CopyTo(_buf.AsSpan(_writePos, value.Length));
        _writePos += value.Length;

        _entries[_count++] = new HeaderEntry(
            nameOffset,  (ushort)name.Length,
            valueOffset, (ushort)value.Length);

        return true;
    }

    /// <summary>
    /// Gets the final writing position within the buffer maintained by the <see cref="HttpHeaders" /> structure.
    /// Represents the offset indicating where data writing has completed, useful for tracking the end of
    /// the allocated region in the underlying buffer after all header entries have been added.
    /// </summary>
    internal int FinalOffset => _writePos;

    /// <summary>
    /// Gets the total number of HTTP headers currently stored in the collection.
    /// </summary>
    /// <remarks>
    /// The <c>Count</c> property represents the number of headers added to the collection.
    /// It is updated whenever a header is added or removed. The maximum number of headers
    /// that can be stored is determined by the underlying implementation.
    /// </remarks>
    public int Count => _count;

    /// <summary>
    /// Represents all values stored for a repeated header name.
    /// </summary>
    public readonly ref struct HeaderValues
    {
        private readonly HttpHeaders _headers;
        private readonly ReadOnlySpan<byte> _lowercaseName;

        internal HeaderValues(HttpHeaders headers, ReadOnlySpan<byte> lowercaseName)
        {
            _headers        = headers;
            _lowercaseName  = lowercaseName;
        }

        public Enumerator GetEnumerator() => new(_headers, _lowercaseName);

        public ref struct Enumerator
        {
            private readonly HttpHeaders _headers;
            private readonly ReadOnlySpan<byte> _lowercaseName;
            private int _index;

            internal Enumerator(HttpHeaders headers, ReadOnlySpan<byte> lowercaseName)
            {
                _headers = headers;
                _lowercaseName = lowercaseName;
                _index = -1;
                Current = default;
            }

            public ReadOnlySpan<byte> Current { get; private set; }

            public bool MoveNext()
            {
                var buf = _headers._buf.AsSpan();
                while (++_index < _headers._count)
                {
                    var entry = _headers._entries[_index];
                    if (entry.NameLength != _lowercaseName.Length ||
                        !buf.Slice(entry.NameOffset, entry.NameLength).SequenceEqual(_lowercaseName))
                    {
                        continue;
                    }

                    Current = buf.Slice(entry.ValueOffset, entry.ValueLength);
                    return true;
                }

                Current = default;
                return false;
            }
        }
    }

    /// <summary>
    /// Attempts to retrieve all values stored for a repeated header name without allocating.
    /// </summary>
    public bool TryGetAllValues(ReadOnlySpan<byte> lowercaseName, out HeaderValues values)
    {
        values = new HeaderValues(this, lowercaseName);
        var buf = _buf.AsSpan();
        for (var i = 0; i < _count; i++)
        {
            var entry = _entries[i];
            if (entry.NameLength == lowercaseName.Length &&
                buf.Slice(entry.NameOffset, entry.NameLength).SequenceEqual(lowercaseName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Attempts to retrieve the value of a header by its name without allocating memory. <paramref name="lowercaseName"/> must already be lowercase.</summary>
    /// <param name="lowercaseName">The name of the header to look up, in lowercase.</param>
    /// <param name="value">When the method returns, contains the value of the header if it was found; otherwise, contains the default value.</param>
    /// <returns><c>true</c> if the header was found; otherwise, <c>false</c>.</returns>
    public bool TryGetValue(ReadOnlySpan<byte> lowercaseName, out ReadOnlySpan<byte> value)
    {
        var buf = _buf.AsSpan();
        for (var i = 0; i < _count; i++)
        {
            var entry = _entries[i];

            if (entry.NameLength != lowercaseName.Length ||
                !buf.Slice(entry.NameOffset, entry.NameLength).SequenceEqual(lowercaseName))
            {
                continue;
            }

            value = buf.Slice(entry.ValueOffset, entry.ValueLength);
            return true;
        }
        
        value = default;
        return false;
    }

    /// <summary>
    /// Performs a case-insensitive lookup of a header value by its name.
    /// This method uses a stack-allocated buffer for the name bytes, ensuring no heap allocation
    /// during the operation. The provided name is normalized to lowercase for comparison.
    /// </summary>
    /// <param name="name">The case-insensitive name of the header to retrieve.</param>
    /// <param name="value">When this method returns, contains the value of the header if found, or the default value if the header does not exist.</param>
    /// <returns>
    /// <see langword="true"/> if a header with the specified name exists; otherwise, <see langword="false"/>.
    /// </returns>
    public bool TryGetValue(string name, out ReadOnlySpan<byte> value)
    {
        if (name.Length > 128)
        {
            value = default; 
            return false; 
        }

        // Inline the search so the stackalloc span never escapes this frame.
        Span<byte> nameBytes = stackalloc byte[name.Length];
        AsciiToLower(name, nameBytes);

        var buf = _buf.AsSpan();
        for (var i = 0; i < _count; i++)
        {
            var entry = _entries[i];

            if (entry.NameLength != nameBytes.Length ||
                !buf.Slice(entry.NameOffset, entry.NameLength).SequenceEqual(nameBytes))
            {
                continue;
            }

            value = buf.Slice(entry.ValueOffset, entry.ValueLength);
            return true;
        }
        
        value = default;
        return false;
    }

    /// <summary>
    /// Converts ASCII characters in the source span to lowercase and writes them to the destination span.
    /// </summary>
    /// <param name="src">The source span containing ASCII characters to be converted to lowercase.</param>
    /// <param name="dst">
    /// The destination span where the lowercase ASCII characters will be written.
    /// The length of this span must be at least equal to the length of the source span.
    /// </param>
    private static void AsciiToLower(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        for (var i = 0; i < src.Length; i++)
        {
            var b = src[i];
            dst[i] = (uint)(b - 'A') <= 'Z' - 'A' ? (byte)(b | 0x20) : b;
        }
    }

    /// <summary>
    /// Converts the characters in the source string to lowercase and writes the result
    /// into the destination span as ASCII bytes.
    /// </summary>
    /// <param name="src">The source string to convert to lowercase.</param>
    /// <param name="dst">The span where the lowercase ASCII bytes will be written.</param>
    private static void AsciiToLower(string src, Span<byte> dst)
    {
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            dst[i] = (uint)(c - 'A') <= 'Z' - 'A' ? (byte)(c | 0x20) : (byte)c;
        }
    }
}
