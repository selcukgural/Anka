namespace Anka;

/// <summary>
/// Represents HTTP methods used in requests. These methods describe the desired
/// action to be performed for a given resource in the HTTP protocol.
/// </summary>
public enum HttpMethod : byte
{
    Unknown = 0,
    Get,
    Post,
    Put,
    Delete,
    Head,
    Options,
    Patch,
    Trace,
    Connect
}