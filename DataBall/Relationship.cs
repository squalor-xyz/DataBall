using System.Text.Json.Serialization;

namespace squalor.DataBall;

/// <summary>
/// Represents a configuration relationship in DataBall.
/// When the trigger field changes, specified reset fields are cleared to null unless modified explicitly.
/// </summary>
public class Relationship
{
    /// <summary>
    /// The trigger field name that, when changed, resets dependents.
    /// </summary>
    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// List of fields to reset if the trigger changes.
    /// </summary>
    [JsonPropertyName("reset")]
    public List<string> Reset { get; set; } = new List<string>();
}