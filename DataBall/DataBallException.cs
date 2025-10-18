using System;

namespace squalor.DataBall;

/// <summary>
/// Represents an exception specific to DataBall operations.
/// </summary>
public class DataBallException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DataBallException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="inner">The exception that is the cause of the current exception, or null if none.</param>
    public DataBallException(string message, Exception? inner = null) : base(message, inner) { }
}