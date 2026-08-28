namespace squalor.DataBall
{
    /// <summary>
    /// Options for <see cref="DataBall.ImportAsync"/>.
    /// </summary>
    public sealed class ImportOptions
    {
        /// <summary>
        /// Gets a value indicating whether imported rows are appended.
        /// Defaults to <c>false</c> (replace existing data).
        /// </summary>
        public bool Append { get; init; }

        /// <summary>
        /// Gets the SQLite table to read. Defaults to <c>data</c> when unset.
        /// </summary>
        public string? TableName { get; init; }
    }
}
