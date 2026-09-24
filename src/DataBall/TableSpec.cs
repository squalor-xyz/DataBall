// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;

namespace squalor.DataBall
{
    /// <summary>
    /// One physical table in a config-declared layout (overlay section <c>tables</c>).
    /// Kinds: <c>master</c> (one root dimension), <c>dimension</c> (normalized repeating
    /// group with a surrogate key), <c>rows</c> (the spine; one per layout, implied when
    /// absent), and <c>measurements</c> (column group stored without all-null rows).
    /// The wide relation <c>"data"</c> stays available as a view over these tables.
    /// </summary>
    public sealed class TableSpec
    {
        /// <summary>
        /// Gets or sets the kind: <c>master</c>, <c>dimension</c>, <c>rows</c>, or <c>measurements</c>.
        /// </summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets canonical column names claimed explicitly. Explicit claims win over <see cref="Roles"/>.
        /// </summary>
        public List<string> Columns { get; set; } = new();

        /// <summary>
        /// Gets or sets role selectors (<c>identity</c>, <c>stimulus</c>, <c>meas</c>, <c>classification</c>)
        /// that bind columns nobody claimed explicitly.
        /// </summary>
        public List<string> Roles { get; set; } = new();

        /// <summary>
        /// Gets or sets the key columns of a dimension. Default is every column of the table.
        /// </summary>
        public List<string> Key { get; set; } = new();

        /// <summary>
        /// Gets or sets the parent dimension (snowflake). Reserved; not supported yet.
        /// </summary>
        public string? Parent { get; set; }
    }
}
