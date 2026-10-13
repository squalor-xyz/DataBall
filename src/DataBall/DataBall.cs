// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
        // Expected types DataBall learned itself (InitializeRow/CommitRow capture, the CSV schema pass). They widen on append
        // and are not persisted as declared; the rest of _expectedColumnTypes is declared (config or AddColumn<T>).
        private readonly HashSet<string> _learnedColumnNames;
        private readonly List<Relationship> _relationships;
        private Config _config;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataBall"/> class, optionally loading configuration.
        /// Pass <paramref name="databasePath"/> by name for a new file-backed session
        /// (<c>new DataBall(databasePath: "session.duckdb")</c>). The first string is always config JSON.
        /// The constructor creates new sessions only: a <paramref name="databasePath"/> that already exists is refused;
        /// open it with <c>Open(path, new OpenOptions { Writable = true })</c>.
        /// </summary>
        /// <param name="configPath">The path to the configuration JSON file, if any.</param>
        /// <param name="logger">Optional logger. Defaults to a no-op logger.</param>
        /// <param name="engine">Optional machine-local engine settings, never persisted in config.</param>
        /// <param name="databasePath">Optional new DuckDB file. Null or empty is <c>:memory:</c>. Filename <c>catalog.duckdb</c> is rejected.</param>
        /// <exception cref="DataBallException"><paramref name="databasePath"/> already exists, or the configuration cannot be loaded.</exception>
        public DataBall(string? configPath = null, ILogger? logger = null, string? databasePath = null, EngineOptions? engine = null)
            : this(string.IsNullOrEmpty(configPath) ? null : Config.LoadConfig(configPath), logger, ThrowIfExisting(databasePath), readOnly: false, native: false, engine,
                temporary: string.IsNullOrEmpty(databasePath) && engine?.ResolveStore(Array.Empty<string>()) == StoreMode.File)
        {
        }

        internal DataBall(Config config, EngineOptions? engine, bool temporary)
            : this(config, null, null, readOnly: false, native: false, engine, temporary)
        {
        }

        private DataBall(Config? overlay, ILogger? logger, string? databasePath, bool readOnly, bool native, EngineOptions? engine = null, bool temporary = false)
        {
            _logger = logger ?? NullLogger.Instance;
            _store = new DuckDbStore(ValidateDatabasePath(databasePath), _logger, readOnly, engine, temporary)
            {
                MetadataPolicySource = CurrentMetadataPolicy,
            };
            _expectedColumnTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            _learnedColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _relationships = new List<Relationship>();
            try
            {
                var stored = native || _store.HasStoredConfig;
                _config = stored ? _store.ReadConfig() : Config.CreateDefaults();
                if (stored)
                {
                    ApplyConfig(_config, applyMetadata: false);
                    InitializeLayout();
                    if (overlay is not null)
                    {
                        var merged = Config.Merge(_config, overlay);
                        if (overlay.Tables.Count > 0 && _store.Layout is null && _store.DataTableExists())
                            throw new DataBallException("Overlay declares a different 'tables' layout");
                        TableLayout.Validate(merged);
                        _store.UpdateLayoutConfig(merged);
                        _config = merged;
                        ApplyConfig(_config, applyMetadata: false);
                        foreach (var (key, value) in overlay.Metadata)
                            _store.SetMetadata(key, DuckDbStore.Unwrap(value));
                    }
                }
                else
                {
                    if (overlay is not null)
                        _config = Config.Merge(_config, overlay);
                    ApplyConfig(_config);
                    InitializeLayout();
                }
                if (!readOnly)
                    PersistConfig();
            }
            catch
            {
                _store.Dispose();
                throw;
            }
        }

        internal string? StorePath => _store.StorePath;

        /// <summary>The constructor creates new sessions only; an existing file is opened with <see cref="Open(string, OpenOptions?)"/>.</summary>
        private static string? ThrowIfExisting(string? databasePath)
        {
            if (!string.IsNullOrEmpty(databasePath)
                && !databasePath.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
                && File.Exists(databasePath))
            {
                throw new DataBallException(
                    $"'{databasePath}' already exists; the constructor creates new sessions only. "
                    + "Open it with Open(path, new OpenOptions { Writable = true })");
            }
            return databasePath;
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

        /// <summary>
        /// The files whose rows the session holds, in import order: one entry per row of the built-in
        /// <c>source</c> table. Each row of <c>"data"</c> points at its entry with <c>_source_key</c>; rows
        /// written by <c>AddRow</c> and the row builder have a NULL key. Empty for a session that never
        /// imported a file. Each call reads a fresh snapshot.
        /// </summary>
        public IReadOnlyList<SourceInfo> Sources
        {
            get
            {
                ThrowIfDisposed();
                return _store.ReadSources();
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
            ThrowIfReadOnly();
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
            ThrowIfReadOnly();
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(values);
            Lineage.ThrowIfReservedColumn(name);
            EnsureExpectedType(name, typeof(T));
            var list = values as IReadOnlyList<T> ?? values.ToList();
            OnWideData(() => _store.AddColumn(name, list));
            _expectedColumnTypes[name] = typeof(T);
            _learnedColumnNames.Remove(name);
            RememberAddedColumn(name, list.Count == 0 ? null : list[list.Count - 1]);
        }

        /// <summary>
        /// Adds a column of string values.
        /// </summary>
        /// <param name="name">The name of the column.</param>
        /// <param name="values">The string values for the column.</param>
        public void AddColumn(string name, IEnumerable<string?> values)
        {
            ThrowIfReadOnly();
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(values);
            Lineage.ThrowIfReservedColumn(name);
            EnsureExpectedType(name, typeof(string));
            var list = values as IReadOnlyList<string?> ?? values.ToList();
            OnWideData(() => _store.AddColumn(name, list));
            _expectedColumnTypes[name] = typeof(string);
            _learnedColumnNames.Remove(name);
            RememberAddedColumn(name, list.Count == 0 ? null : list[list.Count - 1]);
        }

        /// <summary>
        /// Removes a column.
        /// </summary>
        /// <param name="name">The name of the column to remove.</param>
        public void RemoveColumn(string name)
        {
            ThrowIfReadOnly();
            ThrowIfDisposed();
            Lineage.ThrowIfReservedColumn(name);
            ThrowIfDeclaredKey(name);
            OnWideData(() => _store.RemoveColumn(name));
            RemoveExpectedType(name);
        }

        /// <summary>
        /// Inserts a row of values, creating the table or adding missing columns as needed.
        /// </summary>
        /// <param name="values">The column values for the new row.</param>
        public void AddRow(IReadOnlyDictionary<string, object?> values)
        {
            ThrowIfReadOnly();
            ThrowIfDisposed();
            ThrowIfReservedColumns(values);
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
            ThrowIfReadOnly();
            ThrowIfDisposed();
            if (rows is null)
                throw new DataBallException("Row values are required");
            var list = rows as IReadOnlyList<IReadOnlyDictionary<string, object?>> ?? rows.ToList();
            if (list.Count == 0)
                return;
            foreach (var row in list)
                ThrowIfReservedColumns(row);
            _store.AddRows(list, _expectedColumnTypes);
            RememberRow(list[list.Count - 1]);
            SplitIfLayout();
        }

        /// <summary>
        /// Executes SQL against the session store and returns rows as dictionaries.
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
        /// Imports the rows of another session, handling schema differences. The other session is only read; its sources follow its rows.
        /// </summary>
        /// <param name="other">The session to import.</param>
        /// <param name="options">Optional import settings. <see cref="ImportOptions.Append"/> defaults to <c>false</c> (replace existing data).</param>
        /// <returns>A completed task after the import finishes.</returns>
        /// <exception cref="DataBallException">Thrown when this session is read-only or the import fails.</exception>
        public Task ImportAsync(DataBall other, ImportOptions? options = null)
        {
            ThrowIfReadOnly();
            ThrowIfDisposed();
            ThrowIfPendingRow();
            ArgumentNullException.ThrowIfNull(other);
            ObjectDisposedException.ThrowIf(other._disposed, other);
            var append = options?.Append ?? false;

            _logger.LogDebug("Importing another DataBall, append={Append}", append);
            try
            {
                if (!other.Store.DataTableExists())
                {
                    if (!append || !_store.DataTableExists() || _store.RowCount() == 0)
                        RunImport(append: false, () => _store.DropDataRelation());
                    _logger.LogDebug("Import of DataBall completed");
                    return Task.CompletedTask;
                }

                var dir = Path.Combine(Path.GetTempPath(), "databall-merge-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var path = Path.Combine(dir, "data.parquet");
                    // Not the resolving export: the key travels, and the other session's sources follow it below.
                    other.Store.ExportParquetKeepingSourceKey(path);
                    var otherSources = other.Store.ReadSources();
                    RunImport(append, () => _store.ImportParquet(path, append, t => _store.RemapStagingSources(t, otherSources), configTypes: DeclaredMergeTypes(), incomingName: "the merged session"));
                }
                finally
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                _logger.LogDebug("Import of DataBall completed");
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex is not DataBallException and not ObjectDisposedException)
            {
                _logger.LogError(ex, "Import of DataBall failed");
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
            ThrowIfReadOnly();
            ThrowIfDisposed();
            ThrowIfPendingRow();
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

                if (HasLayoutConfig)
                {
                    // A declared key column stays data even when constant: the re-split needs it.
                    var keep = (partitionColumns ?? Array.Empty<string>())
                        .Concat(_config.Tables.Values.SelectMany(t => t.Key))
                        .ToArray();
                    OnWideData(() =>
                    {
                        ExtractConstantsToMetadataSql(keep);
                        DistinctInPlace();
                    });
                }
                else
                {
                    var snapshot = TakeSnapshot();
                    try
                    {
                        _store.InTransaction(() =>
                        {
                            _store.MaterializeView();
                            ExtractConstantsToMetadataSql(partitionColumns);
                            DistinctInPlace();
                        });
                    }
                    catch
                    {
                        RestoreSnapshot(snapshot);
                        throw;
                    }
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
        /// Equivalent to <see cref="ImportAsync(IReadOnlyList{string}, ImportOptions?, CancellationToken)"/> with one input.
        /// </summary>
        /// <param name="path">The path of the file to import.</param>
        /// <param name="options">Optional import settings. <see cref="ImportOptions.Append"/> defaults to <c>false</c>.</param>
        /// <param name="cancellationToken">Cancels the import; the session is left as it was.</param>
        /// <returns>A completed task after the import finishes.</returns>
        /// <exception cref="DataBallException">Thrown when the path is missing, the format is unknown, or import fails.</exception>
        public Task ImportAsync(string path, ImportOptions? options = null, CancellationToken cancellationToken = default) =>
            ImportCoreAsync(new[] { path }, options, cancellationToken);

        /// <summary>
        /// Imports every file of <paramref name="inputs"/> into the session in one transaction, each detected by extension as for a single
        /// path. With <see cref="ImportOptions.Append"/> the rows are added to the session's; without it the first input replaces them
        /// and the others are added to it. If any input is missing, of an unknown format or fails to import, none of them lands.
        /// An input is one file or one hive Parquet directory; directories of files and globs are expanded by <c>Open</c> only.
        /// </summary>
        /// <param name="inputs">The files to import, in the order they are added.</param>
        /// <param name="options">Optional import settings. <see cref="ImportOptions.Append"/> defaults to <c>false</c>.</param>
        /// <param name="cancellationToken">Cancels the import; the session is left as it was.</param>
        /// <returns>A completed task after the import finishes.</returns>
        /// <exception cref="DataBallException">Thrown when there is no input, an input is missing or of an unknown format, or the import fails.</exception>
        public Task ImportAsync(IReadOnlyList<string> inputs, ImportOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (inputs is null || inputs.Count == 0)
                throw new DataBallException("No input to import");
            return ImportCoreAsync(inputs, options, cancellationToken);
        }

        /// <summary>The body of both path forms of <see cref="ImportAsync(IReadOnlyList{string}, ImportOptions?, CancellationToken)"/>.</summary>
        internal Task ImportCoreAsync(IReadOnlyList<string> paths, ImportOptions? options, CancellationToken cancellationToken)
        {
            ThrowIfReadOnly();
            ThrowIfDisposed();
            ThrowIfPendingRow();
            cancellationToken.ThrowIfCancellationRequested();

            // Every input is checked before the first write.
            var inputs = new List<(string Path, PathFormat Format)>(paths.Count);
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new DataBallException("Path is required");
                var format = DetectFormat(path);
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw new DataBallException($"File not found: {path}");
                inputs.Add((path, format));
            }

            options ??= new ImportOptions();
            var append = options.Append;
            try
            {
                // Hashing can fail on an unreadable file; it runs before the first write.
                var sources = inputs.Select(input => input.Format switch
                {
                    PathFormat.Csv => SourceFile.Describe(input.Path, nameof(BuiltInPath.CsvImport)),
                    PathFormat.Parquet => SourceFile.Describe(input.Path, nameof(BuiltInPath.ParquetView)),
                    _ => null
                }).ToList();

                void Import()
                {
                    for (var i = 0; i < inputs.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (path, format) = inputs[i];
                        // Only the first input may replace; the others are added to it.
                        var appendThis = i == 0 ? append : true;
                        _logger.LogInformation("Importing {Path} as {Format}, append={Append}", path, format, appendThis);
                        switch (format)
                        {
                            case PathFormat.Csv:
                                ImportCsvWithSchema(path, appendThis, sources[i]);
                                break;
                            case PathFormat.Parquet:
                                ImportParquetWithMetadata(path, appendThis, sources[i]);
                                break;
                            case PathFormat.Archive:
                                ImportManager.ImportFromArchiveCore(this, path, appendThis);
                                break;
                            case PathFormat.Ball:
                                ImportManager.ImportFromBallCore(this, path, appendThis);
                                break;
                            default:
                                throw new DataBallException($"Unknown import format for '{path}'");
                        }
                    }
                }

                RunImport(append, Import);
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex is not DataBallException and not ObjectDisposedException and not OperationCanceledException)
            {
                _logger.LogError(ex, "Import failed");
                throw new DataBallException("Failed to import", ex);
            }
        }

        /// <summary>
        /// Exports the session's rows as one file in the format <see cref="ExportOptions.Format"/> names. A native <c>.ball</c> is written
        /// with <see cref="SaveAsync"/>.
        /// </summary>
        /// <param name="path">The destination path.</param>
        /// <param name="options">The export settings; <see cref="ExportOptions.Format"/> is required, <see cref="ExportOptions.PartitionBy"/> writes hive-partitioned Parquet.</param>
        /// <returns>A completed task after the export finishes.</returns>
        /// <exception cref="DataBallException">Thrown when export fails.</exception>
        public Task ExportAsync(string path, ExportOptions options)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(options);
            ThrowIfPendingRow();
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");

            var type = options.Format;
            var partitionBy = options.PartitionBy;
            if (partitionBy is not null && type != ExportType.Parquet)
                throw new DataBallException($"ExportOptions.PartitionBy is valid only with ExportType.Parquet, not {type}");
            // A projection without a partition column would fail inside DuckDB after the target folder was cleared; refuse it first.
            if (partitionBy is { Count: > 0 } && _currentFilter?.Columns is { } projection)
            {
                foreach (var column in partitionBy)
                {
                    if (!projection.Any(p => p.Equals(column, StringComparison.OrdinalIgnoreCase)))
                        throw new DataBallException($"Partition column '{column}' is not in the filtered columns");
                }
            }
            _logger.LogInformation("Exporting to {Path} as {Type}", path, type);
            try
            {
                switch (type)
                {
                    case ExportType.Csv:
                        _store.ExportCsv(path, FilteredSelectOrNull());
                        break;
                    case ExportType.Parquet when partitionBy is null:
                        _store.ExportParquet(path, FilteredSelectOrNull());
                        break;
                    case ExportType.Parquet when partitionBy.Count == 0:
                        _store.ExportParquet(Path.Combine(path, "data.parquet"), FilteredSelectOrNull());
                        break;
                    case ExportType.Parquet:
                        _store.ExportPartitionedParquet(path, partitionBy, FilteredSelectOrNull());
                        break;
                    case ExportType.Archive:
                        ExportManager.ExportToArchive(this, path);
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
        /// Saves the whole session to the specified path as a native DuckDB .ball file.
        /// </summary>
        /// <param name="path">The path to save the .ball file.</param>
        /// <param name="options">External Parquet is embedded by default; set Link to preserve its view.</param>
        /// <returns>A completed task after the save finishes.</returns>
        /// <exception cref="DataBallException">Thrown when the save operation fails.</exception>
        public Task SaveAsync(string path, SaveOptions? options = null) => SaveCore(path, options?.Link ?? false);

        /// <summary>
        /// Publishes a closed snapshot generation for readers in other processes: a complete
        /// <c>&lt;Name&gt;.&lt;n&gt;.ball</c> in <see cref="SnapshotOptions.Directory"/>, then the pointer
        /// <c>&lt;Name&gt;.latest</c> is replaced atomically to name it, then older generations beyond
        /// <see cref="SnapshotOptions.Keep"/> are pruned when they can be deleted. Readers call
        /// <see cref="Open(string, OpenOptions?)"/> on the pointer.
        /// </summary>
        /// <remarks>
        /// The snapshot holds committed data only: a pending row is not in it. Capture writes pause while the data is
        /// copied. A read-only session may publish. A view-backed session links the Parquet unless
        /// <see cref="SnapshotOptions.EmbedViews"/> is set.
        /// </remarks>
        /// <param name="options">Where to publish and how many generations to keep.</param>
        /// <returns>A completed task with the generation that was published.</returns>
        /// <exception cref="DataBallException">Thrown for invalid options, an open row stream, or when publishing fails.</exception>
        public Task<SnapshotInfo> PublishSnapshotAsync(SnapshotOptions options)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(options);
            SnapshotFiles.Validate(options);
            _logger.LogInformation("Publishing snapshot {Name} to {Directory}", options.Name, options.Directory);
            try
            {
                var clock = Stopwatch.StartNew();
                var directory = Path.GetFullPath(options.Directory);
                var generation = SnapshotFiles.NextGeneration(directory, options.Name);
                var fileName = SnapshotFiles.GenerationFile(options.Name, generation);
                var target = Path.Combine(directory, fileName);
                var pointer = SnapshotFiles.PointerPath(directory, options.Name);
                if (File.Exists(pointer) && SnapshotFiles.TryReadPointer(pointer) is null)
                    throw new DataBallException($"{pointer} exists and is not a snapshot pointer");
                _store.PublishTo(target, target + "." + Guid.NewGuid().ToString("N") + ".tmp", ConfigForPersistence(), link: !options.EmbedViews);
                SnapshotFiles.WritePointer(pointer, fileName);
                clock.Stop();
                SnapshotFiles.Prune(directory, options.Name, options.Keep, _logger);
                return Task.FromResult(new SnapshotInfo(generation, target, pointer, new FileInfo(target).Length, clock.Elapsed));
            }
            catch (Exception ex) when (ex is not DataBallException and not ObjectDisposedException)
            {
                _logger.LogError(ex, "Snapshot publish failed");
                throw new DataBallException("Failed to publish snapshot", ex);
            }
        }

        /// <summary>
        /// The generation a session opened through a snapshot pointer, or null for any other session.
        /// <see cref="SnapshotInfo.Duration"/> is zero for it.
        /// </summary>
        public SnapshotInfo? OpenedSnapshot { get; private set; }

        /// <summary>
        /// Reads the generation number a snapshot pointer names, without opening the generation. Cheap enough to poll:
        /// compare it with <see cref="SnapshotInfo.Generation"/> of <see cref="OpenedSnapshot"/> and reopen only when it is newer.
        /// </summary>
        /// <param name="pointerPath">The <c>&lt;Name&gt;.latest</c> file.</param>
        /// <returns>The generation, or null when the file does not exist.</returns>
        /// <exception cref="DataBallException">The file is not a snapshot pointer, is malformed, or cannot be read.</exception>
        public static int? ReadSnapshotGeneration(string pointerPath)
        {
            if (string.IsNullOrWhiteSpace(pointerPath))
                throw new DataBallException("Path is required");
            try
            {
                string? generationFile;
                try
                {
                    generationFile = SnapshotFiles.TryReadPointer(pointerPath);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    return null;
                }
                if (generationFile is null)
                    throw new DataBallException($"{pointerPath} is not a snapshot pointer");
                return SnapshotFiles.ParseGeneration(pointerPath, generationFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DataBallException($"Snapshot pointer {pointerPath} cannot be read", ex);
            }
        }

        private const string BallFormatName = "Ball";

        private Task SaveCore(string path, bool link)
        {
            ThrowIfDisposed();
            ThrowIfPendingRow();
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");
            _logger.LogInformation("Exporting to {Path} as {Type}", path, BallFormatName);
            try
            {
                _store.ThrowIfConnectionBusy();
                if (!_store.IsReadOnly)
                    PersistConfig();
                _store.SaveTo(path, ConfigForPersistence(), link);
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex is not DataBallException and not ObjectDisposedException)
            {
                _logger.LogError(ex, "Export failed");
                throw new DataBallException($"Failed to export to {BallFormatName}", ex);
            }
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
            try
            {
                _store.Dispose();
            }
            finally
            {
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        private void ExtractConstantsToMetadataSql(string[]? partitionColumns)
        {
            if (!_store.DataTableExists() || _store.RowCount() == 0)
                return;

            // Hive PARTITION_BY needs these columns left in the table.
            HashSet<string>? skip = null;
            if (partitionColumns is { Length: > 0 })
                skip = new HashSet<string>(partitionColumns, StringComparer.OrdinalIgnoreCase);

            // A per-source column of the source table stays a column of "data" too, even when every row holds one value now.
            var perSource = _store.SourceMetadataColumns().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, _) in _store.GetColumns().ToList())
            {
                // Last remaining constant column drops "data"; stop rather than query a missing table.
                if (!_store.DataTableExists())
                    break;
                if (skip is not null && skip.Contains(name))
                    continue;
                // Constant in a single-file session, but it is lineage: it stays a column.
                if (name.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (perSource.Contains(name))
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
                    RemoveExpectedType(name);
                }
            }
        }

        private void DistinctInPlace()
        {
            // Extracting the last constant column drops "data"; DuckDB cannot DISTINCT a 0-column table.
            if (!_store.DataTableExists())
                return;
            var columns = _store.GetColumns().Select(c => c.Name).ToList();
            var key = columns.FirstOrDefault(c => c.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase));
            if (key is null)
            {
                _store.Execute("CREATE OR REPLACE TABLE \"data\" AS SELECT DISTINCT * FROM \"data\"");
                return;
            }
            // Rows equal in every data column are one row, whichever file they came from: the lowest key survives.
            var select = string.Join(", ", columns.Select(c => c == key
                ? $"min({DuckDbStore.QuoteIdent(c)}) AS {DuckDbStore.QuoteIdent(c)}"
                : DuckDbStore.QuoteIdent(c)));
            var groupBy = string.Join(", ", columns.Where(c => c != key).Select(DuckDbStore.QuoteIdent));
            _store.Execute($"CREATE OR REPLACE TABLE \"data\" AS SELECT {select} FROM \"data\" GROUP BY {groupBy}");
        }

        /// <summary>Host writes may not name a <c>_source*</c> column; checked before anything is written.</summary>
        private static void ThrowIfReservedColumns(IReadOnlyDictionary<string, object?>? values)
        {
            if (values is null)
                return;
            foreach (var key in values.Keys)
                Lineage.ThrowIfReservedColumn(key);
        }

        private void EnsureExpectedType(string name, Type actual)
        {
            if (_expectedColumnTypes.TryGetValue(name, out var expected) && expected != actual)
                throw new DataBallException($"Column '{name}' expected type {expected.Name}, got {actual.Name}");
        }

        private string? CurrentMetadataPolicy() => _config.MetadataPolicy;

        internal void ApplyImportedConfig(Config config)
        {
            ArgumentNullException.ThrowIfNull(config);
            var subset = new Config
            {
                Columns = config.Columns,
                Relationships = config.Relationships,
                Tables = config.Tables,
                Metadata = config.Metadata,
            };
            _config = Config.Merge(_config, subset);
            ApplyConfig(_config);
            _store.UpdateLayoutConfig(_config);
        }

        /// <summary>
        /// Re-applies the constant metadata of an imported config so it wins over
        /// stored metadata, as it did when config was applied last.
        /// </summary>
        internal void ApplyImportedConfigMetadata(Config config)
        {
            ArgumentNullException.ThrowIfNull(config);
            foreach (var (key, value) in config.Metadata)
                _store.SetMetadata(key, DuckDbStore.Unwrap(value));
        }

        /// <summary>
        /// Detects the import format of a path. Directories are Parquet (hive).
        /// Compound suffixes (<c>.tar.gz</c>, <c>.tar.xz</c>) are matched before short ones.
        /// </summary>
        internal static PathFormat DetectFormat(string path)
        {
            if (Directory.Exists(path))
                return PathFormat.Parquet;

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName))
                throw new DataBallException($"Unknown import format for '{path}'");

            if (EndsWithSuffix(fileName, ".tar.gz") || EndsWithSuffix(fileName, ".tgz")
                || EndsWithSuffix(fileName, ".tar.xz") || EndsWithSuffix(fileName, ".txz")
                || EndsWithSuffix(fileName, ".tar") || EndsWithSuffix(fileName, ".zip"))
                return PathFormat.Archive;
            if (EndsWithSuffix(fileName, ".ball"))
                return PathFormat.Ball;
            if (EndsWithSuffix(fileName, ".csv"))
                return PathFormat.Csv;
            if (EndsWithSuffix(fileName, ".parquet"))
                return PathFormat.Parquet;
            throw new DataBallException($"Unknown import format for '{path}'");
        }

        private static bool EndsWithSuffix(string fileName, string suffix)
            => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>Copies Parquet, extracting configured metadata at the CSV path's staging/merge boundary.</summary>
        internal void ImportParquetWithMetadata(string path, bool append, SourceRecord? source = null)
        {
            var record = source ?? SourceFile.Describe(path, nameof(BuiltInPath.ParquetView));
            long RecordSource() => _store.AddSource(record);
            if (HasLayoutConfig)
            {
                _store.ImportParquet(path, append, t => ExtractConfiguredMetadata(t, append && _store.Layout is not null), RecordSource, configTypes: DeclaredMergeTypes());
                return;
            }
            var reconciled = append && _store.DataTableExists() && _store.RowCount() > 0
                ? new HashSet<string>(_store.SnapshotMetadata().Keys, StringComparer.OrdinalIgnoreCase)
                : null;
            _store.ImportParquet(path, append, recordSource: RecordSource, configTypes: DeclaredMergeTypes());
            ExtractConfiguredMetadata("data", false, reconciled);
        }

        /// <summary>
        /// Imports a CSV and applies the CSV schema. Wide and layout sessions alike apply it to the staging
        /// table before the rows are merged (or routed, because a layout's <c>"data"</c> is a view), so an
        /// append sees canonical column names, typed values and extracted metadata.
        /// </summary>
        internal void ImportCsvWithSchema(string path, bool append, SourceRecord? source = null)
        {
            var record = source ?? SourceFile.Describe(path, nameof(BuiltInPath.CsvImport));
            long RecordSource() => _store.AddSource(record);
            // A merge into a live session: learned types then never narrow the incoming values (see ApplyCsvSchema).
            var merging = append && (_store.Layout is not null || (_store.DataTableExists() && _store.RowCount() > 0));
            // Staging rows join an existing session: stored metadata is left for the append reconciliation.
            var joinsSession = HasLayoutConfig ? append && _store.Layout is not null : merging;
            _store.ImportCsv(path, append, _expectedColumnTypes, t => ApplyCsvSchema(t, append: joinsSession, merging: merging), RecordSource, DeclaredMergeTypes(), _learnedColumnNames);
        }

        /// <summary>
        /// Renames raw headers to canonical names, coerces configured types, and extracts
        /// configured metadata fields on <paramref name="table"/>. With <paramref name="append"/>
        /// (staging rows joining a live session) fields already in metadata are left in place for
        /// the append reconciliation, and no column is dropped here.
        /// </summary>
        internal void ApplyCsvSchema(string table, bool append = false, bool merging = false)
        {
            if (!_store.TableExists(table))
                return;
            if (CanonicalizeCsvStaging(table, merging))
                ExtractConfiguredMetadata(table, append);
        }

        /// <summary>
        /// Renames the raw headers of <paramref name="table"/> to canonical names and coerces configured types, learning the
        /// types the headers imply. False when the table has no header to resolve (nothing to extract either).
        /// </summary>
        private bool CanonicalizeCsvStaging(string table, bool merging)
        {
            var headers = _store.GetColumnsOf(table)
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (headers.Count == 0)
                return false;

            var parsed = SchemaResolver.ResolveAll(headers, _config);
            var tableCols = _store.GetColumnsOf(table);
            foreach (var col in parsed)
            {
                if (col.ClrType is not null)
                {
                    var declared = IsDeclaredName(col.Name);
                    _expectedColumnTypes[col.Name] = col.ClrType;
                    if (declared)
                        _learnedColumnNames.Remove(col.Name);
                    else
                        _learnedColumnNames.Add(col.Name);
                }
                var match = tableCols.FirstOrDefault(c => c.Name.Equals(col.Raw, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    match = tableCols.FirstOrDefault(c => c.Name.Equals(col.Name, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    continue;
                if (!match.Name.Equals(col.Name, StringComparison.Ordinal))
                    _store.RenameColumnOf(table, match.Name, col.Name);
            }

            _store.CoerceColumnsOf(table, _expectedColumnTypes, merging ? _learnedColumnNames : null);
            return true;
        }

        /// <summary>Expected types the session declares (config or AddColumn&lt;T&gt;); learned types are left out.</summary>
        private Dictionary<string, Type> DeclaredExpectedTypes()
        {
            var declared = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _expectedColumnTypes)
            {
                if (!_learnedColumnNames.Contains(pair.Key))
                    declared[pair.Key] = pair.Value;
            }
            return declared;
        }

        /// <summary>
        /// Types that keep their declared type on append (cast, never widened): the declared expected types plus the
        /// config Parameters types. Learned types are not in it.
        /// </summary>
        internal Dictionary<string, Type> DeclaredMergeTypes()
        {
            var declared = DeclaredExpectedTypes();
            if (_config.Parameters is not null)
            {
                foreach (var pair in _config.Parameters)
                {
                    if (!declared.ContainsKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value?.Type))
                        declared[pair.Key] = Config.ParseColumnType(pair.Value.Type);
                }
            }
            return declared;
        }

        /// <summary>True when a column name carries a declared type: an expected type that is not learned, or a typed config parameter.</summary>
        private bool IsDeclaredName(string name)
        {
            if (_expectedColumnTypes.ContainsKey(name) && !_learnedColumnNames.Contains(name))
                return true;
            return _config.Parameters is not null
                && _config.Parameters.TryGetValue(name, out var spec)
                && !string.IsNullOrWhiteSpace(spec?.Type);
        }

        /// <summary>Removes a column's expected type together with its learned mark.</summary>
        private void RemoveExpectedType(string name)
        {
            _expectedColumnTypes.Remove(name);
            _learnedColumnNames.Remove(name);
        }

        private Config ConfigForPersistence()
        {
            var persisted = _config.Clone();
            foreach (var pair in Config.ToColumnTypeNames(DeclaredExpectedTypes()))
                persisted.Columns[pair.Key] = pair.Value;
            return persisted;
        }

        private void PersistConfig() => _store.WriteConfig(ConfigForPersistence());

        private void ApplyConfig(Config config, bool applyMetadata = true)
        {
            if (applyMetadata)
            {
                foreach (var (key, value) in config.Metadata)
                    _store.SetMetadata(key, DuckDbStore.Unwrap(value));
            }
            foreach (var col in config.Columns)
            {
                _expectedColumnTypes[col.Key] = Config.ParseColumnType(col.Value);
                _learnedColumnNames.Remove(col.Key);
            }
            if (config.Relationships.Count > 0)
            {
                _relationships.Clear();
                _relationships.AddRange(config.Relationships);
            }
        }

        /// <summary>
        /// What a configured metadata field's name says before any data is looked at: lineage (<see cref="FieldHome.Reserved"/>), a declared table
        /// key (<see cref="FieldHome.LayoutKey"/>), a built-in column of the <c>source</c> table (<see cref="FieldHome.BuiltInSourceName"/>), or
        /// nothing special (<see cref="FieldHome.Fresh"/>).
        /// </summary>
        private FieldHome ClassifyField(string field)
        {
            if (field.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase))
                return FieldHome.Reserved;
            if (IsLayoutKeyColumn(field))
                return FieldHome.LayoutKey;
            return DuckDbStore.IsBuiltInSourceColumn(field) ? FieldHome.BuiltInSourceName : FieldHome.Fresh;
        }

        /// <summary>True when a declared <c>tables</c> entry names <paramref name="column"/> in its <c>key</c>.</summary>
        private bool IsLayoutKeyColumn(string column) =>
            _config.Tables.Values.Any(t => t.Key.Any(k => k.Equals(column, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// Gives each configured metadata field of <paramref name="table"/> the level <see cref="MetadataPlanner"/> decides. With
        /// <paramref name="append"/> (staging rows joining a live session) the column stays in the table: the append reconciliation compares it
        /// with the stored value afterwards.
        /// </summary>
        private void ExtractConfiguredMetadata(string table, bool append, IReadOnlySet<string>? reconciled = null)
        {
            if (!_store.TableExists(table) || _config.MetadataFields.Count == 0)
                return;

            var policy = MetadataPlanner.ParsePolicy(_config.MetadataPolicy);
            var existing = _store.SnapshotMetadata();
            long? rows = null; // read once, and only for bounce
            // A field a many-file open made a column of the source table stays out of Metadata for good: an append makes it a row column.
            var perSource = _store.SourceMetadataColumns().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var field in _config.MetadataFields.ToList())
            {
                if (reconciled?.Contains(field) == true)
                    continue; // already compared with stored metadata by the append path
                var cols = _store.GetColumnsOf(table);
                var match = cols.FirstOrDefault(c => c.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    continue;

                // What the session already holds under the name refines a name that is not special in itself.
                var home = ClassifyField(field);
                if (home is FieldHome.Fresh or FieldHome.BuiltInSourceName)
                {
                    if (perSource.Contains(match.Name))
                        home = FieldHome.PerSource;
                    else if (append && existing.ContainsKey(match.Name))
                        home = FieldHome.Stored;
                    else if (append && _store.GetColumns().Any(c => c.Name.Equals(match.Name, StringComparison.OrdinalIgnoreCase)))
                        home = FieldHome.SessionColumn; // the session already holds the field as a column: it stays data, never also metadata
                }

                var column = match.Name;
                var decision = MetadataPlanner.PlanStaged(policy, home, column, () =>
                {
                    var profile = _store.ProfileColumns(table, new[] { column })[column];
                    return new FieldFacts(profile, policy == MetadataPolicyKind.Bounce ? rows ??= _store.StagedRowCount(table) : 0);
                });
                if (decision.Level != MetadataLevel.Metadata)
                    continue;

                // Under first a column without a value sets nothing, but still leaves the rows.
                var value = decision.FirstNonNull ? _store.FirstNonNull(table, column) : decision.Value;
                if (!decision.FirstNonNull || value is not null)
                    _store.SetMetadata(column, value);
                if (!append)
                {
                    if (table == "data" && _store.IsViewBacked)
                        _store.ExcludeFromView(column);
                    else
                        _store.RemoveColumnOf(table, column);
                    RemoveExpectedType(column);
                }
            }
        }

        /// <summary>
        /// Constructor step: bind an external Parquet view or a configured layout. Other views
        /// need <c>tables</c>; a wide file opened with <c>tables</c> is split; a bad layout fails now.
        /// </summary>
        private void InitializeLayout()
        {
            if (_store.DataIsView())
            {
                if (!HasLayoutConfig)
                {
                    if (!_store.BindParquetView())
                        throw new DataBallException("Session file has a multi-table layout; open it with the config that declares 'tables'");
                    try
                    {
                        // Binding the view validates linked Parquet paths before returning a session.
                        _store.Query("SELECT * FROM \"data\" LIMIT 0");
                    }
                    catch (DataBallException ex)
                    {
                        throw new DataBallException($"Failed to open linked Parquet: {ex.GetBaseException().Message}", ex);
                    }
                    return;
                }
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
        /// remains). Stored <c>.ball</c> config may change the config mid-import, so the
        /// in-memory mirrors (config, expected types, relationships, layout, metadata) are
        /// snapshotted first and restored when the transaction rolls back.
        /// </summary>
        internal void RunImport(bool append, Action import)
        {
            ThrowIfReadOnly();
            ArgumentNullException.ThrowIfNull(import);
            var snapshot = TakeSnapshot();
            try
            {
                _store.InTransaction(() =>
                {
                    if (!append && (_store.IsViewBacked || (HasLayoutConfig && _store.DataIsView())))
                    {
                        _store.DropDataRelation();
                        _store.ClearLayout();
                    }
                    else
                        _store.MaterializeView();

                    // A replacing import starts the sources over; an append keeps their keys.
                    if (!append)
                        _store.ResetSources();

                    import();
                    SplitIfLayout();
                    if (!_store.DataTableExists())
                        _store.ClearLayout();
                    PersistConfig();
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
            Config? LayoutConfig,
            HashSet<string> LearnedNames);

        private SessionSnapshot TakeSnapshot()
        {
            return new SessionSnapshot(
                _config,
                new Dictionary<string, Type>(_expectedColumnTypes, StringComparer.OrdinalIgnoreCase),
                new List<Relationship>(_relationships),
                _store.Layout,
                _store.LayoutConfig,
                new HashSet<string>(_learnedColumnNames, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>Restores the in-memory mirrors after the store rolled back.</summary>
        private void RestoreSnapshot(SessionSnapshot snapshot)
        {
            _config = snapshot.Config;
            _expectedColumnTypes.Clear();
            foreach (var pair in snapshot.ExpectedTypes)
                _expectedColumnTypes[pair.Key] = pair.Value;
            _learnedColumnNames.Clear();
            _learnedColumnNames.UnionWith(snapshot.LearnedNames);
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
                _logger.LogError(ex, "Could not restore session state after a failed write");
            }
        }

        /// <summary>
        /// A column named in a table's declared <c>key</c> cannot be removed while the config
        /// declares it: the re-split would have no key. Checked before anything is written.
        /// </summary>
        private void ThrowIfDeclaredKey(string column)
        {
            foreach (var (table, spec) in _config.Tables)
            {
                if (spec.Key.Any(k => k.Equals(column, StringComparison.OrdinalIgnoreCase)))
                    throw new DataBallException($"Column '{column}' is part of the declared key of table '{table}'; change the 'tables' config before removing it");
            }
        }

        /// <summary>
        /// Runs a wide-table operation inside one transaction. On a layout session the
        /// layout is materialized back into one wide <c>"data"</c> table (row-key order), the
        /// operation runs unchanged, and the result is re-split (or nothing remains when it
        /// dropped <c>"data"</c>). The row key <c>_row</c> is regenerated. On failure the store
        /// rolls back and the in-memory mirrors are restored, as for an import.
        /// </summary>
        private void OnWideData(Action wideOperation)
        {
            if (!HasLayoutConfig)
            {
                // One transaction whether or not "data" is a view: a failure leaves no half-created table behind.
                _store.InTransaction(() =>
                {
                    _store.MaterializeView();
                    wideOperation();
                });
                return;
            }

            var snapshot = TakeSnapshot();
            try
            {
                _store.InTransaction(() =>
                {
                    if (_store.Layout is not null)
                        _store.UnsplitLayout();
                    wideOperation();
                    SplitIfLayout();
                });
            }
            catch
            {
                RestoreSnapshot(snapshot);
                throw;
            }
        }

        private void ThrowIfReadOnly()
        {
            _store.ThrowIfConnectionBusy();
            if (_store.IsReadOnly)
                throw new DataBallException("Session is read-only; open with writable: true to change it");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
