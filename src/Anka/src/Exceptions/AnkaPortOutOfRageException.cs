namespace Anka.Exceptions;

/// <summary>
/// Represents an exception that is thrown when a port number is outside the valid range of 1 to 65535.
/// </summary>
/// <remarks>
/// This exception is typically used to indicate an invalid port value within a context where ports are expected
/// to conform to the standard Internet port number range.
/// </remarks>
/// <example>
/// This exception might be thrown if a port value passed to a server configuration exceeds the allowable range.
/// </example>
/// <param name="paramName">The name of the parameter that caused the exception.</param>
/// <param name="message">A message describing the error.</param>
public sealed class AnkaPortOutOfRageException(string paramName, string message) : ArgumentOutOfRangeException(paramName, message);