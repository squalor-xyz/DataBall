using System;

namespace squalor.DataBall
{
    /// <summary>
    /// Represents errors that occur during DataBall operations.
    /// </summary>
    public class DataBallException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DataBallException"/> class with a specified error message.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public DataBallException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="DataBallException"/> class with a specified error message and a reference to the inner exception.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that is the cause of the current exception.</param>
        public DataBallException(string message, Exception innerException) : base(message, innerException) { }
    }
}