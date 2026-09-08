using System.Collections.Generic;

namespace squalor.DataBall
{
    /// <summary>
    /// Column predicates and optional projection on the session <c>data</c> table.
    /// Pushed down to DuckDB. <c>Query(sql)</c> is the unfiltered escape hatch.
    /// </summary>
    public sealed class SessionFilter
    {
        public IReadOnlyList<ColumnPredicate> Predicates { get; init; } = [];

        /// <summary>
        /// Columns to materialize. Null means all columns.
        /// </summary>
        public IReadOnlyList<string>? Columns { get; init; }
    }
}
