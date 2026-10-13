// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DuckDB.NET.Data;

namespace squalor.DataBall
{
    /// <summary>One source staged for a many-file open: its staging table, its record, the key its rows carry and how many rows it holds.</summary>
    internal sealed record StagedSource(string Table, SourceRecord Record, long Key, long Rows, bool FromHandler)
    {
        /// <summary>The last row a handler parsed for this source, as the handler yielded it; null for a file source or a handler without rows.</summary>
        internal IReadOnlyDictionary<string, object?>? LastRow { get; init; }
    }

    /// <summary>
    /// A per-source metadata column of the <c>source</c> table: a value constant within each source that varies between
    /// sources. <see cref="Values"/> has one entry per staged source, null where a source has no value.
    /// </summary>
    internal sealed record SourceMetadataColumn(string Name, string DuckDbType, IReadOnlyList<object?> Values);

    /// <summary>What one staged column holds, as far as per-source metadata needs to know.</summary>
    internal readonly record struct ColumnProfile(long Distinct, long NonNull, object? Value);

    /// <summary>
    /// A many-file open stages every source in its own staging table (<c>_stg_&lt;key&gt;</c>), reconciles per-source metadata
    /// across them, and only then builds <c>"data"</c> and the <c>source</c> table once.
    /// </summary>
    internal sealed partial class DuckDbStore
    {
        /// <summary>
        /// What a staging table is: TEMP in a memory store, where it lives in memory anyway, and a regular table in a file store, where it
        /// spills to disk instead of adding to the memory the merge needs. The open that created it drops it, or disposes the session.
        /// </summary>
        private string StagingKind => _exclusivePath is null ? "TEMP " : "";

        /// <summary>Test hook: called with the name of each staging table <see cref="StageFile"/> creates.</summary>
        internal static Action<string>? FileStagedForTests;

        /// <summary>Stages a file read by <paramref name="function"/> into staging <paramref name="table"/>, keyed to its source.</summary>
        internal void StageFile(string table, string function, string path, string extraArgs, long sourceKey)
        {
            ThrowIfDisposed();
            ValidateName(table, "Table");
            FileStagedForTests?.Invoke(table);
            Execute($"CREATE OR REPLACE {StagingKind}TABLE {QuoteIdent(table)} AS SELECT * FROM {function}({QuotePath(path)}{extraArgs})");
            KeyStaged(table, sourceKey);
        }

        /// <summary>Stages the data files of a hive directory as one source, partition columns taken from the files.</summary>
        internal void StageHiveDirectory(string table, IReadOnlyList<string> files, long sourceKey)
        {
            ThrowIfDisposed();
            ValidateName(table, "Table");
            Execute($"CREATE OR REPLACE {StagingKind}TABLE {QuoteIdent(table)} AS SELECT * FROM {ReadHiveParquet(files)}");
            KeyStaged(table, sourceKey);
        }

        /// <summary>Stages a source that produced no rows: a table with the key column only.</summary>
        internal void StageNoRows(string table)
        {
            ThrowIfDisposed();
            ValidateName(table, "Table");
            Execute($"CREATE OR REPLACE {StagingKind}TABLE {QuoteIdent(table)} ({QuoteIdent(Lineage.KeyColumn)} BIGINT)");
        }

        private void KeyStaged(string table, long sourceKey)
        {
            NormalizeIncomingColumns(table, allowKey: false);
            AddSourceKeyColumn(table, sourceKey);
            PromoteDateColumns(table);
        }

        /// <summary>
        /// Appends handler rows to staging <paramref name="table"/>, creating it from the first batch. Columns typed from
        /// <paramref name="expectedTypes"/> or the first non-null value; an all-null VARCHAR column takes the type of a
        /// later typed value, as <see cref="AddRows"/> does for <c>"data"</c>.
        /// </summary>
        internal void StageRows(
            string table,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
            IReadOnlyDictionary<string, Type>? expectedTypes)
        {
            ThrowIfDisposed();
            ValidateName(table, "Table");
            if (rows.Count == 0)
                return;
            AppendRowsTo(table, StagingKind, rows, RowColumnOrder(rows), expectedTypes);
        }

