using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace squalor.DataBall
{
    /// <summary>
    /// Vendor or custom format parser registered with <see cref="DataBall.RegisterHandler"/>.
    /// Core generic CSV / Parquet / archive / <c>.ball</c> import does not use this interface.
    /// Lab plugins (custom CSV, STDF, Touchstone, production) implement this and live in
    /// <c>DataBall.Handlers</c>, not <c>DuckDbStore</c>.
    /// </summary>
    public interface IFormatHandler
    {
        /// <summary>
        /// Returns whether this handler should parse <paramref name="path"/>.
        /// <paramref name="sniff"/> is the file stream at position 0; do not dispose it.
        /// </summary>
        bool CanHandle(string path, Stream sniff);

        /// <summary>
        /// Yields canonical session rows (column name → value) for <paramref name="path"/>.
        /// </summary>
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(
            string path,
            CancellationToken cancellationToken = default);
    }
}
