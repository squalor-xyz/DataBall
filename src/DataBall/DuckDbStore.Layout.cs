// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace squalor.DataBall
{
    /// <summary>
    /// Config-declared table layout: the wide relation <c>"data"</c> becomes a view over
    /// dimension, spine, and measurement-group tables. Every legacy write path stays
    /// hard-coded to the base table; these members are the only ones that know about views.
    /// One SQL path (<see cref="InsertStagingRows"/>) serves both the initial split and appends.
    /// </summary>
    internal sealed partial class DuckDbStore
    {
        private const string WideTemp = "_wide";
        private const string CastTemp = "_cast";
        private const string RowsTemp = "_rows";
        private const string UnsplitTemp = "_unsplit";

        /// <summary>Layout bound to this store, or null for a single wide table.</summary>
        internal TableLayout? Layout { get; private set; }

        /// <summary>Merged config the bound layout was built from (needed to grow it on append).</summary>
        internal Config? LayoutConfig { get; private set; }

        /// <summary>
        /// Points the bound layout at a newer merged config (after importing native <c>.ball</c> config). The
        /// overlay must describe the same tables for the current wide columns; a different
        /// <c>tables</c> section cannot be adopted while rows are already split.
        /// </summary>
        internal void UpdateLayoutConfig(Config config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (Layout is null)
                return;
            var rebuilt = TableLayout.Build(config, Layout.WideColumns);
            if (!Layout.SameShapeAs(rebuilt))
                throw new DataBallException("The imported config declares a different 'tables' layout than the open session; replace the session instead of appending");
            LayoutConfig = config;
        }

        /// <summary>Forgets the layout after the committing caller dropped its relation.</summary>
        internal void ClearLayout()
        {
            Layout = null;
            LayoutConfig = null;
        }

        /// <summary>Puts back a snapshot after the caller's transaction rolled back.</summary>
        internal void RestoreLayout(TableLayout? layout, Config? config)
        {
            Layout = layout;
            LayoutConfig = layout is null ? null : config;
        }

        /// <summary>True when <c>"data"</c> exists and is a VIEW.</summary>
        internal bool DataIsView()
        {
            ThrowIfDisposed();
            var result = ExecuteScalar("""
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_catalog = current_database() AND table_schema = 'main' AND table_name = 'data' AND table_type = 'VIEW'
                """);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
        }

        /// <summary>
        /// Splits the base table <c>"data"</c> into the tables declared by <paramref name="config"/>
        /// and replaces it with the wide view. Atomic; the row key is
        /// <c>row_number() OVER (ORDER BY rowid)</c>. Dimension keys are deterministic hashes of the
        /// key columns; a key that does not determine its other columns throws.
        /// </summary>
        internal TableLayout SplitIntoLayout(Config config)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(config);
            if (!DataTableExists())
                throw new DataBallException("No data to split");
            if (DataIsView())
                throw new DataBallException("\"data\" is already a multi-table view");

            var wide = GetColumns();
            var layout = TableLayout.Build(config, wide.Select(c => c.Name).ToList());
            foreach (var table in layout.PhysicalTables)
            {
                if (TableExists(table.Name))
                    throw new DataBallException($"Table '{table.Name}' already exists; cannot build the layout");
            }

            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, type) in wide)
                types[name] = type;
            var incoming = layout.WideColumns.Select(c => new IncomingColumn(c, c)).ToList();

            try
            {
                InTransaction(() =>
                {
                    Execute($"CREATE OR REPLACE TEMP TABLE {QuoteIdent(WideTemp)} AS SELECT * FROM \"data\"");
                    foreach (var table in layout.PhysicalTables)
                        CreateEmptyLayoutTable(layout, table, types);
                    Execute("DROP TABLE \"data\"");
                    InsertStagingRows(WideTemp, layout, incoming);
                    CreateDataView(layout);
                });
            }
            finally
            {
                DropTemp(WideTemp);
                DropTemp(CastTemp);
            }

            Layout = layout;
            LayoutConfig = config;
            return layout;
        }

        /// <summary>
        /// Routes the rows of a TEMP staging table into the bound layout: reconciles metadata
        /// columns, grows spine/group schema for new columns, casts to physical types, reuses
        /// dimension keys, and refreshes the view when the wide column list changed. Growth a
        /// dimension cannot take in place (a new dimension, a new dimension column, or metadata
        /// that now varies into a dimension or group) re-splits the session through
        /// <see cref="AppendByRematerializing"/>. Atomic; in-memory layout and metadata are
        /// restored when the transaction rolls back.
        /// </summary>
        internal void AppendStagingIntoLayout(string staging)
        {
            ThrowIfDisposed();
            ValidateName(staging, "Table");
            var current = Layout ?? throw new DataBallException("No table layout is bound");
            var config = LayoutConfig ?? throw new DataBallException("No table layout config is bound");

            var stagingCols = GetColumnsOf(staging);
            var plan = PlanAppend(current, config, staging, stagingCols);

            try
            {
                InTransaction(() =>
                {
                    if (plan.NeedsRematerialize)
                    {
                        AppendByRematerializing(plan, staging, config);
                        return;
                    }

                    foreach (var (key, _) in plan.Demote)
                        RemoveMetadata(key);

                    if (plan.WideChanged)
                    {
                        Execute("DROP VIEW \"data\"");
                        GrowLayoutSchema(current, plan.Next, plan.Types);
                    }

                    var qSpine = QuoteIdent(plan.Next.Spine.Name);
                    foreach (var (key, value) in plan.Demote)
                        ExecuteParameterized($"UPDATE {qSpine} SET {QuoteIdent(key)} = $v", Param("v", value));

                    InsertStagingRows(staging, plan.Next, plan.Incoming);

                    if (plan.WideChanged)
                        CreateDataView(plan.Next);
                });
            }
            catch
            {
                // A re-split clears the in-memory layout before it can fail; no SQL here, the rollback undoes the tables.
                RestoreLayout(current, config);
                // Owning the transaction means it is rolled back already; a joined caller reloads after its own rollback.
                if (_currentTx is null)
                    ReloadMetadata();
                throw;
            }
            finally
            {
                DropTemp(CastTemp);
            }

            // The re-split path binds its own layout.
            if (!plan.NeedsRematerialize)
                Layout = plan.Next;
        }

        /// <summary>
        /// Append that a grow-in-place cannot express (a new dimension, a new dimension column, or
        /// metadata that now varies into a dimension or group): materialize the layout wide, add
        /// the new columns, append the staged rows, and split again. Runs inside the caller's
        /// transaction; the split renumbers <c>_row</c> and dimension keys may change.
        /// </summary>
        private void AppendByRematerializing(AppendPlan plan, string staging, Config config)
        {
            // After UnsplitLayout "data" is a base table and Layout is null, so writing it by name is safe here.
            UnsplitLayout();

            var qData = "\"data\"";
            var dataTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, type) in GetColumnsOf("data"))
                dataTypes[name] = type;

            foreach (var key in plan.Demote.Select(d => d.Key))
                RemoveMetadata(key);
            var demoted = plan.Demote.ToDictionary(d => d.Key, d => d.Value, StringComparer.OrdinalIgnoreCase);

            foreach (var column in plan.Next.WideColumns)
            {
                if (dataTypes.ContainsKey(column))
                    continue;
                var type = plan.Types[column];
                Execute($"ALTER TABLE {qData} ADD COLUMN {QuoteIdent(column)} {type}");
                dataTypes[column] = type;
                if (demoted.TryGetValue(column, out var value))
                    ExecuteParameterized($"UPDATE {qData} SET {QuoteIdent(column)} = $v", Param("v", value));
            }

            var select = string.Join(", ", plan.Incoming.Select(c => $"CAST({QuoteIdent(c.Staging)} AS {dataTypes[c.Wide]}) AS {QuoteIdent(c.Wide)}"));
            Execute($"INSERT INTO {qData} BY NAME SELECT {select} FROM {QuoteIdent(staging)} ORDER BY rowid");

            SplitIntoLayout(config);
        }

        /// <summary>
        /// Stages <paramref name="rows"/> into a TEMP table typed from the view (existing columns) or
        /// the values (new columns), then routes them with <see cref="AppendStagingIntoLayout"/>.
        /// </summary>
        private void AddRowsIntoLayout(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
            IReadOnlyList<string> keyOrder,
            IReadOnlyDictionary<string, Type>? expectedTypes)
        {
            var viewCols = GetColumns();
            var parts = new List<string>(keyOrder.Count);
            foreach (var key in keyOrder)
            {
                var existing = viewCols.FirstOrDefault(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                var type = existing.Name is null
                    ? ToDuckDbType(ResolveColumnType(key, FirstValue(rows, key), expectedTypes))
                    : existing.DuckDbType;
                parts.Add($"{QuoteIdent(key)} {type}");
            }

            try
            {
                Execute($"CREATE OR REPLACE TEMP TABLE {QuoteIdent(RowsTemp)} ({string.Join(", ", parts)})");
                var columns = GetColumnsOf(RowsTemp);
                using (var appender = _connection.CreateAppender(RowsTemp))
                {
                    foreach (var values in rows)
                        WriteCoercedAppenderRow(appender, CoerceAppenderValues(columns, values));
                }

                AppendStagingIntoLayout(RowsTemp);
            }
            finally
            {
                DropTemp(RowsTemp);
            }
        }

        /// <summary>
        /// Binds an existing view-backed session (file reopen) to <paramref name="config"/>.
        /// Throws when the physical tables the config implies are missing.
        /// </summary>
        internal TableLayout BindExistingLayout(Config config)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(config);
            if (!DataIsView())
                throw new DataBallException("\"data\" is not a multi-table view");
            var wide = GetColumns().Select(c => c.Name).ToList();
            var layout = TableLayout.Build(config, wide);
            foreach (var table in layout.PhysicalTables)
            {
                if (!TableExists(table.Name))
                    throw new DataBallException($"Session layout does not match config 'tables': table '{table.Name}' is missing");
            }
            Layout = layout;
            LayoutConfig = config;
            return layout;
        }

        /// <summary>
        /// Drops the <c>"data"</c> relation: the view plus its physical tables when a layout is
        /// bound, otherwise the base table. Runs inside the caller's replacing transaction and
        /// leaves <see cref="Layout"/> untouched so a rollback stays consistent; the committing
        /// caller rebinds or calls <see cref="ClearLayout"/>.
        /// </summary>
        internal void DropDataRelation()
        {
            ThrowIfDisposed();
            if (DataIsView())
            {
                Execute("DROP VIEW \"data\"");
                if (Layout is not null)
                {
                    foreach (var table in Layout.PhysicalTables)
                        Execute($"DROP TABLE IF EXISTS {QuoteIdent(table.Name)}");
                }
                return;
            }

            Execute("DROP TABLE IF EXISTS \"data\"");
        }

        /// <summary>
        /// Turns the bound layout back into one wide base table <c>"data"</c>, rows in row-key
        /// order, and forgets the layout. Runs inside the caller's transaction, which re-splits
        /// afterwards (or restores the snapshot on rollback). The re-split numbers rows with
        /// <c>row_number() OVER (ORDER BY rowid)</c>, so insertion order keeps row order.
        /// </summary>
        internal void UnsplitLayout()
        {
            ThrowIfDisposed();
            var layout = Layout ?? throw new DataBallException("No table layout is bound");
            try
            {
                Execute($"CREATE OR REPLACE TEMP TABLE {QuoteIdent(UnsplitTemp)} AS {layout.BuildOrderedViewSelect()}");
                DropDataRelation();
                Execute($"CREATE TABLE \"data\" AS SELECT * FROM {QuoteIdent(UnsplitTemp)}");
            }
            finally
            {
                DropTemp(UnsplitTemp);
            }

            ClearLayout();
        }

        /// <summary>Staging column and the wide (canonical) column it feeds.</summary>
        private sealed record IncomingColumn(string Staging, string Wide);

        private sealed record AppendPlan(
            TableLayout Next,
            bool WideChanged,
            IReadOnlyList<IncomingColumn> Incoming,
            IReadOnlyDictionary<string, string> Types,
            IReadOnlyList<(string Key, object? Value)> Demote,
            bool NeedsRematerialize);

        /// <summary>
        /// Decides, without writing, how a staging table joins the layout: which staging columns
        /// are metadata (equal → ignored; conflicting → demoted to a column of the table the config
        /// assigns it to, or ignored under <c>metadataPolicy: first</c>), which wide columns are new, and the grown layout.
        /// </summary>
        private AppendPlan PlanAppend(
            TableLayout current,
            Config config,
            string staging,
            IReadOnlyList<(string Name, string DuckDbType)> stagingCols)
        {
            var qStaging = QuoteIdent(staging);
            var policyFirst = string.Equals(config.MetadataPolicy, "first", StringComparison.OrdinalIgnoreCase);
            var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var demote = new List<(string Key, object? Value)>();
            var wideNameFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in _metadata.ToList())
            {
                // A key that is already a wide column is data, not a constant to reconcile (legacy guard).
                if (current.WideColumns.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                    continue;
                var col = stagingCols.FirstOrDefault(c => c.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                if (col.Name is null)
                    continue;
                var differing = Convert.ToInt64(
                    ExecuteScalarParameterized(
                        $"SELECT COUNT(*) FILTER (WHERE {QuoteIdent(col.Name)} IS DISTINCT FROM $v) FROM {qStaging}",
                        Param("v", pair.Value)),
                    CultureInfo.InvariantCulture);
                if (differing == 0 || policyFirst)
                {
                    ignore.Add(col.Name);
                    continue;
                }

                demote.Add((pair.Key, pair.Value));
                wideNameFor[col.Name] = pair.Key;
            }

            var incoming = new List<IncomingColumn>();
            var newWide = current.WideColumns.ToList();
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, type) in stagingCols)
            {
                if (ignore.Contains(name))
                    continue;
                var wide = wideNameFor.TryGetValue(name, out var demoted)
                    ? demoted
                    : newWide.FirstOrDefault(w => w.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
                if (!newWide.Any(w => w.Equals(wide, StringComparison.OrdinalIgnoreCase)))
                    newWide.Add(wide);
                incoming.Add(new IncomingColumn(name, wide));
                types[wide] = type;
            }

            var next = TableLayout.Build(config, newWide);
            var wideChanged = !newWide.SequenceEqual(current.WideColumns, StringComparer.Ordinal);

            if (!next.Spine.Name.Equals(current.Spine.Name, StringComparison.OrdinalIgnoreCase))
                throw new DataBallException($"Layout spine changed from '{current.Spine.Name}' to '{next.Spine.Name}'; the session config no longer matches its tables");
            foreach (var g in current.MeasurementGroups)
            {
                var after = next.MeasurementGroups.FirstOrDefault(x => x.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase));
                if (after is null || g.Columns.Any(c => !after.Columns.Contains(c, StringComparer.OrdinalIgnoreCase)))
                    throw new DataBallException($"Layout table '{g.Name}' changed; the session config no longer matches its tables");
            }

            // A new dimension, a new dimension column, or a demoted key whose table is not the spine
            // cannot grow in place; the append materializes the layout wide and splits it again.
            var rematerialize = false;
            foreach (var d in next.Dimensions)
            {
                var before = current.Dimensions.FirstOrDefault(x => x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                if (before is null || d.Columns.Any(c => !before.Columns.Contains(c, StringComparer.OrdinalIgnoreCase)))
                    rematerialize = true;
            }

            foreach (var (key, _) in demote)
            {
                if (!next.TableFor(key).Equals(next.Spine.Name, StringComparison.OrdinalIgnoreCase))
                    rematerialize = true;
            }

            return new AppendPlan(next, wideChanged, incoming, types, demote, rematerialize);
        }

        /// <summary>Creates new spine/group tables and adds new spine/group columns. Dimensions never change here.</summary>
        private void GrowLayoutSchema(TableLayout current, TableLayout next, IReadOnlyDictionary<string, string> types)
        {
            foreach (var table in next.PhysicalTables)
            {
                if (table.IsDimension)
                    continue;
                var before = current.PhysicalTables.FirstOrDefault(t => t.Name.Equals(table.Name, StringComparison.OrdinalIgnoreCase));
                if (before is null)
                {
                    CreateEmptyLayoutTable(next, table, types);
                    continue;
                }

                foreach (var column in table.Columns)
                {
                    if (before.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                        continue;
                    Execute($"ALTER TABLE {QuoteIdent(table.Name)} ADD COLUMN {QuoteIdent(column)} {types[column]}");
                }
            }
        }

        private void CreateEmptyLayoutTable(TableLayout layout, TableLayout.PhysicalTable table, IReadOnlyDictionary<string, string> types)
        {
            var parts = new List<string>();
            if (table.IsDimension)
            {
                parts.Add($"{QuoteIdent(table.KeyColumnName)} BIGINT");
            }
            else
            {
                parts.Add($"{QuoteIdent(TableLayout.RowKey)} BIGINT");
                if (ReferenceEquals(table, layout.Spine))
                {
                    foreach (var d in layout.Dimensions)
                        parts.Add($"{QuoteIdent(d.KeyColumnName)} BIGINT");
                }
            }

            foreach (var column in table.Columns)
            {
                if (!types.TryGetValue(column, out var type))
                    throw new DataBallException($"No type for column '{column}' of table '{table.Name}'");
                parts.Add($"{QuoteIdent(column)} {type}");
            }

            Execute($"CREATE TABLE {QuoteIdent(table.Name)} ({string.Join(", ", parts)})");
        }

        /// <summary>
        /// Casts the staging rows to the physical column types (dimension columns absent from
        /// staging become typed NULLs so their key hashes), numbers them after the spine's last
        /// row key, then inserts dimensions (key reuse, functional-key check), spine, and groups.
        /// </summary>
        private void InsertStagingRows(string staging, TableLayout layout, IReadOnlyList<IncomingColumn> incoming)
        {
            var qStaging = QuoteIdent(staging);
            var qCast = QuoteIdent(CastTemp);
            var qRow = QuoteIdent(TableLayout.RowKey);
            var qSpine = QuoteIdent(layout.Spine.Name);

            var physicalTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in layout.PhysicalTables)
            {
                foreach (var (name, type) in GetColumnsOf(table.Name))
                    physicalTypes[name] = type;
            }

            var byWide = incoming.ToDictionary(c => c.Wide, c => c, StringComparer.OrdinalIgnoreCase);
            var dimensionColumns = new HashSet<string>(layout.Dimensions.SelectMany(d => d.Columns), StringComparer.OrdinalIgnoreCase);
            var castParts = new List<string>
            {
                $"row_number() OVER (ORDER BY rowid) + (SELECT COALESCE(MAX({qRow}), 0) FROM {qSpine}) AS {qRow}"
            };
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var wide in layout.WideColumns)
            {
                var qWide = QuoteIdent(wide);
                var type = physicalTypes[wide];
                if (byWide.TryGetValue(wide, out var col))
                {
                    castParts.Add($"CAST({QuoteIdent(col.Staging)} AS {type}) AS {qWide}");
                    present.Add(wide);
                }
                else if (dimensionColumns.Contains(wide))
                {
                    castParts.Add($"CAST(NULL AS {type}) AS {qWide}");
                    present.Add(wide);
                }
            }

            Execute($"CREATE OR REPLACE TEMP TABLE {qCast} AS SELECT {string.Join(", ", castParts)} FROM {qStaging}");

            foreach (var d in layout.Dimensions)
            {
                var qd = QuoteIdent(d.Name);
                var qk = QuoteIdent(d.KeyColumnName);
                var cols = string.Join(", ", d.Columns.Select(QuoteIdent));
                var keyed = $"SELECT DISTINCT {TableLayout.KeyExpression(d)} AS {qk}, {cols} FROM {qCast}";
                var dup = ExecuteScalar($"""
                    SELECT COUNT(*) - COUNT(DISTINCT {qk}) FROM (
                        SELECT {qk}, {cols} FROM {qd}
                        UNION
                        {keyed}
                    )
                    """);
                if (Convert.ToInt64(dup, CultureInfo.InvariantCulture) > 0)
                {
                    throw new DataBallException(
                        $"Key columns of table '{d.Name}' ({string.Join(", ", d.KeyColumns)}) do not determine its other columns");
                }

                Execute($"""
                    INSERT INTO {qd} BY NAME
                    SELECT * FROM ({keyed}) "n"
                    WHERE NOT EXISTS (SELECT 1 FROM {qd} WHERE {qd}.{qk} = "n".{qk})
                    """);
            }

            var spineParts = new List<string> { qRow };
            spineParts.AddRange(layout.Spine.Columns.Where(present.Contains).Select(QuoteIdent));
            foreach (var d in layout.Dimensions)
                spineParts.Add($"{TableLayout.KeyExpression(d)} AS {QuoteIdent(d.KeyColumnName)}");
            Execute($"INSERT INTO {qSpine} BY NAME SELECT {string.Join(", ", spineParts)} FROM {qCast}");

            foreach (var g in layout.MeasurementGroups)
            {
                var cols = g.Columns.Where(present.Contains).ToList();
                if (cols.Count == 0)
                    continue;
                var list = string.Join(", ", cols.Select(QuoteIdent));
                var allNull = string.Join(" AND ", cols.Select(c => $"{QuoteIdent(c)} IS NULL"));
                Execute($"INSERT INTO {QuoteIdent(g.Name)} BY NAME SELECT {qRow}, {list} FROM {qCast} WHERE NOT ({allNull})");
            }
        }

        private void CreateDataView(TableLayout layout)
        {
            Execute("CREATE VIEW \"data\" AS " + layout.BuildViewSelect());
        }

        /// <summary>
        /// Best-effort cleanup of a TEMP table. Inside a joined transaction that a DuckDB error has
        /// already aborted, the DROP fails too; swallowing it keeps the original exception visible
        /// and the rollback removes the temp table anyway.
        /// </summary>
        private void DropTemp(string name)
        {
            try
            {
                Execute($"DROP TABLE IF EXISTS {QuoteIdent(name)}");
            }
            catch (DataBallException ex)
            {
                _logger.LogDebug(ex, "Could not drop temp table {Table}", name);
            }
        }
    }
}
