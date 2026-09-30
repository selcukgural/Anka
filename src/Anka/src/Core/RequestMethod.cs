namespace Anka;

/// <summary>
/// Represents HTTP methods used in requests. These methods describe the desired
/// action to be performed for a given resource in the HTTP protocol.
/// </summary>
/// <remarks>
/// Named <c>RequestMethod</c> rather than <c>HttpMethod</c> so it does not collide with
/// <see cref="System.Net.Http.HttpMethod"/>, which <c>ImplicitUsings</c> imports into every console project.
/// </remarks>
public enum RequestMethod : byte
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