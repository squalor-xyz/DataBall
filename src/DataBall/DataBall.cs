// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using squalor.DataBall.Export;
using squalor.DataBall.Import;

namespace squalor.DataBall
{
    /// <summary>
    /// Represents a versatile data handling class for test executive applications.
    /// Instances are not thread-safe; one session per owner.
    /// </summary>
    public sealed partial class DataBall : IDisposable
    {
        private readonly ILogger _logger;
        private readonly DuckDbStore _store;
        private readonly Dictionary<string, Type> _expectedColumnTypes;
        private readonly List<Relationship> _relationships;
        private Config _config;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataBall"/> class, optionally loading configuration.
        /// Pass <paramref name="databasePath"/> by name for a file-backed session
        /// (<c>new DataBall(databasePath: "session.duckdb")</c>). The first string is always config JSON.
        /// </summary>
        /// <param name="configPath">The path to the configuration JSON file, if any.</param>
        /// <param name="logger">Optional logger. Defaults to a no-op logger.</param>
        /// <param name="databasePath">Optional DuckDB file. Null or empty is <c>:memory:</c>. Filename <c>catalog.duckdb</c> is rejected.</param>
        public DataBall(string? configPath = null, ILogger? logger = null, string? databasePath = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _store = new DuckDbStore(ValidateDatabasePath(databasePath), _logger);
            _expectedColumnTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            _relationships = new List<Relationship>();
            try
            {
                _config = string.IsNullOrEmpty(configPath)
                    ? Config.CreateDefaults()
                    : Config.LoadMerged(configPath);
                ApplyConfig(_config);
                InitializeLayout();
            }
            catch
            {
                _store.Dispose();
                throw;
            }
        }

