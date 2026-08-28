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

        /// <summary>
        /// Maps a config type name to a supported CLR type.
        /// </summary>
        /// <param name="name">The type name from configuration.</param>
        /// <returns>The matching CLR type.</returns>
        /// <exception cref="DataBallException">Thrown when the type name is unknown.</exception>
        public static Type ParseColumnType(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new DataBallException("Unknown column type ''");

            var n = name.Trim();
            return n.ToLowerInvariant() switch
            {
                "int" or "int32" or "system.int32" => typeof(int),
                "long" or "int64" or "system.int64" => typeof(long),
                "float" or "single" or "system.single" => typeof(float),
                "double" or "system.double" => typeof(double),
                "bool" or "boolean" or "system.boolean" => typeof(bool),
                "datetime" or "date" or "system.datetime" => typeof(DateTime),
                "string" or "system.string" => typeof(string),
                _ => Type.GetType(n, throwOnError: false) is { } t
                    ? t
                    : throw new DataBallException($"Unknown column type '{name}'")
            };
        }
    }
}