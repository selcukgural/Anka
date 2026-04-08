using System.Net.Sockets;
using System.Threading.Tasks.Sources;

namespace Anka;

/// <summary>
/// Zero-allocation socket receive wrapper built on <see cref="SocketAsyncEventArgs"/>.
///
/// Compared with <c>Socket.ReceiveAsync(Memory&lt;byte&gt; …)</c>:
/// <list type="bullet">
///   <item>When data is already in the kernel buffer, the fast synchronous path is taken —
///   no allocation, no thread-pool post.</item>
///   <item>When no data is available yet the OS kqueue/epoll completion thread fires
///   <see cref="SocketAsyncEventArgs.Completed"/> and resumes the caller via the
///   thread pool (<see cref="ManualResetValueTaskSourceCore{T}.RunContinuationsAsynchronously"/>
///   = <see langword="true"/>), keeping the OS I/O thread free to service other sockets.
///   This is the mechanism that allows Anka to scale at high connection counts: the
///   thread pool only sees request-processing work, not I/O waits.</item>
/// </list>
///
/// One instance should be created per connection and <see cref="Dispose"/>d when the
/// connection closes.  <see cref="ReceiveAsync"/> must not be called concurrently.
/// </summary>
internal sealed class SocketReceiver : IValueTaskSource<int>, IDisposable
{
    // RunContinuationsAsynchronously = true: when async I/O completes on an OS kqueue/epoll
    // thread, the continuation (request-processing code) is posted to the ThreadPool so the
    // I/O thread can immediately return to the OS event loop.  This allows multiple connections
    // to have their I/O completions handled concurrently at high connection counts.
    private ManualResetValueTaskSourceCore<int> _source = new() { RunContinuationsAsynchronously = true };

    private readonly SocketAsyncEventArgs _saea;

    /// <summary>
    /// Provides an implementation for receiving network data asynchronously using <see cref="Socket"/>.
    /// Utilizes <see cref="SocketAsyncEventArgs"/> for efficient asynchronous operations and manages its lifecycle.
    /// Implements <see cref="IValueTaskSource{T}"/> to enable custom handling of asynchronous task results.
    /// </summary>
    internal SocketReceiver()
    {
        _saea = new SocketAsyncEventArgs();
        _saea.Completed += OnCompleted;
    }

    /// <summary>
    /// Receives bytes from <paramref name="socket"/> into <paramref name="buffer"/>.
    /// Returns a <see cref="ValueTask{T}"/> that resolves to the number of bytes received,
    /// or 0 when the remote end closed the connection.
    /// </summary>
    public ValueTask<int> ReceiveAsync(Socket socket, Memory<byte> buffer)
    {
        _source.Reset();
        _saea.SetBuffer(buffer);

        if (!socket.ReceiveAsync(_saea))
        {
            // Synchronous completion — data was already in the kernel buffer.
            return _saea.SocketError == SocketError.Success
                ? new ValueTask<int>(_saea.BytesTransferred)
                : ValueTask.FromException<int>(new SocketException((int)_saea.SocketError));
        }

        // Asynchronous path — OnCompleted will fire on an OS I/O thread.
        return new ValueTask<int>(this, _source.Version);
    }

    /// <summary>
    /// Handles the completion event of a <see cref="SocketAsyncEventArgs"/> operation.
    /// Processes the result of the asynchronous socket operation and signals the associated task source with the outcome.
    /// </summary>
    /// <param name="_">Represents the sending object. Not used in this method, can be <see langword="null"/>.</param>
    /// <param name="e">The <see cref="SocketAsyncEventArgs"/> instance containing the result of the completed operation.
    /// If the operation completes successfully, its <see cref="SocketAsyncEventArgs.BytesTransferred"/> property specifies the number of bytes read.
    /// If the operation fails, its <see cref="SocketAsyncEventArgs.SocketError"/> property indicates the error.</param>
    private void OnCompleted(object? _, SocketAsyncEventArgs e)
    {
        if (e.SocketError == SocketError.Success)
        {
            _source.SetResult(e.BytesTransferred);
        }
        else
        {
            _source.SetException(new SocketException((int)e.SocketError));
        }
    }

    /// <summary>
    /// Retrieves the result of the asynchronous operation associated with the specified token
    /// and clears the state for reinvocation.
    /// </summary>
    /// <param name="token">
    /// A token that identifies the asynchronous operation. This is provided during the operation lifecycle
    /// to ensure the correct result is retrieved.
    /// </param>
    /// <returns>
    /// The result of the asynchronous operation, typically indicating the number of bytes received.
    /// </returns>
    int IValueTaskSource<int>.GetResult(short token)
        => _source.GetResult(token);

    /// <summary>
    /// Retrieves the current status of the asynchronous operation associated with the specified token.
    /// </summary>
    /// <param name="token">
    /// A short value representing the token that uniquely identifies the asynchronous operation
    /// whose status is being queried.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTaskSourceStatus"/> that indicates the current status of the asynchronous operation.
    /// The status can be one of the following:
    /// <list type="bullet">
    /// <item><see cref="ValueTaskSourceStatus.Pending"/>: The operation is not yet complete.</item>
    /// <item><see cref="ValueTaskSourceStatus.Succeeded"/>: The operation has completed successfully.</item>
    /// <item><see cref="ValueTaskSourceStatus.Faulted"/>: The operation has completed with an error.</item>
    /// <item><see cref="ValueTaskSourceStatus.Canceled"/>: The operation was canceled.</item>
    /// </list>
    /// </returns>
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token)
        => _source.GetStatus(token);

    /// <summary>
    /// Schedules a continuation action to be invoked upon the completion of the value task.
    /// Called by the async infrastructure to register a continuation for the operation represented by this <see cref="IValueTaskSource{T}"/>.
    /// Delegates the operation to <see cref="ManualResetValueTaskSourceCore{T}.OnCompleted"/>.
    /// </summary>
    /// <param name="continuation">The action to invoke after the operation completes.</param>
    /// <param name="state">A state object to be passed to the continuation action during invocation.</param>
    /// <param name="token">A token identifying the operation, used to verify correctness.</param>
    /// <param name="flags">Options for invoking the continuation, such as execution context flow.</param>
    void IValueTaskSource<int>.OnCompleted(
        Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _source.OnCompleted(continuation, state, token, flags);

    /// <summary>
    /// Releases the resources used by the <see cref="SocketReceiver"/> instance, including its underlying
    /// <see cref="SocketAsyncEventArgs"/> object. This method ensures that all unmanaged resources are properly
    /// released and the instance is rendered unusable after disposal.
    /// </summary>
    public void Dispose() => _saea.Dispose();
}
