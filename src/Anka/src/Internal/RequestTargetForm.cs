namespace Anka;

/// <summary>
/// Represents a request target form containing only the authority component of a URI.
/// </summary>
/// <remarks>
/// The <see cref="Authority"/> target form is typically used in HTTP CONNECT requests,
/// where the target is specified as a hostname and optional port without a scheme or path.
/// </remarks>
internal enum RequestTargetForm : byte
{
    /// <summary>
    /// Represents the origin form of a request target as specified by the HTTP/1.1 protocol.
    /// </summary>
    /// <remarks>
    /// The origin form is the most common form of a request target in HTTP. It consists of a path and optional
    /// query string (e.g., "/path?query=value"). This form is primarily used in requests outside the CONNECT
    /// method and OPTIONS method with "*".
    /// </remarks>
    Origin = 0,

    /// <summary>
    /// Represents the absolute form of a request target as specified by the HTTP/1.1 protocol.
    /// </summary>
    /// <remarks>
    /// The absolute form is used when the full URI is provided as the request target.
    /// This form includes the scheme, host, and optional path and query components (e.g., "https://example.com/path?query=value").
    /// It is typically used in requests sent to proxies or intermediaries.
    /// </remarks>
    Absolute = 1,

    /// <summary>
    /// Represents the request-target form where the request target is the asterisk '*' character.
    /// </summary>
    /// <remarks>
    /// The asterisk form is used as the request target in HTTP requests,
    /// typically with the OPTIONS method, to apply the request to the entire server
    /// rather than a specific resource.
    /// </remarks>
    Authority = 2,

    /// <summary>
    /// Represents the asterisk form of a request target as specified by the HTTP/1.1 protocol.
    /// </summary>
    /// <remarks>
    /// The asterisk form is used with the OPTIONS method to represent a request that does not
    /// target a specific resource but instead applies to the server as a whole.
    /// This form consists of a single asterisk character ("*"), and it is not combined
    /// with a URI or path.
    /// </remarks>
    Asterisk = 3
}

/// <summary>
/// Represents the scheme type for requests that use the absolute-form of the target in HTTP parsing.
/// </summary>
/// <remarks>
/// The <see cref="AbsoluteFormScheme"/> enumeration is used internally to differentiate between various
/// schemes in absolute-form request URIs, such as "http", "https", or other custom schemes. This distinction
/// aids in the proper handling and parsing of HTTP requests that include a fully qualified URI.
/// </remarks>
internal enum AbsoluteFormScheme : byte
{
    /// <summary>
    /// Represents the absence of a scheme in the absolute-form of a request URI.
    /// </summary>
    /// <remarks>
    /// This value indicates that no specific scheme has been identified or assigned
    /// when processing the request target in absolute-form. It is typically used
    /// as a default or uninitialized state before a scheme is determined.
    /// </remarks>
    None = 0,

    /// <summary>
    /// Represents the "http" scheme in the absolute-form of a request target as specified by the HTTP/1.1 protocol.
    /// </summary>
    /// <remarks>
    /// This enum member is used to indicate that the request URI uses the "http" scheme when the absolute-form
    /// is specified in the request target. The absolute-form includes a fully qualified URI that consists of
    /// the scheme, host, and path (e.g., "http://example.com/path"). This is commonly used in proxy requests.
    /// </remarks>
    Http = 1,

    /// <summary>
    /// Represents the "https" scheme type for requests that use the absolute-form of the target in HTTP parsing.
    /// </summary>
    /// <remarks>
    /// The "https" scheme is used to indicate secure communication over the HTTP protocol using TLS/SSL.
    /// This is part of the <see cref="AbsoluteFormScheme"/> enumeration and is relevant in scenarios where
    /// fully qualified URIs with the "https" scheme are present in the absolute-form of request targets.
    /// </remarks>
    Https = 2,

    /// <summary>
    /// Represents a custom or non-standard URI scheme in the absolute-form
    /// of a request target. This value is used when the scheme specified
    /// in the absolute-form target does not match predefined schemes such as
    /// HTTP or HTTPS.
    /// </summary>
    Other = 3
}
