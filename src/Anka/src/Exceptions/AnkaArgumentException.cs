namespace Anka.Exceptions;

/// <summary>
/// Represents an exception that is thrown when an argument provided to a method in
/// the Anka library is invalid or does not meet the defined requirements.
/// </summary>
/// <remarks>
/// This exception is a specialized form of <see cref="ArgumentException"/> used
/// specifically within the Anka library to indicate argument-related errors.
/// </remarks>
public sealed class AnkaArgumentException(string message,string paramName) : ArgumentException(message, paramName);