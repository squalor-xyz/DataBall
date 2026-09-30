// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace squalor.DataBall
{
    /// <summary>
    /// Resolved physical layout of a session declared through <see cref="Config.Tables"/>.
    /// Pure: built from the merged config plus the wide column list, no DuckDB access.
    /// </summary>
    internal sealed class TableLayout
    {
        internal const string RowKey = "_row";
        internal const string ImpliedSpineName = "rows";

        internal const string KindMaster = "master";
        internal const string KindDimension = "dimension";
        internal const string KindRows = "rows";
        internal const string KindMeasurements = "measurements";

        private static readonly string[] Kinds = { KindMaster, KindDimension, KindRows, KindMeasurements };
        private static readonly string[] SelectableRoles =
        {
            ParameterRole.Identity, ParameterRole.Stimulus, ParameterRole.Meas, ParameterRole.Classification
        };

        /// <summary>
        /// One physical table. <see cref="Columns"/> are wide (canonical) names in wide order.
        /// </summary>
        internal sealed class PhysicalTable
        {
            internal PhysicalTable(string name, string kind, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns)
            {
                Name = name;
                Kind = kind;
                Columns = columns;
                KeyColumns = keyColumns;
            }

            internal string Name { get; }
            internal string Kind { get; }
            internal IReadOnlyList<string> Columns { get; }

            /// <summary>Dimension key columns (subset of <see cref="Columns"/>); empty for spine and groups.</summary>
            internal IReadOnlyList<string> KeyColumns { get; }

            /// <summary>Surrogate key column name carried by the spine for a dimension.</summary>
            internal string KeyColumnName => Name + "_key";

            internal bool IsDimension => Kind == KindMaster || Kind == KindDimension;
        }

        private readonly Dictionary<string, PhysicalTable> _tableForColumn = new(StringComparer.OrdinalIgnoreCase);

        private TableLayout(
            IReadOnlyList<string> wideColumns,
            PhysicalTable spine,
            IReadOnlyList<PhysicalTable> dimensions,
            IReadOnlyList<PhysicalTable> measurementGroups)
        {
            WideColumns = wideColumns;
            Spine = spine;
            Dimensions = dimensions;
            MeasurementGroups = measurementGroups;
            foreach (var table in PhysicalTables)
            {
                foreach (var column in table.Columns)
                    _tableForColumn[column] = table;
            }
        }

        /// <summary>Wide (view) columns in their original order.</summary>
        internal IReadOnlyList<string> WideColumns { get; }

        internal PhysicalTable Spine { get; }

        internal IReadOnlyList<PhysicalTable> Dimensions { get; }

        internal IReadOnlyList<PhysicalTable> MeasurementGroups { get; }

        /// <summary>Dimensions, then the spine, then measurement groups.</summary>
        internal IEnumerable<PhysicalTable> PhysicalTables
        {
            get
            {
                foreach (var d in Dimensions)
                    yield return d;
                yield return Spine;
                foreach (var g in MeasurementGroups)
                    yield return g;
            }
        }

        internal IReadOnlyList<string> PhysicalTableNames => PhysicalTables.Select(t => t.Name).ToList();

        /// <summary>True when the config declares a <c>tables</c> section.</summary>
        internal static bool IsDeclared(Config config)
        {
            ArgumentNullException.ThrowIfNull(config);
            return config.Tables.Count > 0;
        }

        /// <summary>
        /// Structural validation that needs no data: names, kinds, one spine, one master,
        /// key/parent rules, and no column or role claimed twice.
        /// </summary>
        internal static void Validate(Config config)
        {
            ArgumentNullException.ThrowIfNull(config);
            var claimedColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var claimedRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? spine = null;
            string? master = null;

            foreach (var (name, spec) in config.Tables)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new DataBallException("tables: a table name is required");
                if (name.Equals("data", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("meta", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith('_'))
                    throw new DataBallException($"tables.{name}: name is reserved ('data', 'meta', and names starting with '_')");
                if (spec is null)
                    throw new DataBallException($"tables.{name}: table spec is required");

                var kind = (spec.Kind ?? string.Empty).Trim().ToLowerInvariant();
                if (!Kinds.Contains(kind))
                    throw new DataBallException($"tables.{name}.kind '{spec.Kind}' is not one of master, dimension, rows, measurements");

                if (kind == KindRows)
                {
                    if (spine is not null)
                        throw new DataBallException($"tables: only one 'rows' table is allowed ('{spine}' and '{name}')");
                    spine = name;
                }

                if (kind == KindMaster)
                {
                    if (master is not null)
                        throw new DataBallException($"tables: only one 'master' table is allowed ('{master}' and '{name}')");
                    master = name;
                }

                if (!string.IsNullOrWhiteSpace(spec.Parent))
                    throw new DataBallException($"tables.{name}.parent is not supported yet");

                if (spec.Key.Count > 0 && kind is not (KindMaster or KindDimension))
                    throw new DataBallException($"tables.{name}.key is only allowed on master or dimension tables");

                foreach (var column in spec.Columns)
                {
                    if (string.IsNullOrWhiteSpace(column))
                        throw new DataBallException($"tables.{name}.columns contains an empty name");
                    if (claimedColumns.TryGetValue(column, out var other))
                        throw new DataBallException($"tables: column '{column}' is claimed by both '{other}' and '{name}'");
                    claimedColumns[column] = name;
                }

                foreach (var key in spec.Key)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        throw new DataBallException($"tables.{name}.key contains an empty name");
                    if (spec.Columns.Count > 0 && !spec.Columns.Any(c => c.Equals(key, StringComparison.OrdinalIgnoreCase)))
                        throw new DataBallException($"tables.{name}.key '{key}' is not in tables.{name}.columns");
                }

                foreach (var role in spec.Roles)
                {
                    var r = (role ?? string.Empty).Trim().ToLowerInvariant();
                    if (!SelectableRoles.Contains(r))
                        throw new DataBallException($"tables.{name}.roles '{role}' is not one of identity, stimulus, meas, classification");
                    if (claimedRoles.TryGetValue(r, out var other))
                        throw new DataBallException($"tables: role '{r}' is selected by both '{other}' and '{name}'");
                    claimedRoles[r] = name;
                }
            }

            if (spine is null && config.Tables.Keys.Any(k => k.Equals(ImpliedSpineName, StringComparison.OrdinalIgnoreCase)))
                throw new DataBallException($"tables: '{ImpliedSpineName}' is the implied spine name; declare a table of kind 'rows' or rename it");
        }

        /// <summary>
        /// Assigns each wide column to a table: explicit claims first, then role selectors
        /// (in table order), then the spine. Tables that end up with no columns are omitted.
        /// </summary>
        internal static TableLayout Build(Config config, IReadOnlyList<string> wideColumns)
        {
            ArgumentNullException.ThrowIfNull(wideColumns);
            Validate(config);
            if (!IsDeclared(config))
                throw new DataBallException("tables: no layout declared");

            var wide = wideColumns.ToList();
            foreach (var column in wide)
            {
                if (column.Equals(RowKey, StringComparison.OrdinalIgnoreCase))
                    throw new DataBallException($"tables: column '{column}' collides with the reserved row key");
                foreach (var name in config.Tables.Keys)
                {
                    if (column.Equals(name + "_key", StringComparison.OrdinalIgnoreCase))
                        throw new DataBallException($"tables: column '{column}' collides with the surrogate key of table '{name}'");
                }
            }

            // Config.Tables is a Dictionary; JSON object order is preserved in practice and is
            // what decides role-selector precedence between tables.
            var order = config.Tables.Keys.ToList();
            var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // wide column -> table

            foreach (var name in order)
            {
                foreach (var claimed in config.Tables[name].Columns)
                {
                    var match = wide.FirstOrDefault(c => c.Equals(claimed, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                        assigned[match] = name;
                }
            }

            foreach (var column in wide)
            {
                if (assigned.ContainsKey(column))
                    continue;
                var role = config.ResolveRole(column);
                foreach (var name in order)
                {
                    if (config.Tables[name].Roles.Any(r => r.Trim().Equals(role, StringComparison.OrdinalIgnoreCase)))
                    {
                        assigned[column] = name;
                        break;
                    }
                }
            }

            var spineName = order.FirstOrDefault(n => KindOf(config.Tables[n]) == KindRows) ?? ImpliedSpineName;
            foreach (var column in wide)
            {
                if (!assigned.ContainsKey(column))
                    assigned[column] = spineName;
            }

            List<string> ColumnsOf(string table) => wide.Where(c => assigned[c].Equals(table, StringComparison.OrdinalIgnoreCase)).ToList();

            var dimensions = new List<PhysicalTable>();
            var groups = new List<PhysicalTable>();
            foreach (var name in order)
            {
                var spec = config.Tables[name];
                var kind = KindOf(spec);
                if (kind == KindRows)
                    continue;
                var columns = ColumnsOf(name);
                if (columns.Count == 0)
                    continue;

                if (kind is KindMaster or KindDimension)
                {
                    List<string> key;
                    if (spec.Key.Count == 0)
                    {
                        key = columns;
                    }
                    else
                    {
                        key = new List<string>(spec.Key.Count);
                        foreach (var k in spec.Key)
                        {
                            var match = columns.FirstOrDefault(c => c.Equals(k, StringComparison.OrdinalIgnoreCase))
                                ?? throw new DataBallException($"tables.{name}.key '{k}' is not in the data");
                            key.Add(match);
                        }
                    }
                    dimensions.Add(new PhysicalTable(name, kind, columns, key));
                }
                else
                {
                    groups.Add(new PhysicalTable(name, kind, columns, Array.Empty<string>()));
                }
            }

            var spine = new PhysicalTable(spineName, KindRows, ColumnsOf(spineName), Array.Empty<string>());
            return new TableLayout(wide, spine, dimensions, groups);
        }

        /// <summary>
        /// True when <paramref name="other"/> has the same spine, dimensions (name, columns, key),
        /// and measurement groups (name, columns). Column order and wide order are ignored.
        /// </summary>
        internal bool SameShapeAs(TableLayout other)
        {
            ArgumentNullException.ThrowIfNull(other);
            static bool SameTable(PhysicalTable a, PhysicalTable b)
                => a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)
                   && a.Kind == b.Kind
                   && a.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Columns)
                   && a.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.KeyColumns);

            var mine = PhysicalTables.ToList();
            var theirs = other.PhysicalTables.ToList();
            if (mine.Count != theirs.Count)
                return false;
            foreach (var table in mine)
            {
                var match = theirs.FirstOrDefault(t => t.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase));
                if (match is null || !SameTable(table, match))
                    return false;
            }
            return true;
        }

        /// <summary>Physical table that owns <paramref name="column"/>.</summary>
        internal string TableFor(string column)
        {
            if (_tableForColumn.TryGetValue(column, out var table))
                return table.Name;
            throw new DataBallException($"Unknown column '{column}'");
        }

        /// <summary>
        /// Deterministic surrogate key: <c>CAST(hash(k1, ...) &gt;&gt; 1 AS BIGINT)</c> over the key
        /// columns, read from <paramref name="source"/> (a quoted relation or alias) when given.
        /// </summary>
        internal static string KeyExpression(PhysicalTable dimension, string? source = null)
        {
            var args = dimension.KeyColumns.Select(k =>
                source is null ? DuckDbStore.QuoteIdent(k) : source + "." + DuckDbStore.QuoteIdent(k));
            return $"CAST(hash({string.Join(", ", args)}) >> 1 AS BIGINT)";
        }

        /// <summary>
        /// <c>SELECT</c> body of the <c>"data"</c> view: wide columns in wide order, spine
        /// first, dimensions joined on their key, measurement groups joined on the row key.
        /// </summary>
        internal string BuildViewSelect()
        {
            if (WideColumns.Count == 0)
                throw new DataBallException("A table layout needs at least one wide column");
            var sb = new StringBuilder("SELECT ");
            var first = true;
            foreach (var column in WideColumns)
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                var table = _tableForColumn[column];
                sb.Append(DuckDbStore.QuoteIdent(table.Name)).Append('.').Append(DuckDbStore.QuoteIdent(column));
            }

            var qSpine = DuckDbStore.QuoteIdent(Spine.Name);
            sb.Append(" FROM ").Append(qSpine);
            foreach (var d in Dimensions)
            {
                var qd = DuckDbStore.QuoteIdent(d.Name);
                var qk = DuckDbStore.QuoteIdent(d.KeyColumnName);
                sb.Append(" LEFT JOIN ").Append(qd).Append(" ON ").Append(qd).Append('.').Append(qk)
                  .Append(" = ").Append(qSpine).Append('.').Append(qk);
            }
            foreach (var g in MeasurementGroups)
            {
                var qg = DuckDbStore.QuoteIdent(g.Name);
                var qr = DuckDbStore.QuoteIdent(RowKey);
                sb.Append(" LEFT JOIN ").Append(qg).Append(" ON ").Append(qg).Append('.').Append(qr)
                  .Append(" = ").Append(qSpine).Append('.').Append(qr);
            }
            return sb.ToString();
        }

        /// <summary>Every wide row in row-key order (the order the rows were written).</summary>
        internal string BuildOrderedViewSelect()
        {
            return BuildViewSelect()
                + $" ORDER BY {DuckDbStore.QuoteIdent(Spine.Name)}.{DuckDbStore.QuoteIdent(RowKey)}";
        }

        /// <summary>The wide row with the highest row key (the last committed row).</summary>
        internal string BuildLastRowSelect()
        {
            return BuildViewSelect()
                + $" ORDER BY {DuckDbStore.QuoteIdent(Spine.Name)}.{DuckDbStore.QuoteIdent(RowKey)} DESC LIMIT 1";
        }

        private static string KindOf(TableSpec spec) => (spec.Kind ?? string.Empty).Trim().ToLowerInvariant();
    }
}