        /// <summary>True when a staging table's column holds no value at all (the reader types such a column VARCHAR, which merges with anything).</summary>
        internal bool StagedColumnIsAllNull(string table, string column)
        {
            ThrowIfDisposed();
            return IsAllNull(table, column);
        }

        /// <summary>Rows in a staging table.</summary>
        internal long StagedRowCount(string table) => RowCountOf(table);

        /// <summary>Drops a staging table a source did not finish, or one a skipped source left behind. Best effort, as for any staging table.</summary>
        internal void DropStaged(string table)
        {
            ThrowIfDisposed();
            DropTemp(table);
        }

        /// <summary>Drops <paramref name="column"/> from a staging table. The source-key column keeps the table alive.</summary>
        internal void DropStagedColumn(string table, string column)
        {
            ThrowIfDisposed();
            Execute($"ALTER TABLE {QuoteIdent(table)} DROP COLUMN {QuoteIdent(column)}");
        }

        /// <summary>For each of <paramref name="columns"/>: distinct non-null values, non-null count and one non-null value.</summary>
        internal IReadOnlyDictionary<string, ColumnProfile> ProfileColumns(string table, IReadOnlyList<string> columns)
        {
            ThrowIfDisposed();
            var result = new Dictionary<string, ColumnProfile>(StringComparer.OrdinalIgnoreCase);
            if (columns.Count == 0)
                return result;
            var select = string.Join(", ", columns.Select((c, i) =>
            {
                var q = QuoteIdent(c);
                return $"COUNT(DISTINCT {q}) AS d{i}, COUNT({q}) AS n{i}, any_value({q}) AS v{i}";
            }));
            var row = Query($"SELECT {select} FROM {QuoteIdent(table)}")[0];
            for (var i = 0; i < columns.Count; i++)
            {
                result[columns[i]] = new ColumnProfile(
                    Convert.ToInt64(row[$"d{i}"], CultureInfo.InvariantCulture),
                    Convert.ToInt64(row[$"n{i}"], CultureInfo.InvariantCulture),
                    row[$"v{i}"]);
            }
            return result;
        }

        /// <summary>The first non-null value of a staged column, or null when it has none.</summary>
        internal object? FirstNonNull(string table, string column)
        {
            var q = QuoteIdent(column);
            var rows = Query($"SELECT {q} AS v FROM {QuoteIdent(table)} WHERE {q} IS NOT NULL LIMIT 1");
            return rows.Count == 0 ? null : rows[0]["v"];
        }

