using System.Collections.Generic;

namespace squalor.DataBall
{
    /// <summary>
    /// Represents a relationship between columns, defining trigger and dependent fields.
    /// </summary>
    public class Relationship
    {
        /// <summary>
        /// Gets or sets the name of the trigger field that, when changed, affects dependent fields.
        /// </summary>
        public string TriggerField { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the list of dependent fields to reset when the trigger field changes.
        /// </summary>
        public List<string> ResetFields { get; set; } = new();
    }
}