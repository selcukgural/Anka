namespace Anka.Extensions;

/// <summary>
/// Provides extension methods for <see cref="HttpRequest"/> to enable additional functionality.
/// </summary>
internal static class HttpRequestExtensions
{
    /// <summary>
    /// Determines whether the size of the request body is within the specified maximum limit, based on
    /// the "Content-Length" header from the request. If no limit is specified, or if the header is not present
    /// or valid, the method assumes the size is acceptable.
    /// </summary>
    /// <param name="request">
    /// The HTTP request containing headers that specify the size of the body to validate.
    /// </param>
    /// <param name="maxRequestBodySize">
    /// The maximum allowable size for the request body, expressed as an integer in bytes. A null value
    /// indicates no limit is enforced.
    /// </param>
    /// <returns>
    /// Returns true if the size of the request body is within the specified limit, there is no limit set,
    /// or the "Content-Length" header is not provided. Returns false if the size exceeds the specified limit.
    /// </returns>
    public static bool IsRequestBodySizeWithinLimit(this HttpRequest request, int? maxRequestBodySize)
    {
        if(maxRequestBodySize is null)
        {
            return true;
        }

        if (request is { HasContentLength: true, HasInvalidContentLength: false })
        {
            return request.ContentLength <= maxRequestBodySize.Value;
        }
        
        return true;
    }
}
