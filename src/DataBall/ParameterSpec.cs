namespace squalor.DataBall
{
    /// <summary>
    /// Per-parameter schema overlay (role and optional type), keyed by canonical name.
    /// </summary>
    public sealed class ParameterSpec
    {
        /// <summary>
        /// Gets or sets the role: identity, stimulus, meas, classification, or metadata.
        /// </summary>
        public string? Role { get; set; }

        /// <summary>
        /// Gets or sets an explicit type name. Wins over the unit lookup.
        /// </summary>
        public string? Type { get; set; }
    }
}
