// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace squalor.DataBall
{
    /// <summary>
    /// Configuration for a DataBall instance: metadata values, column types, relationships,
    /// CSV header patterns, unit→type lookup, parameter roles, and metadata field lists.
    /// Overlay files merge on top of <see cref="CreateDefaults"/>.
    /// </summary>
    public class Config
    {
        private static readonly JsonSerializerOptions LoadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// Gets or sets constant metadata values written at construction.
        /// </summary>
        public Dictionary<string, object?> Metadata { get; set; } = new();

        /// <summary>
        /// Gets or sets per-column type overrides (canonical name → type name). Wins over unit lookup.
        /// </summary>
        public Dictionary<string, string> Columns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets trigger/reset relationships.
        /// </summary>
        public List<Relationship> Relationships { get; set; } = new();

        /// <summary>
        /// Gets or sets CSV header-pattern settings.
        /// </summary>
        public CsvConfig Csv { get; set; } = new();

        /// <summary>
        /// Gets or sets unit → type lookup (keys and aliases are case-insensitive).
        /// </summary>
        public Dictionary<string, UnitSpec> Units { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets per-parameter role/type overlays, keyed by canonical name.
        /// </summary>
        public Dictionary<string, ParameterSpec> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets canonical names treated as stimulus (merged into <see cref="Parameters"/>).
        /// </summary>
        public List<string> Stimulus { get; set; } = new();

        /// <summary>
        /// Gets or sets canonical names treated as row-level classification (pass/bin/grade).
        /// </summary>
        public List<string> Classification { get; set; } = new();

        /// <summary>
        /// Gets or sets canonical names that belong in the metadata table, not in <c>data</c>.
        /// </summary>
        public List<string> MetadataFields { get; set; } = new();

        /// <summary>
        /// Gets or sets names appended to the default metadata field list (overlay only).
        /// </summary>
        public List<string> MetadataFieldsAdd { get; set; } = new();

        /// <summary>
        /// Gets or sets metadata extraction policy: <c>requireConstant</c>, <c>first</c>, or <c>bounce</c>.
        /// </summary>
        public string? MetadataPolicy { get; set; }

        /// <summary>
        /// Gets or sets the config-declared table layout, keyed by table name (insertion order is
        /// table order). Empty means one wide <c>"data"</c> table. Non-empty makes <c>"data"</c> a
        /// view over these tables. An overlay with tables replaces the baseline list.
        /// </summary>
        public Dictionary<string, TableSpec> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Built-in profile: header patterns, common units, default stimulus and metadata names.
        /// </summary>
        public static Config CreateDefaults()
        {
            var config = new Config
            {
                Csv = new CsvConfig
                {
                    HeaderPatterns =
                    {
                        HeaderParser.NameUnitParens,
                        HeaderParser.NameUnderscoreUnit,
                        HeaderParser.NameOnly,
                    }
                },
                MetadataPolicy = "requireConstant",
            };

            void Unit(string name, string type, params string[] aliases)
            {
                config.Units[name] = new UnitSpec { Type = type, Aliases = aliases.ToList() };
            }

            Unit("dB", "double");
            Unit("dBm", "double");
            Unit("A", "double", "Amps", "Amp", "ampere", "amperes");
            Unit("V", "double", "Volt", "Volts");
            Unit("Hz", "double");
            Unit("kHz", "double");
            Unit("MHz", "double");
            Unit("GHz", "double");
            Unit("s", "double", "sec", "second", "seconds");
            Unit("ms", "double");
            Unit("C", "double", "°C", "degC", "celsius");
            Unit("Ohm", "double", "ohm", "ohms", "Ω");
            Unit("W", "double", "Watt", "Watts");
            Unit("%", "double", "pct", "percent");
            Unit("id", "long", "ID");

            foreach (var name in new[] { "Freq", "Frequency", "Pwr", "Power", "Temp", "Time" })
                config.Parameters[name] = new ParameterSpec { Role = ParameterRole.Stimulus };

            foreach (var name in new[] { "id", "ID" })
            {
                if (!config.Parameters.ContainsKey(name))
                    config.Parameters[name] = new ParameterSpec { Role = ParameterRole.Identity, Type = "long" };
            }

            config.MetadataFields.AddRange(new[] { "Lot", "Tester", "Program" });
            ApplyRoleLists(config);
            return config;
        }

        /// <summary>
        /// Loads overlay JSON from a file (does not apply defaults).
        /// </summary>
        public static Config LoadConfig(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Config>(json, LoadOptions) ?? throw new DataBallException("Failed to deserialize config");
                return Normalize(loaded);
            }
            catch (Exception ex)
            {
                throw new DataBallException("Failed to load config", ex);
            }
        }

        /// <summary>
        /// Loads overlay JSON and merges it onto <see cref="CreateDefaults"/>.
        /// </summary>
        public static Config LoadMerged(string path)
        {
            return Merge(CreateDefaults(), LoadConfig(path));
        }

        /// <summary>
        /// Merges <paramref name="overlay"/> onto <paramref name="baseline"/>. Overlay keys win.
        /// If overlay <see cref="MetadataFields"/> is non-empty it replaces the baseline list;
        /// <see cref="MetadataFieldsAdd"/> always appends.
        /// </summary>
        public static Config Merge(Config baseline, Config overlay)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(overlay);

            var result = new Config();
            CopyInto(result, baseline);
            CopyInto(result, overlay);
            ApplyRoleLists(result);
            return result;
        }

        /// <summary>
        /// Resolves a unit (or alias) to a type name, or null if unknown.
        /// </summary>
        public string? TryResolveUnitType(string unit)
        {
            if (string.IsNullOrWhiteSpace(unit))
                return null;
            var u = unit.Trim();
            if (Units.TryGetValue(u, out var spec) && !string.IsNullOrWhiteSpace(spec.Type))
                return spec.Type;
            foreach (var pair in Units)
            {
                if (pair.Value.Aliases is null)
                    continue;
                foreach (var alias in pair.Value.Aliases)
                {
                    if (alias is not null && alias.Equals(u, StringComparison.OrdinalIgnoreCase))
                        return string.IsNullOrWhiteSpace(pair.Value.Type) ? "double" : pair.Value.Type;
                }
            }
            return null;
        }

        /// <summary>
        /// Type name for a canonical parameter: column override, then parameter.Type, then unit lookup.
        /// Null means leave DuckDB inference (do not coerce).
        /// </summary>
        public string? ResolveTypeName(string canonicalName, string? unit)
        {
            if (Columns.TryGetValue(canonicalName, out var colType) && !string.IsNullOrWhiteSpace(colType))
                return colType;
            if (Parameters.TryGetValue(canonicalName, out var p) && !string.IsNullOrWhiteSpace(p.Type))
                return p.Type;
            return TryResolveUnitType(unit ?? "");
        }

        /// <summary>
        /// Role for a canonical name. Default <see cref="ParameterRole.Meas"/>.
        /// </summary>
        public string ResolveRole(string canonicalName)
        {
            if (Parameters.TryGetValue(canonicalName, out var p) && !string.IsNullOrWhiteSpace(p.Role))
                return p.Role.Trim().ToLowerInvariant();
            if (ContainsName(Stimulus, canonicalName))
                return ParameterRole.Stimulus;
            if (ContainsName(Classification, canonicalName))
                return ParameterRole.Classification;
            if (ContainsName(MetadataFields, canonicalName) || ContainsName(MetadataFieldsAdd, canonicalName))
                return ParameterRole.Metadata;
            return ParameterRole.Meas;
        }

        /// <summary>
        /// Maps a config type name to a supported CLR type.
        /// </summary>
        public static Type ParseColumnType(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new DataBallException("Unknown column type ''");

            var n = name.Trim();
            return n.ToLowerInvariant() switch
            {
                "int" or "int32" or "system.int32" => typeof(int),
                "long" or "int64" or "bigint" or "system.int64" => typeof(long),
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

        private static void CopyInto(Config dest, Config src)
        {
            foreach (var (k, v) in src.Metadata)
                dest.Metadata[k] = v;
            foreach (var (k, v) in src.Columns)
                dest.Columns[k] = v;
            if (src.Relationships.Count > 0)
            {
                dest.Relationships.Clear();
                foreach (var rel in src.Relationships)
                    dest.Relationships.Add(CloneRelationship(rel));
            }

            if (src.Csv.HeaderPatterns.Count > 0)
                dest.Csv.HeaderPatterns = new List<string>(src.Csv.HeaderPatterns);

            foreach (var (k, v) in src.Units)
                dest.Units[k] = CloneUnit(v);
            foreach (var (k, v) in src.Parameters)
                dest.Parameters[k] = CloneParameter(v);
            if (src.Stimulus.Count > 0)
                dest.Stimulus = new List<string>(src.Stimulus);
            if (src.Classification.Count > 0)
                dest.Classification = new List<string>(src.Classification);

            if (src.MetadataFields.Count > 0)
                dest.MetadataFields = new List<string>(src.MetadataFields);
            foreach (var add in src.MetadataFieldsAdd)
            {
                if (!ContainsName(dest.MetadataFields, add))
                    dest.MetadataFields.Add(add);
            }

            if (!string.IsNullOrWhiteSpace(src.MetadataPolicy))
                dest.MetadataPolicy = src.MetadataPolicy;

            if (src.Tables.Count > 0)
            {
                dest.Tables = new Dictionary<string, TableSpec>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in src.Tables)
                    dest.Tables[k] = CloneTable(v);
            }
        }

        private static Config Normalize(Config config)
        {
            config.Metadata ??= new();
            config.Columns ??= new();
            config.Relationships ??= new();
            config.Csv ??= new();
            config.Units ??= new();
            config.Parameters ??= new();
            config.Stimulus ??= new();
            config.Classification ??= new();
            config.MetadataFields ??= new();
            config.MetadataFieldsAdd ??= new();
            config.Tables ??= new();
            config.Columns = new Dictionary<string, string>(config.Columns, StringComparer.OrdinalIgnoreCase);
            config.Units = new Dictionary<string, UnitSpec>(config.Units, StringComparer.OrdinalIgnoreCase);
            config.Parameters = new Dictionary<string, ParameterSpec>(config.Parameters, StringComparer.OrdinalIgnoreCase);
            config.Tables = new Dictionary<string, TableSpec>(config.Tables, StringComparer.OrdinalIgnoreCase);
            return config;
        }

        private static void ApplyRoleLists(Config config)
        {
            void Apply(IEnumerable<string> names, string role)
            {
                foreach (var name in names)
                {
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    if (!config.Parameters.TryGetValue(name, out var spec) || spec is null)
                        spec = config.Parameters[name] = new ParameterSpec();
                    if (string.IsNullOrWhiteSpace(spec.Role))
                        spec.Role = role;
                }
            }

            Apply(config.Stimulus, ParameterRole.Stimulus);
            Apply(config.Classification, ParameterRole.Classification);
            Apply(config.MetadataFields, ParameterRole.Metadata);
        }

        internal Config Clone()
        {
            var dest = new Config();
            CopyInto(dest, this);
            return dest;
        }

        private static UnitSpec CloneUnit(UnitSpec spec)
        {
            if (spec is null)
                return new UnitSpec();
            return new UnitSpec
            {
                Type = spec.Type,
                Aliases = spec.Aliases is null ? new List<string>() : new List<string>(spec.Aliases)
            };
        }

        private static ParameterSpec CloneParameter(ParameterSpec spec)
        {
            if (spec is null)
                return new ParameterSpec();
            return new ParameterSpec { Role = spec.Role, Type = spec.Type };
        }

        private static TableSpec CloneTable(TableSpec spec)
        {
            if (spec is null)
                return new TableSpec();
            return new TableSpec
            {
                Kind = spec.Kind,
                Columns = spec.Columns is null ? new List<string>() : new List<string>(spec.Columns),
                Roles = spec.Roles is null ? new List<string>() : new List<string>(spec.Roles),
                Key = spec.Key is null ? new List<string>() : new List<string>(spec.Key),
                Parent = spec.Parent
            };
        }

        private static Relationship CloneRelationship(Relationship rel)
        {
            if (rel is null)
                return new Relationship();
            return new Relationship
            {
                TriggerField = rel.TriggerField,
                ResetFields = rel.ResetFields is null ? new List<string>() : new List<string>(rel.ResetFields)
            };
        }

        private static bool ContainsName(IEnumerable<string> names, string name)
        {
            foreach (var n in names)
            {
                if (n is not null && n.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
