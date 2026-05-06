using System.Runtime.InteropServices;

namespace Anka;

/// <summary>
/// A specialized implementation of <see cref="Stream"/> that supports writing HTTP/1.1 chunked responses.
/// This class is optimized for reuse within a single HTTP connection to minimize resource allocations.
/// </summary>
public sealed class HttpResponseStream : Stream
{
    private bool _isStarted;
    private bool _isFinished;
    private bool _suppressBody;
    private List<HttpHeader>? _trailers;
    private HttpResponseWriter? _writer;
    private CancellationToken _cancellationToken;

    /// <summary>
    /// A specialized implementation of <see cref="Stream"/> that supports writing HTTP/1.1 chunked responses.
    /// This stream is designed for efficient reuse within an HTTP connection, minimizing resource allocations during its lifetime.
    /// </summary>
    internal HttpResponseStream() { }

    /// <summary>
    /// Initializes the stream for writing an HTTP response with the specified writer and cancellation token.
    /// </summary>
    /// <param name="writer">The <see cref="HttpResponseWriter"/> to be used for writing the response.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to observe cancellation requests.</param>
    /// <param name="suppressBody">When <c>true</c> (e.g. HEAD requests), headers are still sent but chunk data and the final terminating chunk are suppressed.</param>
    internal void Initialize(HttpResponseWriter writer, CancellationToken cancellationToken, bool suppressBody = false)
    {
        _writer = writer;
        _isStarted = false;
        _isFinished = false;
        _suppressBody = suppressBody;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Resets the internal state of the stream, allowing it to be reused for a new request.
    /// This method clears the associated writer, resets state flags, and cancels any existing cancellation tokens.
    /// </summary>
    internal void Reset()
    {
        _writer = null;
        _isStarted = false;
        _isFinished = false;
        _suppressBody = false;
        _cancellationToken = CancellationToken.None;
        _trailers?.Clear();
    }

    /// <summary>
    /// Gets a value indicating whether the current stream supports reading.
    /// This property always returns <see langword="false"/> as <see cref="HttpResponseStream"/>
    /// is a write-only stream designed for sending HTTP responses using chunked transfer encoding.
    /// </summary>
    public override bool CanRead => false;

    /// <summary>
    /// Gets a value indicating whether seeking is supported within the current stream.
    /// </summary>
    /// <remarks>
    /// The <see cref="HttpResponseStream"/> does not support seeking. As a result, this property always returns <c>false</c>.
    /// Attempting to perform seeking operations will throw a <see cref="NotSupportedException"/>.
    /// </remarks>
    public override bool CanSeek => false;

    /// <summary>
    /// Gets a value indicating whether this stream supports writing.
    /// This property always returns <c>true</c>, as <see cref="HttpResponseStream"/>
    /// is explicitly designed to enable writing HTTP responses.
    /// </summary>
    public override bool CanWrite => true;

    /// <summary>
    /// Gets the length of the stream. This property is not supported and always throws a <see cref="NotSupportedException"/> when accessed.
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown when attempting to get the length of the stream.</exception>
    public override long Length => throw new NotSupportedException();

    /// <summary>
    /// Gets or sets the position within the stream. This property is not supported for <see cref="HttpResponseStream"/>
    /// and will always throw a <see cref="NotSupportedException"/> when accessed or modified.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Always thrown when attempting to get or set this property, as seeking is not supported in <see cref="HttpResponseStream"/>.
    /// </exception>
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>
    /// Flushes any buffered data in the stream to the underlying HTTP response writer,
    /// ensuring that all updates are propagated to the client.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the stream has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the stream has not been properly initialized.</exception>
    public override void Flush() { }

    /// <summary>
    /// Flushes any buffered data asynchronously to the underlying HTTP response stream and monitors for cancellation requests.
    /// </summary>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to observe cancellation requests.</param>
    /// <return>A task that represents the asynchronous flush operation.</return>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Reads data from the stream into the specified buffer. This operation is not supported for <see cref="HttpResponseStream"/>.
    /// </summary>
    /// <param name="buffer">The array of bytes to store the data read from the stream. This parameter is ignored.</param>
    /// <param name="offset">The zero-based byte offset in the buffer at which to begin storing data. This parameter is ignored.</param>
    /// <param name="count">The maximum number of bytes to read. This parameter is ignored.</param>
    /// <returns>This method always throws <see cref="NotSupportedException"/>.</returns>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Attempts to set the position within the stream to the specified offset relative to the origin.
    /// This method is not supported in <see cref="HttpResponseStream"/>.
    /// </summary>
    /// <param name="offset">A byte offset relative to the <paramref name="origin"/> parameter.</param>
    /// <param name="origin">A value of type <see cref="SeekOrigin"/> indicating the reference point used to obtain the new position.</param>
    /// <returns>The new position within the stream. However, this method will always throw a <see cref="NotSupportedException"/> in <see cref="HttpResponseStream"/>.</returns>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>
    /// Sets the length of the current stream. This operation is not supported for <see cref="HttpResponseStream"/>.
    /// </summary>
    /// <param name="value">The desired length of the stream in bytes.</param>
    /// <exception cref="NotSupportedException">Thrown in all cases as the operation is not supported.</exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Writes a sequence of bytes to the stream using the specified byte array, offset, and count.
    /// </summary>
    /// <param name="buffer">The byte array that supplies the data to be written to the stream.</param>
    /// <param name="offset">The zero-based byte offset in the buffer at which to begin writing from.</param>
    /// <param name="count">The number of bytes to write to the stream starting from the offset.</param>
    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count), _cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Writes a chunk of data to the HTTP response stream asynchronously. If the response stream
    /// has not started, it will initialize the chunked response automatically before writing the data.
    /// </summary>
    /// <param name="buffer">The data to write, represented as a <see cref="ReadOnlyMemory{T}"/> of bytes.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask"/> that represents the asynchronous write operation.</returns>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        
        ObjectDisposedException.ThrowIf(_writer == null, typeof(HttpResponseStream));
        
