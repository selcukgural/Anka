namespace Anka.Test;

public class ChunkedBodyParserTests
{
    [Fact]
    public void TryReadChunkSize_SimpleHex_ReturnsSizeAndConsumed()
    {
        var result = ChunkedBodyParser.TryReadChunkSize("A\r\n"u8, out var chunkSize, out var consumed);

        Assert.Equal(ChunkedBodyParseResult.Success, result);
        Assert.Equal(10, chunkSize);
        Assert.Equal(3, consumed);
    }

    [Fact]
    public void TryReadChunkSize_WithExtension_IgnoresExtension()
    {
        var result = ChunkedBodyParser.TryReadChunkSize("5;foo=bar\r\n"u8, out var chunkSize, out var consumed);

        Assert.Equal(ChunkedBodyParseResult.Success, result);
        Assert.Equal(5, chunkSize);
        Assert.Equal(11, consumed);
    }

    [Fact]
    public void TryReadChunkSize_IncompleteLine_ReturnsIncomplete()
    {
        var result = ChunkedBodyParser.TryReadChunkSize("5"u8, out var chunkSize, out var consumed);

        Assert.Equal(ChunkedBodyParseResult.Incomplete, result);
        Assert.Equal(0, chunkSize);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryReadChunkSize_InvalidHex_ReturnsInvalid()
    {
        var result = ChunkedBodyParser.TryReadChunkSize("Z\r\n"u8, out _, out _);
        Assert.Equal(ChunkedBodyParseResult.Invalid, result);
    }

    [Fact]
    public void TryReadChunkSize_Overflow_ReturnsInvalid()
    {
        var result = ChunkedBodyParser.TryReadChunkSize("FFFFFFFFF\r\n"u8, out _, out _);
        Assert.Equal(ChunkedBodyParseResult.Invalid, result);
    }

    [Fact]
    public void TryConsumeChunkData_ValidChunk_ReturnsConsumedLength()
    {
        var result = ChunkedBodyParser.TryConsumeChunkData("hello\r\n"u8, 5, out var consumed);

        Assert.Equal(ChunkedBodyParseResult.Success, result);
        Assert.Equal(7, consumed);
    }

    [Fact]
    public void TryConsumeChunkData_IncompleteChunk_ReturnsIncomplete()
    {
        var result = ChunkedBodyParser.TryConsumeChunkData("hello"u8, 5, out _);
        Assert.Equal(ChunkedBodyParseResult.Incomplete, result);
    }

    [Fact]
    public void TryConsumeChunkData_MissingChunkTerminator_ReturnsInvalid()
    {
        var result = ChunkedBodyParser.TryConsumeChunkData("helloXX"u8, 5, out _);
        Assert.Equal(ChunkedBodyParseResult.Invalid, result);
    }

    [Fact]
    public void TryConsumeTrailers_EmptyTrailerBlock_ReturnsSuccess()
    {
        var result = ChunkedBodyParser.TryConsumeTrailers("\r\n"u8, out var consumed);

        Assert.Equal(ChunkedBodyParseResult.Success, result);
        Assert.Equal(2, consumed);
    }

    [Fact]
    public void TryConsumeTrailers_MultipleTrailerLines_ReturnsSuccess()
    {
        var result = ChunkedBodyParser.TryConsumeTrailers("ETag: abc\r\nX-Test: ok\r\n\r\n"u8, out var consumed);

        Assert.Equal(ChunkedBodyParseResult.Success, result);
        Assert.Equal("ETag: abc\r\nX-Test: ok\r\n\r\n".Length, consumed);
    }

    [Fact]
    public void TryConsumeTrailers_InvalidLine_ReturnsInvalid()
    {
        var result = ChunkedBodyParser.TryConsumeTrailers("broken\r\n\r\n"u8, out _);
        Assert.Equal(ChunkedBodyParseResult.Invalid, result);
    }

    [Fact]
    public void TryConsumeTrailers_IncompleteLine_ReturnsIncomplete()
    {
        var result = ChunkedBodyParser.TryConsumeTrailers("ETag: abc\r\nX-Test"u8, out _);
        Assert.Equal(ChunkedBodyParseResult.Incomplete, result);
    }
}
