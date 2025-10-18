using System.Collections.Generic;

namespace squalor.DataBall;

/// <summary>
/// Represents the configuration for a <see cref="DataBall"/> instance, including metadata, column types, and relationships.
/// </summary>
public class Config
{
    /// <summary>
    /// Gets or sets the metadata key-value pairs for the DataBall.
    /// </summary>
    public Dictionary<string, object?>? Metadata { get; set; }

    /// <summary>
    /// Gets or sets the column definitions, mapping column names to their data types.
    /// </summary>
    public Dictionary<string, string>? Columns { get; set; }

    /// <summary>
    /// Gets or sets the list of relationships for row builder operations.
    /// </summary>
    public List<Relationship>? Relationships { get; set; }
}