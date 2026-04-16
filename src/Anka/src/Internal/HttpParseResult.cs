namespace Anka;

/// <summary>
/// Represents the result of parsing an HTTP request.
/// </summary>
internal enum HttpParseResult
{
    Success = 0,
    Incomplete = 1,
    Invalid = 2,
    RequestTargetTooLong = 3,
    HeaderFieldsTooLarge = 4,
    HttpVersionNotSupported = 5
}
