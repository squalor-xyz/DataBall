using System;

namespace squalor.DataBall;

/// <summary>
/// Base exception for DataBall operations.
/// </summary>
public class DataBallException : Exception
{
    public DataBallException(string message) : base(message) { }
    public DataBallException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Exception thrown during import operations.
/// </summary>
public class ImportException : DataBallException
{
    public ImportException(string message) : base(message) { }
    public ImportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Exception thrown during export operations.
/// </summary>
public class ExportException : DataBallException
{
    public ExportException(string message) : base(message) { }
    public ExportException(string message, Exception inner) : base(message, inner) { }
}