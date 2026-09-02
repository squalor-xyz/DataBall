using System;
using System.Collections.Generic;

namespace squalor.DataBall
{
    /// <summary>
    /// Resolves raw headers to canonical name, unit, role, and CLR type using a merged config.
    /// </summary>
    public static class SchemaResolver
    {
        /// <summary>
        /// Parses <paramref name="rawHeader"/> and applies unit, parameter, and column overlays.
        /// </summary>
        public static ParsedHeader Resolve(string rawHeader, Config config)
        {
            ArgumentNullException.ThrowIfNull(config);
            var (name, unit) = HeaderParser.Parse(
                rawHeader,
                config.Csv.HeaderPatterns,
                u => config.TryResolveUnitType(u) is not null);

            var role = config.ResolveRole(name);
            var typeName = config.ResolveTypeName(name, unit);
            Type? clr = typeName is null ? null : Config.ParseColumnType(typeName);
            return new ParsedHeader(rawHeader.Trim(), name, unit, role, clr);
        }

        /// <summary>
        /// Resolves every header in <paramref name="rawHeaders"/>. Duplicate canonical names throw.
        /// </summary>
        public static IReadOnlyList<ParsedHeader> ResolveAll(IEnumerable<string> rawHeaders, Config config)
        {
            ArgumentNullException.ThrowIfNull(rawHeaders);
            ArgumentNullException.ThrowIfNull(config);
            var list = new List<ParsedHeader>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in rawHeaders)
            {
                var parsed = Resolve(raw, config);
                if (!seen.Add(parsed.Name))
                    throw new DataBallException($"Duplicate canonical column '{parsed.Name}' from header '{parsed.Raw}'");
                list.Add(parsed);
            }
            return list;
        }
    }
}
