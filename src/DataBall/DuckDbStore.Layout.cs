// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace squalor.DataBall
{
    /// <summary>
    /// Config-declared table layout: the wide relation <c>"data"</c> becomes a view over
    /// dimension, spine, and measurement-group tables. Every legacy write path stays
    /// hard-coded to the base table; these members are the only ones that know about views.
    /// </summary>
    internal sealed partial class DuckDbStore
    {
        private const string WideTemp = "_wide";

        /// <summary>Layout bound to this store, or null for a single wide table.</summary>
        internal TableLayout? Layout { get; private set; }

        /// <summary>True when <c>"data"</c> exists and is a VIEW.</summary>
        internal bool DataIsView()
        {
            ThrowIfDisposed();
            var result = ExecuteScalar("""
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema = 'main' AND table_name = 'data' AND table_type = 'VIEW'
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

            var wide = GetColumns().Select(c => c.Name).ToList();
            var layout = TableLayout.Build(config, wide);
            foreach (var table in layout.PhysicalTables)
            {
                if (TableExists(table.Name))
                    throw new DataBallException($"Table '{table.Name}' already exists; cannot build the layout");
            }

            try
            {
                InTransaction(() =>
                {
                    Execute($"""
                        CREATE OR REPLACE TEMP TABLE {QuoteIdent(WideTemp)} AS
                        SELECT row_number() OVER (ORDER BY rowid) AS {QuoteIdent(TableLayout.RowKey)}, * FROM "data"
                        """);
                    CreateLayoutTablesFromWide(layout, WideTemp);
                    Execute("DROP TABLE \"data\"");
                    CreateDataView(layout);
                });
            }
            finally
            {
                Execute($"DROP TABLE IF EXISTS {QuoteIdent(WideTemp)}");
            }

            Layout = layout;
            return layout;
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
            return layout;
        }

        /// <summary>
        /// Drops the <c>"data"</c> relation: the view plus its physical tables when a layout is
        /// bound, otherwise the base table. Callers wrap this in the replacing transaction.
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
                Layout = null;
                return;
            }

            Execute("DROP TABLE IF EXISTS \"data\"");
            Layout = null;
        }

        /// <summary>
        /// Rebuilds the layout from one parquet file per physical table (a <c>.ball</c> v2), then
        /// creates the wide view. Replaces any existing <c>"data"</c> relation. Atomic.
        /// </summary>
        internal void LoadLayoutTables(TableLayout layout, IReadOnlyDictionary<string, string> parquetByTable)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(layout);
            ArgumentNullException.ThrowIfNull(parquetByTable);
            InTransaction(() =>
            {
                DropDataRelation();
                foreach (var table in layout.PhysicalTables)
                {
                    if (!parquetByTable.TryGetValue(table.Name, out var path))
                        throw new DataBallException($"Missing parquet for table '{table.Name}'");
                    if (TableExists(table.Name))
                        throw new DataBallException($"Table '{table.Name}' already exists; cannot build the layout");
                    Execute($"CREATE TABLE {QuoteIdent(table.Name)} AS SELECT * FROM read_parquet({QuotePath(path)})");
                    var actual = GetColumnsOf(table.Name).Select(c => c.Name).ToList();
                    foreach (var expected in ExpectedPhysicalColumns(layout, table))
                    {
                        if (!actual.Any(a => a.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                            throw new DataBallException($"Table '{table.Name}' parquet is missing column '{expected}'");
                    }
                }
                CreateDataView(layout);
            });
            Layout = layout;
        }

        /// <summary>Writes one physical table to parquet.</summary>
        internal void ExportTableParquet(string tableName, string path)
        {
            ThrowIfDisposed();
            ValidateName(tableName, "Table");
            if (!TableExists(tableName))
                throw new DataBallException($"Table '{tableName}' does not exist");
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            Execute($"COPY (SELECT * FROM {QuoteIdent(tableName)}) TO {QuotePath(path)} (FORMAT PARQUET)");
        }

        /// <summary>Row count of one physical table.</summary>
        internal long RowCountOfTable(string tableName)
        {
            ThrowIfDisposed();
            ValidateName(tableName, "Table");
            return RowCountOf(tableName);
        }

        internal static IEnumerable<string> ExpectedPhysicalColumns(TableLayout layout, TableLayout.PhysicalTable table)
        {
            if (table.IsDimension)
            {
                yield return table.KeyColumnName;
            }
            else
            {
                yield return TableLayout.RowKey;
                if (ReferenceEquals(table, layout.Spine))
                {
                    foreach (var d in layout.Dimensions)
                        yield return d.KeyColumnName;
                }
            }

            foreach (var column in table.Columns)
                yield return column;
        }

        private void CreateLayoutTablesFromWide(TableLayout layout, string wideTable)
        {
            var qWide = QuoteIdent(wideTable);
            var qRow = QuoteIdent(TableLayout.RowKey);

            foreach (var d in layout.Dimensions)
            {
                var qd = QuoteIdent(d.Name);
                var qk = QuoteIdent(d.KeyColumnName);
                var cols = string.Join(", ", d.Columns.Select(QuoteIdent));
                Execute($"CREATE TABLE {qd} AS SELECT DISTINCT {TableLayout.KeyExpression(d)} AS {qk}, {cols} FROM {qWide}");
                var dup = ExecuteScalar($"SELECT COUNT(*) - COUNT(DISTINCT {qk}) FROM {qd}");
                if (Convert.ToInt64(dup, CultureInfo.InvariantCulture) > 0)
                {
                    throw new DataBallException(
                        $"Key columns of table '{d.Name}' ({string.Join(", ", d.KeyColumns)}) do not determine its other columns");
                }
            }

            var spineParts = new List<string> { qRow };
            spineParts.AddRange(layout.Spine.Columns.Select(QuoteIdent));
            foreach (var d in layout.Dimensions)
                spineParts.Add($"{TableLayout.KeyExpression(d)} AS {QuoteIdent(d.KeyColumnName)}");
            Execute($"CREATE TABLE {QuoteIdent(layout.Spine.Name)} AS SELECT {string.Join(", ", spineParts)} FROM {qWide}");

            foreach (var g in layout.MeasurementGroups)
            {
                var cols = string.Join(", ", g.Columns.Select(QuoteIdent));
                var allNull = string.Join(" AND ", g.Columns.Select(c => $"{QuoteIdent(c)} IS NULL"));
                Execute($"CREATE TABLE {QuoteIdent(g.Name)} AS SELECT {qRow}, {cols} FROM {qWide} WHERE NOT ({allNull})");
            }
        }

        private void CreateDataView(TableLayout layout)
        {
            Execute("CREATE VIEW \"data\" AS " + layout.BuildViewSelect());
        }
    }
}
