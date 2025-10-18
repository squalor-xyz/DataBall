namespace squalor.DataBall;

/// <summary>
/// Represents a relationship in the row builder pattern, where a change in a trigger field resets dependent fields.
/// </summary>
public class Relationship
{
    /// <summary>
    /// Gets or sets the name of the trigger field that initiates the relationship.
    /// </summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the array of field names to reset when the trigger changes.
    /// </summary>
    public string[] Reset { get; set; } = Array.Empty<string>();
}