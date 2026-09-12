// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace squalor.DataBall
{
    /// <summary>
    /// Splits a column header into canonical name and optional unit using configured patterns.
    /// </summary>
    public static class HeaderParser
    {
        public const string NameUnitParens = "{name}({unit})";
        public const string NameUnderscoreUnit = "{name}_{unit}";
        public const string NameOnly = "{name}";

        /// <summary>
        /// Parses <paramref name="header"/> against <paramref name="patterns"/> in order.
        /// <c>{name}_{unit}</c> only applies when the suffix is a known unit.
        /// </summary>
        public static (string Name, string? Unit) Parse(
            string header,
            IReadOnlyList<string> patterns,
            Func<string, bool> isKnownUnit)
        {
            ArgumentNullException.ThrowIfNull(patterns);
            ArgumentNullException.ThrowIfNull(isKnownUnit);
            if (string.IsNullOrWhiteSpace(header))
                throw new DataBallException("Header is required");

            var trimmed = header.Trim();
            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;
                if (TryMatch(trimmed, pattern.Trim(), isKnownUnit, out var name, out var unit))
                    return (name, unit);
            }

            return (trimmed, null);
        }

        private static bool TryMatch(
            string header,
            string pattern,
            Func<string, bool> isKnownUnit,
            out string name,
            out string? unit)
        {
            name = header;
            unit = null;

            if (pattern.Equals(NameUnitParens, StringComparison.OrdinalIgnoreCase)
                || pattern.Equals("parameter(unit)", StringComparison.OrdinalIgnoreCase))
            {
                var close = header.LastIndexOf(')');
                var open = header.LastIndexOf('(');
                if (close != header.Length - 1 || open <= 0 || close <= open + 1)
                    return false;
                var n = header[..open].Trim();
                var u = header[(open + 1)..close].Trim();
                if (n.Length == 0 || u.Length == 0)
                    return false;
                name = n;
                unit = u;
                return true;
            }

            if (pattern.Equals(NameUnderscoreUnit, StringComparison.OrdinalIgnoreCase)
                || pattern.Equals("parameter_unit", StringComparison.OrdinalIgnoreCase))
            {
                var us = header.LastIndexOf('_');
                if (us <= 0 || us >= header.Length - 1)
                    return false;
                var n = header[..us].Trim();
                var u = header[(us + 1)..].Trim();
                if (n.Length == 0 || u.Length == 0 || !isKnownUnit(u))
                    return false;
                name = n;
                unit = u;
                return true;
            }

            if (pattern.Equals(NameOnly, StringComparison.OrdinalIgnoreCase)
                || pattern.Equals("parameter", StringComparison.OrdinalIgnoreCase))
            {
                name = header;
                unit = null;
                return true;
            }

            return false;
        }
    }
}