        private static string? ValidateDatabasePath(string? databasePath)
        {
            if (string.IsNullOrEmpty(databasePath)
                || databasePath.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
                return databasePath;

            if (string.IsNullOrWhiteSpace(databasePath))
                throw new DataBallException("Database path is required");

            var full = Path.GetFullPath(databasePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(full);
            if (name.Equals("catalog.duckdb", StringComparison.OrdinalIgnoreCase))
                throw new DataBallException("catalog.duckdb is not a DataBall session file");

            return databasePath;
        }

        /// <summary>
        /// Gets a snapshot of the metadata dictionary.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Metadata
        {
            get
            {
                ThrowIfDisposed();
                return _store.SnapshotMetadata();
            }
        }

        internal DuckDbStore Store
        {
            get
            {
                ThrowIfDisposed();
                return _store;
            }
        }

        internal ILogger Logger => _logger;

        internal IReadOnlyDictionary<string, Type> ExpectedColumnTypes => _expectedColumnTypes;

        internal IReadOnlyList<Relationship> Relationships => _relationships;

        /// <summary>Bound table layout, or null when <c>"data"</c> is one wide table.</summary>
        internal TableLayout? Layout => _store.Layout;

        /// <summary>True when the merged config declares a <c>tables</c> layout.</summary>
        internal bool HasLayoutConfig => _config.Tables.Count > 0;

        /// <summary>The live merged config (not a clone); for store binding only.</summary>
        internal Config CurrentConfig => _config;

        /// <summary>
        /// Gets a snapshot of the merged schema config (native defaults plus any overlay file).
        /// Mutating the returned object does not affect the session.
        /// </summary>
        public Config Schema => _config.Clone();

        /// <summary>
        /// Sets a metadata value.
        /// </summary>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The metadata value.</param>
        public void SetMetadata(string key, object? value)
        {
            ThrowIfDisposed();
            _store.SetMetadata(key, value);
        }

        /// <summary>
        /// Adds a column of primitive values.
        /// </summary>
        /// <typeparam name="T">The type of the column values, which must be a non-nullable value type.</typeparam>
        /// <param name="name">The name of the column.</param>
        /// <param name="values">The values for the column.</param>
        public void AddColumn<T>(string name, IEnumerable<T> values) where T : struct
        {
            ThrowIfDisposed();
            ThrowIfLayoutDeclared("AddColumn");
            ArgumentNullException.ThrowIfNull(values);
            EnsureExpectedType(name, typeof(T));
            var list = values as IReadOnlyList<T> ?? values.ToList();
            _store.AddColumn(name, list);
            _expectedColumnTypes[name] = typeof(T);
            RememberAddedColumn(name, list.Count == 0 ? null : list[list.Count - 1]);
        }

        /// <summary>
        /// Adds a column of string values.
        /// </summary>
        /// <param name="name">The name of the column.</param>
        /// <param name="values">The string values for the column.</param>
        public void AddColumn(string name, IEnumerable<string?> values)
        {
            ThrowIfDisposed();
            ThrowIfLayoutDeclared("AddColumn");
            ArgumentNullException.ThrowIfNull(values);
            EnsureExpectedType(name, typeof(string));
            var list = values as IReadOnlyList<string?> ?? values.ToList();
            _store.AddColumn(name, list);
            _expectedColumnTypes[name] = typeof(string);
            RememberAddedColumn(name, list.Count == 0 ? null : list[list.Count - 1]);
        }

        /// <summary>
        /// Removes a column.
        /// </summary>
        /// <param name="name">The name of the column to remove.</param>
        public void RemoveColumn(string name)
        {
            ThrowIfDisposed();
            ThrowIfLayoutDeclared("RemoveColumn");
            _store.RemoveColumn(name);
            _expectedColumnTypes.Remove(name);
        }

        /// <summary>
        /// Inserts a row of values, creating the table or adding missing columns as needed.
        /// </summary>
        /// <param name="values">The column values for the new row.</param>
        public void AddRow(IReadOnlyDictionary<string, object?> values)
        {
            ThrowIfDisposed();
            _store.AddRow(values, _expectedColumnTypes);
            RememberRow(values);
            SplitIfLayout();
        }

        /// <summary>
        /// Inserts many rows with one DuckDB appender. Does not apply trigger/reset (<see cref="CommitRow"/>).
        /// Empty <paramref name="rows"/> is a no-op.
        /// </summary>
        public void AddRows(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
        {
            ThrowIfDisposed();
            if (rows is null)
                throw new DataBallException("Row values are required");
            var list = rows as IReadOnlyList<IReadOnlyDictionary<string, object?>> ?? rows.ToList();
            if (list.Count == 0)
                return;
            _store.AddRows(list, _expectedColumnTypes);
            RememberRow(list[list.Count - 1]);
            SplitIfLayout();
        }

        /// <summary>
        /// Executes SQL against the in-memory store and returns rows as dictionaries.
        /// Each row is a caller-owned copy; mutating it does not change the store.
        /// </summary>
        /// <param name="sql">The SQL to execute.</param>
        /// <returns>Query results as dictionaries keyed by column name.</returns>
        /// <exception cref="DataBallException">Thrown when the SQL is empty or execution fails.</exception>
        public IReadOnlyList<Dictionary<string, object?>> Query(string sql)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(sql))
                throw new DataBallException("SQL is required");
            try
            {
                return _store.Query(sql);
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                throw new DataBallException("Query failed", ex);
            }
        }

        /// <summary>
        /// Merges or appends another DataBall, handling schema differences.
        /// </summary>
        /// <param name="other">The DataBall to merge or append.</param>
        /// <param name="append">If true, appends data; otherwise, replaces existing data.</param>
        public void MergeOrAppend(DataBall other, bool append)
        {
            ThrowIfDisposed();
            ThrowIfPendingRow();
            ArgumentNullException.ThrowIfNull(other);
            ObjectDisposedException.ThrowIf(other._disposed, other);

            _logger.LogDebug("Merging or appending DataBall");
            try
            {
                if (!other.Store.DataTableExists())
                {
                    if (!append || !_store.DataTableExists() || _store.RowCount() == 0)
                        RunImport(append: false, () => _store.DropDataRelation());
                    _logger.LogDebug("Merge/append completed");
                    return;
                }

                var dir = Path.Combine(Path.GetTempPath(), "databall-merge-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var path = Path.Combine(dir, "data.parquet");
                    other.Store.ExportParquet(path);
                    RunImport(append, () => _store.ImportParquet(path, append));
                }
                finally
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                _logger.LogDebug("Merge/append completed");
            }
            catch (Exception ex) when (ex is not DataBallException and not ObjectDisposedException)
            {
                _logger.LogError(ex, "Merge/append failed");
                throw new DataBallException("Failed to merge or append DataBall", ex);
            }
        }

        /// <summary>
        /// Compacts in-memory data: extracts strict constants (one distinct non-null value and no nulls)
        /// into metadata, then DISTINCT. The last remaining constant column is extracted too; if that
        /// leaves no columns, the data table is dropped (DuckDB cannot store a 0-column table).
        /// Columns named in <paramref name="partitionColumns"/> are not extracted so hive directories
        /// can still be written. Optionally exports hive-partitioned Parquet when both a path and
        /// partition columns are supplied.
        /// </summary>
        /// <param name="partitionedParquetPath">The path to export partitioned Parquet files, if any.</param>
        /// <param name="partitionColumns">The columns to partition by, if any.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="DataBallException">Thrown when the Bounce operation fails.</exception>
        public Task Bounce(string? partitionedParquetPath = null, string[]? partitionColumns = null)
        {
            ThrowIfDisposed();
            ThrowIfPendingRow();
            ThrowIfMultiTable("Bounce");
            _logger.LogInformation("Starting Bounce operation");
            try
            {
                if (!_store.DataTableExists())
                {
                    _logger.LogDebug("Bounce skipped; no data table");
                    return Task.CompletedTask;
                }

                var writePartitioned = !string.IsNullOrEmpty(partitionedParquetPath) && partitionColumns is { Length: > 0 };
                // Resolve against the pre-extraction schema so a typo names the missing column instead of exporting after DROP TABLE.
                if (writePartitioned)
                    _store.ResolvePartitionColumns(partitionColumns!);

                var expectedSnap = new Dictionary<string, Type>(_expectedColumnTypes, StringComparer.OrdinalIgnoreCase);
                try
                {
                    _store.InTransaction(() =>
                    {
                        ExtractConstantsToMetadataSql(partitionColumns);
                        DistinctInPlace();
                    });
                }
                catch
                {
                    _store.ReloadMetadata();
                    _expectedColumnTypes.Clear();
                    foreach (var pair in expectedSnap)
                        _expectedColumnTypes[pair.Key] = pair.Value;
                    throw;
                }

                PruneLastCommittedRow();
                if (writePartitioned)
                {
                    _store.ExportPartitionedParquet(partitionedParquetPath!, partitionColumns!);
                    _logger.LogInformation($"Exported to partitioned Parquet at {partitionedParquetPath}");
                }
                _logger.LogInformation("Bounce operation completed");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bounce operation failed");
                throw new DataBallException("Bounce operation failed", ex);
            }
        }

        /// <summary>
        /// Performs the Squish operation. Delegates to <see cref="Bounce"/> (same compaction:
        /// strict no-nulls constant extraction and DISTINCT). Optionally writes hive-partitioned Parquet.
        /// </summary>
        /// <param name="partitionedParquetPath">The path to export partitioned Parquet files, if any.</param>
        /// <param name="partitionColumns">The columns to partition by, if any.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        /// <exception cref="DataBallException">Thrown when the Squish operation fails.</exception>
        public Task Squish(string? partitionedParquetPath = null, string[]? partitionColumns = null)
        {
            return Bounce(partitionedParquetPath, partitionColumns);
        }

        /// <summary>
        /// Imports data from a file, detecting the format by extension.
        /// Column types come from config or <c>AddColumn</c>. Untyped CSV integers stay DuckDB BIGINT / <c>long</c>.
        /// </summary>
        /// <param name="path">The path of the file to import.</param>
        /// <param name="options">Optional import settings. <see cref="ImportOptions.Append"/> defaults to <c>false</c>.</param>
        /// <returns>A completed task after the import finishes.</returns>
        /// <exception cref="DataBallException">Thrown when the path is missing, the format is unknown, or import fails.</exception>
        public Task ImportAsync(string path, ImportOptions? options = null)
        {
            ThrowIfDisposed();
            ThrowIfPendingRow();
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");

            var format = DetectFormat(path);
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new DataBallException($"File not found: {path}");

            options ??= new ImportOptions();
            var append = options.Append;
            _logger.LogInformation("Importing {Path} as {Format}, append={Append}", path, format, append);
            try
            {
                void Import()
                {
                    switch (format)
                    {
                        case ExportType.Csv:
                            ImportCsvWithSchema(path, append);
                            break;
                        case ExportType.Parquet:
                            _store.ImportParquet(path, append);
                            break;
                        case ExportType.Archive:
                            ImportManager.ImportFromArchiveCore(this, path, append);
                            break;
                        case ExportType.Ball:
                            ImportManager.ImportFromBallCore(this, path, append);
                            break;
                        default:
                            throw new DataBallException($"Unknown import format for '{path}'");
                    }
                }

                RunImport(append, Import);
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex is not DataBallException and not ObjectDisposedException)
            {
                _logger.LogError(ex, "Import failed");
                throw new DataBallException("Failed to import", ex);
            }
        }

        /// <summary>
        /// Exports data in the specified format.
        /// </summary>
        /// <param name="path">The destination path.</param>
        /// <param name="type">The export format.</param>
        /// <returns>A completed task after the export finishes.</returns>
        /// <exception cref="DataBallException">Thrown when export fails.</exception>
        public Task ExportAsync(string path, ExportType type)
        {
            ThrowIfDisposed();
            ThrowIfPendingRow();
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");

            _logger.LogInformation("Exporting to {Path} as {Type}", path, type);
            try
            {
                switch (type)
                {
                    case ExportType.Csv:
                        _store.ExportCsv(path, FilteredSelectOrNull());
                        break;
                    case ExportType.Parquet:
                        _store.ExportParquet(path, FilteredSelectOrNull());
                        break;
                    case ExportType.Archive:
                        ExportManager.ExportToArchive(this, path);
                        break;
                    case ExportType.Ball:
                        ExportManager.ExportToBall(this, path);
                        break;
                    default:
                        throw new DataBallException($"Unsupported export type: {type}");
                }
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex is not DataBallException and not ObjectDisposedException)
            {
                _logger.LogError(ex, "Export failed");
                throw new DataBallException($"Failed to export to {type}", ex);
            }
        }

        /// <summary>
        /// Saves the data to the specified path using the .ball format.
        /// </summary>
        /// <param name="path">The path to save the .ball file.</param>
        /// <returns>A completed task after the save finishes.</returns>
        /// <exception cref="DataBallException">Thrown when the save operation fails.</exception>
        public Task SaveAsync(string path)
        {
            ThrowIfDisposed();
            return ExportAsync(path, ExportType.Ball);
        }

        /// <summary>
        /// Exports data in the specified format.
        /// </summary>
        /// <param name="type">The export format.</param>
        /// <param name="path">The destination path.</param>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public void Roll(ExportType type, string path)
        {
            ThrowIfDisposed();
            ExportAsync(path, type).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Exports the data to an archive (ZIP, TAR.GZ, or TAR.XZ).
        /// </summary>
        /// <param name="path">The path to save the archive.</param>
        /// <exception cref="DataBallException">Thrown when the export operation fails.</exception>
        public void ExportToArchive(string path)
        {
            ThrowIfDisposed();
            ExportAsync(path, ExportType.Archive).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Saves the data to the specified path using the .ball format.
        /// </summary>
        /// <param name="path">The path to save the .ball file.</param>
        /// <exception cref="DataBallException">Thrown when the save operation fails.</exception>
        public void Save(string path)
        {
            ThrowIfDisposed();
            SaveAsync(path).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Releases the underlying DuckDB connection.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            if (_pendingRow is not null)
                _logger.LogWarning("Disposing with an uncommitted pending row; it will be discarded.");
            _store.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void ExtractConstantsToMetadataSql(string[]? partitionColumns)
        {
            if (!_store.DataTableExists() || _store.RowCount() == 0)
                return;

            // Hive PARTITION_BY needs these columns left in the table.
            HashSet<string>? skip = null;
            if (partitionColumns is { Length: > 0 })
                skip = new HashSet<string>(partitionColumns, StringComparer.OrdinalIgnoreCase);

            foreach (var (name, _) in _store.GetColumns().ToList())
            {
                // Last remaining constant column drops "data"; stop rather than query a missing table.
                if (!_store.DataTableExists())
                    break;
                if (skip is not null && skip.Contains(name))
                    continue;

                var q = DuckDbStore.QuoteIdent(name);
                var rows = _store.Query($"""
                    SELECT
                      COUNT(DISTINCT {q}) FILTER (WHERE {q} IS NOT NULL) AS d,
                      COUNT(*) FILTER (WHERE {q} IS NULL) AS n,
                      any_value({q}) AS v
                    FROM "data"
                    """);
                var d = Convert.ToInt64(rows[0]["d"], CultureInfo.InvariantCulture);
                var n = Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture);
                if (d == 1 && n == 0)
                {
                    _store.SetMetadata(name, rows[0]["v"]);
                    _store.RemoveColumn(name);
                    _expectedColumnTypes.Remove(name);
                }
            }
        }

        private void DistinctInPlace()
        {
            // Extracting the last constant column drops "data"; DuckDB cannot DISTINCT a 0-column table.
            if (!_store.DataTableExists())
                return;
            _store.Execute("CREATE OR REPLACE TABLE \"data\" AS SELECT DISTINCT * FROM \"data\"");
        }

        private void EnsureExpectedType(string name, Type actual)
        {
            if (_expectedColumnTypes.TryGetValue(name, out var expected) && expected != actual)
                throw new DataBallException($"Column '{name}' expected type {expected.Name}, got {actual.Name}");
        }

        internal void ApplyImportedConfig(Config config)
        {
            ArgumentNullException.ThrowIfNull(config);
            _config = Config.Merge(_config, config);
            ApplyConfig(_config);
            _store.UpdateLayoutConfig(_config);
        }

        /// <summary>
        /// Re-applies the constant metadata of an imported config so it wins over
        /// <c>metadata.json</c>, as it did when config was applied last.
        /// </summary>
        internal void ApplyImportedConfigMetadata(Config config)
        {
            ArgumentNullException.ThrowIfNull(config);
            foreach (var (key, value) in config.Metadata)
                _store.SetMetadata(key, DuckDbStore.Unwrap(value));
        }

        /// <summary>
        /// Detects import/export format from a path. Directories are Parquet (hive).
        /// Compound suffixes (<c>.tar.gz</c>, <c>.tar.xz</c>) are matched before short ones.
        /// </summary>
        public static ExportType DetectFormat(string path)
        {
            if (Directory.Exists(path))
                return ExportType.Parquet;

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName))
                throw new DataBallException($"Unknown import format for '{path}'");

            if (EndsWithSuffix(fileName, ".tar.gz") || EndsWithSuffix(fileName, ".tgz")
                || EndsWithSuffix(fileName, ".tar.xz") || EndsWithSuffix(fileName, ".txz")
                || EndsWithSuffix(fileName, ".tar") || EndsWithSuffix(fileName, ".zip"))
                return ExportType.Archive;
            if (EndsWithSuffix(fileName, ".ball"))
                return ExportType.Ball;
            if (EndsWithSuffix(fileName, ".csv"))
                return ExportType.Csv;
            if (EndsWithSuffix(fileName, ".parquet"))
                return ExportType.Parquet;
            throw new DataBallException($"Unknown import format for '{path}'");
        }

        private static bool EndsWithSuffix(string fileName, string suffix)
            => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Imports a CSV and applies the CSV schema. Legacy sessions apply it to <c>"data"</c> after
        /// the merge (unchanged behavior); layout sessions apply it to the staging table before
        /// the rows are routed, because <c>"data"</c> is a view.
        /// </summary>
        internal void ImportCsvWithSchema(string path, bool append)
        {
            if (HasLayoutConfig)
            {
                _store.ImportCsv(path, append, _expectedColumnTypes, t => ApplyCsvSchema(t, layoutAppend: append && _store.Layout is not null));
                return;
            }

            _store.ImportCsv(path, append, _expectedColumnTypes);
            ApplyCsvSchema();
        }

        /// <summary>
        /// Renames raw headers to canonical names, coerces configured types, and extracts
        /// configured metadata fields on <paramref name="table"/>. With <paramref name="layoutAppend"/>
        /// (staging rows joining a live layout) fields already in metadata are left in place for
        /// the append reconciliation, and no column is dropped here.
        /// </summary>
        internal void ApplyCsvSchema(string table = "data", bool layoutAppend = false)
        {
            if (!_store.TableExists(table))
                return;

            var headers = _store.GetColumnsOf(table)
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (headers.Count == 0)
                return;

            var parsed = SchemaResolver.ResolveAll(headers, _config);
            var tableCols = _store.GetColumnsOf(table);
            foreach (var col in parsed)
            {
                if (col.ClrType is not null)
                    _expectedColumnTypes[col.Name] = col.ClrType;
                var match = tableCols.FirstOrDefault(c => c.Name.Equals(col.Raw, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    match = tableCols.FirstOrDefault(c => c.Name.Equals(col.Name, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    continue;
                if (!match.Name.Equals(col.Name, StringComparison.Ordinal))
                    _store.RenameColumnOf(table, match.Name, col.Name);
            }

            _store.CoerceColumnsOf(table, _expectedColumnTypes);
            ExtractConfiguredMetadata(table, layoutAppend);
        }

        private void ApplyConfig(Config config)
        {
            foreach (var (key, value) in config.Metadata)
                _store.SetMetadata(key, DuckDbStore.Unwrap(value));
            foreach (var col in config.Columns)
                _expectedColumnTypes[col.Key] = Config.ParseColumnType(col.Value);
            if (config.Relationships.Count > 0)
            {
                _relationships.Clear();
                _relationships.AddRange(config.Relationships);
            }
        }

        private void ExtractConfiguredMetadata(string table, bool layoutAppend)
        {
            if (!_store.TableExists(table) || _config.MetadataFields.Count == 0)
                return;

            var qTable = DuckDbStore.QuoteIdent(table);
            var policy = _config.MetadataPolicy ?? "requireConstant";
            var existing = _store.SnapshotMetadata();
            foreach (var field in _config.MetadataFields.ToList())
            {
                var cols = _store.GetColumnsOf(table);
                var match = cols.FirstOrDefault(c => c.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    continue;
                if (layoutAppend && existing.ContainsKey(match.Name))
                    continue; // the append reconciliation compares against the stored value

                var q = DuckDbStore.QuoteIdent(match.Name);
                var rows = _store.Query($"""
                    SELECT
                      COUNT(DISTINCT {q}) FILTER (WHERE {q} IS NOT NULL) AS d,
                      COUNT(*) FILTER (WHERE {q} IS NULL) AS n,
                      any_value({q}) AS v
                    FROM {qTable}
                    """);
                var distinct = Convert.ToInt64(rows[0]["d"], CultureInfo.InvariantCulture);
                var nulls = Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture);
                var value = rows[0]["v"];

                if (policy.Equals("first", StringComparison.OrdinalIgnoreCase))
                {
                    var first = _store.Query($"SELECT {q} FROM {qTable} WHERE {q} IS NOT NULL LIMIT 1");
                    if (first.Count > 0)
                        _store.SetMetadata(match.Name, first[0][match.Name]);
                    if (!layoutAppend)
                    {
                        _store.RemoveColumnOf(table, match.Name);
                        _expectedColumnTypes.Remove(match.Name);
                    }
                    continue;
                }

                if (distinct == 1 && (nulls == 0 || policy.Equals("bounce", StringComparison.OrdinalIgnoreCase) is false))
                {
                    _store.SetMetadata(match.Name, value);
                    if (!layoutAppend)
                    {
                        _store.RemoveColumnOf(table, match.Name);
                        _expectedColumnTypes.Remove(match.Name);
                    }
                    continue;
                }

                if (policy.Equals("requireConstant", StringComparison.OrdinalIgnoreCase) && distinct > 1)
                    throw new DataBallException($"Metadata field '{match.Name}' is not constant");
            }
        }

        /// <summary>
        /// Constructor step: bind or build the layout. A view-backed file needs a config with
        /// <c>tables</c>; a wide file opened with <c>tables</c> is split; a bad layout fails now.
        /// </summary>
        private void InitializeLayout()
        {
            if (_store.DataIsView())
            {
                if (!HasLayoutConfig)
                    throw new DataBallException("Session file has a multi-table layout; open it with the config that declares 'tables'");
                _store.BindExistingLayout(_config);
                return;
            }

            if (!HasLayoutConfig)
                return;

            TableLayout.Validate(_config);
            SplitIfLayout();
        }

        /// <summary>
        /// Splits the wide base table into the declared layout when one is configured and
        /// <c>"data"</c> is still a base table. No-op otherwise.
        /// </summary>
        internal void SplitIfLayout()
        {
            if (!HasLayoutConfig || _store.Layout is not null)
                return;
            if (!_store.DataTableExists() || _store.DataIsView())
                return;
            _store.SplitIntoLayout(_config);
            _logger.LogInformation("Split \"data\" into tables: {Tables}", string.Join(", ", _store.Layout!.PhysicalTableNames));
        }

        /// <summary>
        /// Runs an import as one transaction. On a layout session a replace drops the view and its
        /// tables first; afterwards the result is split (or the layout cleared when no data
        /// remains). A <c>.ball</c> <c>config.json</c> may change the config mid-import, so the
        /// in-memory mirrors (config, expected types, relationships, layout, metadata) are
        /// snapshotted first and restored when the transaction rolls back.
        /// </summary>
        internal void RunImport(bool append, Action import)
        {
            ArgumentNullException.ThrowIfNull(import);
            var snapshot = TakeSnapshot();
            try
            {
                _store.InTransaction(() =>
                {
                    if (HasLayoutConfig && !append && _store.DataIsView())
                    {
                        _store.DropDataRelation();
                        _store.ClearLayout();
                    }

                    import();
                    SplitIfLayout();
                    if (!_store.DataTableExists())
                        _store.ClearLayout();
                });
            }
            catch
            {
                RestoreSnapshot(snapshot);
                throw;
            }
        }

        private sealed record SessionSnapshot(
            Config Config,
            Dictionary<string, Type> ExpectedTypes,
            List<Relationship> Relationships,
            TableLayout? Layout,
            Config? LayoutConfig);

        private SessionSnapshot TakeSnapshot()
        {
            return new SessionSnapshot(
                _config,
                new Dictionary<string, Type>(_expectedColumnTypes, StringComparer.OrdinalIgnoreCase),
                new List<Relationship>(_relationships),
                _store.Layout,
                _store.LayoutConfig);
        }

        /// <summary>Restores the in-memory mirrors after the store rolled back.</summary>
        private void RestoreSnapshot(SessionSnapshot snapshot)
        {
            _config = snapshot.Config;
            _expectedColumnTypes.Clear();
            foreach (var pair in snapshot.ExpectedTypes)
                _expectedColumnTypes[pair.Key] = pair.Value;
            _relationships.Clear();
            _relationships.AddRange(snapshot.Relationships);
            try
            {
                _store.RestoreLayout(snapshot.Layout, snapshot.LayoutConfig);
                if (!_store.DataTableExists())
                    _store.ClearLayout();
                _store.ReloadMetadata();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not restore session state after a failed import");
            }
        }

        private void ThrowIfLayoutDeclared(string operation)
        {
            if (HasLayoutConfig)
                throw new DataBallException($"{operation} is not supported on a multi-table session yet");
        }

        private void ThrowIfMultiTable(string operation)
        {
            if (_store.DataIsView())
                throw new DataBallException($"{operation} is not supported on a multi-table session yet");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
