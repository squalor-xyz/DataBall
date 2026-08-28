namespace squalor.DataBall
{
    /// <summary>
    /// Options for <see cref="DataBall.ExportAsync"/>.
    /// </summary>
    public sealed class ExportOptions
    {
        /// <summary>
        /// Gets the SQLite table to write. Defaults to <c>data</c> when unset.
        /// </summary>
        public string? TableName { get; init; }
    }
}