        /// <summary>
        /// The columns <c>"data"</c> gets from the staged sources, in order of first appearance. A column present in several sources takes the
        /// merged type of its non-empty occurrences (the rules of an append: equal types, wider integer, FLOAT/integer to DOUBLE, DATE to
        /// TIMESTAMP); an all-null occurrence adopts the type of the others; a configured column keeps the type of the first source that has it.
        /// Throws, naming the source, for the first column whose types cannot merge. Reads only.
        /// </summary>
        private List<(string Name, string DuckDbType)> PlanStagedSchema(IReadOnlyList<StagedSource> staged, IReadOnlyDictionary<string, Type>? configTypes)
        {
            var order = new List<string>();
            var state = new Dictionary<string, (string Type, List<string> Contributors, string FirstPath)>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in staged)
            {
                foreach (var (name, type) in GetColumnsOf(s.Table))
                {
                    if (name.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!state.TryGetValue(name, out var current))
                    {
                        order.Add(name);
                        state[name] = (type, new List<string> { s.Table }, s.Record.Path);
                        continue;
                    }
                    if (configTypes?.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) == true)
                        continue;
                    if (NormalizeType(current.Type) == NormalizeType(type))
                    {
                        current.Contributors.Add(s.Table);
                        continue;
                    }
                    var decision = DecideColumnMerge(current.Type, type,
                        () => IsAllNull(s.Table, name), () => current.Contributors.All(t => IsAllNull(t, name)));
                    if (decision.Kind == ColumnMerge.Conflict)
                    {
                        throw new DataBallException(
                            $"Cannot merge '{s.Record.Path}': column '{name}' has type {current.Type} in the sources before it (first seen in '{current.FirstPath}') and {type} in this one");
                    }
                    current.Contributors.Add(s.Table);
                    state[name] = (decision.Type, current.Contributors, current.FirstPath);
                }
            }
            return order.Select(n => (n, state[n].Type)).ToList();
        }

        /// <summary>
        /// Builds <c>"data"</c> from the staged sources, in source order, dropping each staging table once its rows are in.
        /// Columns a source lacks are NULL; <c>_source_key</c> is the last column. Nothing is created when no source holds a data column.
        /// </summary>
        internal void BuildDataFromStaged(IReadOnlyList<StagedSource> staged, IReadOnlyDictionary<string, Type>? configTypes)
        {
            ThrowIfDisposed();
            var merged = PlanStagedSchema(staged, configTypes);
            var key = QuoteIdent(Lineage.KeyColumn);
            var first = true;
            foreach (var s in staged)
            {
                if (merged.Count > 0)
                {
                    var have = GetColumnsOf(s.Table).ToDictionary(c => c.Name, c => c.DuckDbType, StringComparer.OrdinalIgnoreCase);
                    var parts = new List<string>(merged.Count + 1);
                    foreach (var (name, type) in merged)
                    {
                        var q = QuoteIdent(name);
                        if (!have.TryGetValue(name, out var haveType))
                            parts.Add($"CAST(NULL AS {type}) AS {q}");
                        else if (NormalizeType(haveType) == NormalizeType(type))
                            parts.Add(q);
                        else
                            parts.Add($"CAST({q} AS {type}) AS {q}");
                    }
                    parts.Add(key);
                    var select = $"SELECT {string.Join(", ", parts)} FROM {QuoteIdent(s.Table)}";
                    Execute(first ? $"CREATE TABLE \"data\" AS {select}" : $"INSERT INTO \"data\" BY NAME {select}");
                    first = false;
                }
                DropTemp(s.Table);
            }
        }

        /// <summary>
        /// Creates the <c>source</c> table with one row per staged source and the per-source metadata <paramref name="columns"/>
        /// that go with them. Runs inside the caller's transaction, once, so no column is added after a row is written.
        /// </summary>
        internal void CreateSourceTable(IReadOnlyList<StagedSource> staged, IReadOnlyList<SourceMetadataColumn> columns)
        {
            ThrowIfDisposed();
            Execute("DROP TABLE IF EXISTS \"source\"");
            var definitions = string.Concat(columns.Select(c => $", {QuoteIdent(c.Name)} {c.DuckDbType}"));
            Execute($"CREATE TABLE \"source\" ({SourceColumnDefinitions}{definitions})");
            var names = string.Concat(columns.Select(c => ", " + QuoteIdent(c.Name)));
            var holders = string.Concat(columns.Select((c, i) => $", CAST($m{i} AS {c.DuckDbType})"));
            var sql = $"INSERT INTO \"source\" ({SourceColumnList}{names}) VALUES ($key, $path, $sha, $type, $bytes, $modified, $added{holders})";
            var added = DateTime.UtcNow;
            for (var i = 0; i < staged.Count; i++)
            {
                var r = staged[i].Record;
                var parameters = new List<DuckDBParameter>
                {
                    Param("key", staged[i].Key), Param("path", r.Path), Param("sha", r.Sha256), Param("type", r.Type),
                    Param("bytes", r.Bytes), Param("modified", r.ModifiedUtc), Param("added", added),
                };
                for (var c = 0; c < columns.Count; c++)
                    parameters.Add(Param($"m{c}", columns[c].Values[i]));
                ExecuteParameterized(sql, parameters.ToArray());
            }
        }
    }
}