        if (_isFinished)
        {
            throw new InvalidOperationException("Stream is already finished.");
        }

        // Hot path: headers already sent, directly forward to WriteChunkAsync (zero state-machine allocation).
        if (_isStarted)
        {
            return _suppressBody ? default : _writer.WriteChunkAsync(buffer, cancellationToken);
        }

        return WriteAsyncSlow(buffer, cancellationToken);
    }

    // Cold path: first write — send chunked headers, then the chunk.
    private async ValueTask WriteAsyncSlow(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await _writer!.StartChunkedResponseAsync(200, cancellationToken: cancellationToken);
        
        _isStarted = true;
        
        if (!_suppressBody)
        {
            await _writer.WriteChunkAsync(buffer, cancellationToken);
        }
    }

    /// <summary>
    /// Asynchronously writes a range of bytes from the specified buffer to the HTTP response stream.
    /// </summary>
    /// <param name="buffer">The buffer containing data to write to the stream.</param>
    /// <param name="offset">The zero-based byte offset in the buffer at which to begin writing data.</param>
    /// <param name="count">The number of bytes to write to the stream.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe for cancellation requests.</param>
    /// <returns>A <see cref="Task"/> that represents the asynchronous write operation.</returns>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var vt = WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        return vt.IsCompletedSuccessfully ? Task.CompletedTask : vt.AsTask();
    }

    /// <summary>
    /// Adds a trailer header to be sent at the end of the response.
    /// Only applicable for chunked responses.
    /// </summary>
    /// <param name="header">The trailer header to add.</param>
    public void AddTrailer(HttpHeader header)
    {
        _trailers ??= [];
        _trailers.Add(header);
    }

    /// <summary>
    /// Finalizes the chunked HTTP response by sending the terminating chunk.
    /// If the response was not started, it initializes the chunked response and then completes it.
    /// </summary>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe while waiting for the operation to complete.</param>
    /// <returns>A <see cref="ValueTask"/> that represents the asynchronous operation to finalize the HTTP response.</returns>
    public async ValueTask FinishAsync(CancellationToken cancellationToken = default)
    {
        if (_writer == null || _isFinished)
        {
            return;
        }

        if (!_isStarted)
        {
            await _writer.StartChunkedResponseAsync(200, cancellationToken: cancellationToken);
            _isStarted = true;
        }

        if (!_suppressBody)
        {
            if (_trailers is { Count: > 0 })
            {
                await _writer.FinishChunkedResponseAsync(CollectionsMarshal.AsSpan(_trailers), cancellationToken);
            }
            else
            {
                await _writer.FinishChunkedResponseAsync(default, cancellationToken);
            }
        }

        _isFinished = true;
    }

    /// <summary>
    /// Asynchronously releases the resources used by the <see cref="HttpResponseStream"/>
    /// and finalizes the chunked HTTP response by ensuring the terminating chunk is sent.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> that represents the asynchronous disposal operation.</returns>
    public override ValueTask DisposeAsync() => FinishAsync(_cancellationToken);
}
