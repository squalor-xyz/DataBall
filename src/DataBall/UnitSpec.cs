// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;

namespace squalor.DataBall
{
    /// <summary>
    /// Maps a physical unit (and aliases) to a DataBall column type name.
    /// </summary>
    public sealed class UnitSpec
    {
        /// <summary>
        /// Gets or sets the type name (<c>double</c>, <c>long</c>, …).
        /// </summary>
        public string Type { get; set; } = "double";

        /// <summary>
        /// Gets or sets alternate spellings of the unit (e.g. Amps for A).
        /// </summary>
        public List<string> Aliases { get; set; } = new();
    }
}
