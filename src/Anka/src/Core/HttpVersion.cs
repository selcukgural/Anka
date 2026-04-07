namespace Anka;

/// <summary>
/// Represents the HTTP version used in a request or response.
/// This enum defines commonly used HTTP versions such as 1.0 and 1.1,
/// as well as a placeholder for unknown or unsupported versions.
/// </summary>
public enum HttpVersion : byte
{
    Unknown = 0,
    Http10  = 1,
    Http11  = 2
}