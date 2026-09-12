// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;

namespace squalor.DataBall
{
    /// <summary>
    /// CSV-specific Open/import settings.
    /// </summary>
    public sealed class CsvConfig
    {
        /// <summary>
        /// Gets or sets header patterns tried in order, e.g. <c>{name}({unit})</c>.
        /// </summary>
        public List<string> HeaderPatterns { get; set; } = new();
    }
}
