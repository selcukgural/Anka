using BenchmarkDotNet.Attributes;

namespace Anka.Benchmark;

/// <summary>
/// Measures isolated chunked transfer-decoding cost independently from request-line/header parsing.
/// </summary>
[MemoryDiagnoser]
public class ChunkedBodyParserBenchmarks
{
    private byte[] _singleChunk = null!;
    private byte[] _multiChunk = null!;
    private byte[] _chunkedWithTrailers = null!;

    [GlobalSetup]
    public void Setup()
    {
        _singleChunk = "5\r\nhello\r\n0\r\n\r\n"u8.ToArray();
        _multiChunk = "5\r\nhello\r\n6\r\n world\r\n1\r\n!\r\n0\r\n\r\n"u8.ToArray();
        _chunkedWithTrailers =
            "5\r\nhello\r\n6\r\n world\r\n0\r\netag: \"abc\"\r\nx-request-id: 42\r\n\r\n"u8.ToArray();
    }

    [Benchmark(Baseline = true)]
    public int Decode_SingleChunk() => Decode(_singleChunk);

    [Benchmark]
    public int Decode_MultiChunk() => Decode(_multiChunk);

    [Benchmark]
    public int Decode_WithTrailers() => Decode(_chunkedWithTrailers);

    private static int Decode(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var totalBodyBytes = 0;

        while (true)
        {
            var sizeResult = ChunkedBodyParser.TryReadChunkSize(payload[offset..], out var chunkSize, out var sizeConsumed);
            if (sizeResult != ChunkedBodyParseResult.Success)
            {
                throw new InvalidOperationException($"Unexpected chunk-size parse result: {sizeResult}");
            }

            offset += sizeConsumed;
            if (chunkSize == 0)
            {
                var trailersResult = ChunkedBodyParser.TryConsumeTrailers(payload[offset..], out var trailersConsumed);
                if (trailersResult != ChunkedBodyParseResult.Success)
                {
                    throw new InvalidOperationException($"Unexpected trailer parse result: {trailersResult}");
                }

                offset += trailersConsumed;
                return totalBodyBytes + offset;
            }

            var dataResult = ChunkedBodyParser.TryConsumeChunkData(payload[offset..], chunkSize, out var dataConsumed);
            if (dataResult != ChunkedBodyParseResult.Success)
            {
                throw new InvalidOperationException($"Unexpected chunk-data parse result: {dataResult}");
            }

            totalBodyBytes += chunkSize;
            offset += dataConsumed;
        }
    }
}
