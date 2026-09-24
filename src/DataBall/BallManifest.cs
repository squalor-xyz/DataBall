// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;
using System.Text.Json;

namespace squalor.DataBall
{
    /// <summary>
    /// <c>manifest.json</c> inside a <c>.ball</c> v2: the wide column order and the physical
    /// tables stored under <c>tables/&lt;name&gt;.parquet</c>. Absent in v1 balls.
    /// </summary>
    public sealed class BallManifest
    {
        internal const string FileName = "manifest.json";

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>Gets or sets the format version. 2 = per-table parquet plus wide <c>data.parquet</c>.</summary>
        public int BallVersion { get; set; }

        /// <summary>Gets or sets the wide (view) columns in order.</summary>
        public List<string> Columns { get; set; } = new();

        /// <summary>Gets or sets the physical table names (each has <c>tables/&lt;name&gt;.parquet</c>).</summary>
        public List<string> Tables { get; set; } = new();
    }
}
