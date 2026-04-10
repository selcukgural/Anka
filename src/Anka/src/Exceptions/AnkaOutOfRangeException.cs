namespace Anka.Exceptions;

/// <summary>
/// Represents an exception thrown when a value is outside the allowable range of values.
/// </summary>
/// <remarks>
/// This exception is specifically used in the <c>Anka</c> namespace to indicate that a supplied
/// parameter falls outside the defined constraints for its range.
/// Common scenarios where this exception can be encountered include:
/// - Specifying a server port outside the valid range of 1 to 65535.
/// - Setting an unsupported value for configuration properties, such as <c>MaxRequestBodySize</c>.
/// The exception provides details on the parameter name and the expected range or constraint
/// that was violated for better debugging and error handling.
/// </remarks>
/// <example>
/// <para>
/// This exception is typically thrown by specific methods or constructors when input validation
/// fails. For instance, it might be thrown in the <c>SetMaxRequestBodySize</c> method or the
/// <c>Server</c> constructor within the <c>Anka</c> namespace.
/// </para>
/// </example>
public sealed class AnkaOutOfRangeException(string paramName, string message) : ArgumentOutOfRangeException(paramName, message);