using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace squalor.DataBall
{
    /// <summary>
    /// Represents the configuration for a DataBall instance, including metadata, column types, and relationships.
    /// </summary>
    public class Config
    {
        /// <summary>
        /// Gets or sets the metadata dictionary for storing constant values.
        /// </summary>
        public Dictionary<string, object?> Metadata { get; set; } = new();

        /// <summary>
        /// Gets or sets the dictionary mapping column names to their type names.
        /// </summary>
        public Dictionary<string, string> Columns { get; set; } = new();

        /// <summary>
        /// Gets or sets the list of relationships defining dependencies between columns.
        /// </summary>
        public List<Relationship> Relationships { get; set; } = new();

        /// <summary>
        /// Loads configuration from a JSON file.
        /// </summary>
        /// <param name="path">The path to the JSON configuration file.</param>
        /// <returns>A <see cref="Config"/> object populated with the configuration data.</returns>
        /// <exception cref="DataBallException">Thrown when the configuration file cannot be loaded or deserialized.</exception>
        public static Config LoadConfig(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Config>(json) ?? throw new DataBallException("Failed to deserialize config");
            }
            catch (Exception ex)
            {
                throw new DataBallException("Failed to load config", ex);
            }
        }
    }
}