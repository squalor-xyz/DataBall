using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace squalor.DataBall;

/// <summary>
/// Configuration model for DataBall, loaded from JSON.
/// Defines metadata, column types, and relationships.
/// </summary>
public class Config
{
    /// <summary>
    /// Initial metadata constants.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; set; }

    /// <summary>
    /// Column definitions with expected types.
    /// </summary>
    [JsonPropertyName("columns")]
    public Dictionary<string, string>? Columns { get; set; } // Key: column name, Value: type string (int, string, etc.)

    /// <summary>
    /// List of relationships for propagation on row commit.
    /// </summary>
    [JsonPropertyName("relationships")]
    public List<Relationship>? Relationships { get; set; }
}