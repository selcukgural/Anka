using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;

namespace Anka.Benchmark;

/// <summary>
/// Measures chunk-encoding throughput and allocation for the chunked response writer API.
/// A loopback socket pair is used; a background drain task keeps the send buffer free so
/// timings reflect chunk-header formatting + kernel handoff, not socket backpressure.
/// </summary>
[MemoryDiagnoser]
public class HttpResponseStreamBenchmarks
{
    private Socket _sendSocket = null!;
    private Socket _recvSocket = null!;
    private HttpResponseWriter _writer = null!;
    private HttpResponseStream _stream = null!;
    private CancellationTokenSource _cts = null!;
    private Task _drainTask = null!;

    // Pre-allocated chunk payloads — never re-allocated between benchmark iterations.
    private static readonly ReadOnlyMemory<byte> SmallChunk  = new byte[128];
    private static readonly ReadOnlyMemory<byte> MediumChunk = new byte[4096];

    [GlobalSetup]
    public void Setup()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        _sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _sendSocket.NoDelay = true;
        _sendSocket.Connect((IPEndPoint)listener.LocalEndPoint!);
        _recvSocket = listener.Accept();

        _writer = new HttpResponseWriter(_sendSocket);

        // Background drain: discard all received bytes so the kernel send buffer never fills.
        var drainBuf = new byte[64 * 1024];
        _cts = new CancellationTokenSource();
        _drainTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try   { await _recvSocket.ReceiveAsync(drainBuf, _cts.Token); }
                catch { break; }
            }
        });

        // First WriteAsync call goes through WriteAsyncSlow (sends chunked headers, sets _isStarted = true).
        // The benchmarks below all enter after this — they take the zero-alloc hot path.
        _stream = (HttpResponseStream)_writer.GetStream();
        _stream.WriteAsync(SmallChunk).AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cts.Cancel();
        try { _drainTask.GetAwaiter().GetResult(); } catch { }
        _writer.Dispose();
        _sendSocket.Dispose();
        _recvSocket.Dispose();
        _cts.Dispose();
    }

    /// <summary>WriteChunkAsync — 128-byte chunk: raw chunk-header formatting + socket send. Baseline.</summary>
    [Benchmark(Baseline = true)]
    public ValueTask WriteChunkAsync_Small() => _writer.WriteChunkAsync(SmallChunk);

    /// <summary>WriteChunkAsync — 4 KB chunk.</summary>
    [Benchmark]
    public ValueTask WriteChunkAsync_Medium() => _writer.WriteChunkAsync(MediumChunk);

    /// <summary>
    /// Stream.WriteAsync warm path — 128-byte chunk.
    /// After setup, <c>_isStarted == true</c>, so this call forwards directly to
    /// <c>WriteChunkAsync</c> with no state-machine allocation.
    /// Expected allocation: 0 B (matches WriteChunkAsync_Small).
    /// </summary>
    [Benchmark]
    public ValueTask Stream_WriteAsync_Small() => _stream.WriteAsync(SmallChunk);

    /// <summary>Stream.WriteAsync warm path — 4 KB chunk.</summary>
    [Benchmark]
    public ValueTask Stream_WriteAsync_Medium() => _stream.WriteAsync(MediumChunk);

    /// <summary>
    /// StartChunkedResponseAsync — chunked response header formatting + send.
    /// Cold path: called once at the start of each streaming response.
    /// </summary>
    [Benchmark]
    public ValueTask StartChunkedResponse() => _writer.StartChunkedResponseAsync(200);
}
