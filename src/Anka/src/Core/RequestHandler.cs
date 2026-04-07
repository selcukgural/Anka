namespace Anka;

/// <summary>
/// Represents a delegate that handles HTTP requests and generates appropriate responses.
/// </summary>
/// <param name="request">
/// An instance of <see cref="HttpRequest"/> representing the incoming HTTP request.
/// </param>
/// <param name="response">
/// An instance of <see cref="HttpResponseWriter"/> used to construct and write the HTTP response.
/// </param>
/// <param name="cancellationToken">
/// A <see cref="CancellationToken"/> used to observe cancellation requests during the processing of the HTTP request.
/// </param>
/// <returns>
/// A <see cref="ValueTask"/> representing the asynchronous operation of handling the request and generating a response.
/// </returns>
public delegate ValueTask RequestHandler(
    HttpRequest request,
    HttpResponseWriter response,
    CancellationToken cancellationToken);
    