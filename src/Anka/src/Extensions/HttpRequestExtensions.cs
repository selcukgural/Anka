namespace Anka.Extensions;

/// <summary>
/// Provides extension methods for <see cref="HttpRequest"/> to enable additional functionality.
/// </summary>
internal static class HttpRequestExtensions
{
    /// <summary>
    /// Validates the "Content-Length" header for specific HTTP methods (POST, PUT, PATCH) to ensure
    /// it complies with RFC 9110 §8.6. The method returns true if the validation passes or if the
    /// header is not applicable to the current request method.
    /// </summary>
    /// <param name="request">
    /// The HTTP request that contains method and header information to validate.
    /// </param>
    /// <returns>
    /// Returns true if the "Content-Length" header is valid or the request method is not
    /// subject to validation. Returns false if the "Content-Length" header is invalid.
    /// </returns>
    public static bool ValidateContentLengthFor411(this HttpRequest request)
    {
        if (request.Method is not HttpMethod.Post and not HttpMethod.Put and not HttpMethod.Patch)
        {
            return true;
        }
 
        // RFC 9110 §8.6)
        if (!request.Headers.TryGetValue(HttpHeaderNames.ContentLength, out var value))
        {
            return true;
        }
 
        return int.TryParse(value, out var length) && length >= 0;
    }
}