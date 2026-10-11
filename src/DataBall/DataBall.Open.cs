// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using squalor.DataBall.FileTypes;

namespace squalor.DataBall
{
    public sealed partial class DataBall
    {
        internal const int HandlerBatchSize = 10_000;
        internal static Action<DataBall, int>? AfterHandlerBatchForTests;

        private static readonly object HandlerGate = new();
        private static readonly List<IFormatHandler> Handlers = new();
        private static readonly Dictionary<(string Id, int Version), JsonFileType> LoadedTypes = new();
        private static bool TypesScanned;
        private static string? HomeTypeRoot;
        private static string? LocalTypeRoot;

        /// <summary>Loaded JSON type identities, versions and source paths.</summary>
        public static IReadOnlyList<FileTypeInfo> FileTypes
        {
            get
            {
                lock (HandlerGate)
                {
                    LoadFolders();
                    return Array.AsReadOnly(LoadedTypes.Values.Select(t => t.Info).ToArray());
                }
            }
        }

        /// <summary>Loads and registers a JSON type, overriding the same id and version from earlier origins.</summary>
        public static void RegisterFileType(string path)
        {
            var type = JsonFileType.Load(path);
            lock (HandlerGate)
            {
                LoadFolders();
                AddType(type);
            }
        }

        private static void AddType(JsonFileType type)
        {
            var key = (type.Info.Id, type.Info.Version);
            if (LoadedTypes.TryGetValue(key, out var previous))
                Handlers.Remove(previous);
            LoadedTypes[key] = type;
            Handlers.Add(type);
        }

        private static void LoadFolders()
        {
            if (TypesScanned)
                return;
            var home = HomeTypeRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".databall", "types");
            var local = LocalTypeRoot ?? Path.Combine(Environment.CurrentDirectory, "databall", "types");
            var pending = new List<JsonFileType>();
            try
            {
                foreach (var root in new[] { home, local })
                    if (Directory.Exists(root))
                        foreach (var path in Directory.EnumerateFiles(root, "*.json").OrderBy(p => p, StringComparer.Ordinal))
                            pending.Add(JsonFileType.Load(path));
            }
            catch (Exception ex) when (ex is not DataBallException)
            {
                throw new DataBallException("Failed to load file type folders", ex);
            }
            foreach (var type in pending)
                AddType(type);
            TypesScanned = true;
        }

        internal static void SetFileTypeRootsForTests(string? home, string? local)
        {
            lock (HandlerGate)
            {
                Handlers.RemoveAll(h => h is JsonFileType);
                LoadedTypes.Clear();
                TypesScanned = false;
                HomeTypeRoot = home;
                LocalTypeRoot = local;
            }
        }

        /// <summary>Scores handlers without constructing a session; built-ins are the fallback.</summary>
        public static DetectionReport Detect(string path)
        {
            try { return DetectCore(path); }
            catch (Exception ex) when (ex is not DataBallException)
            {
                throw new DataBallException($"Detection failed for '{path}'", ex);
            }
        }

        private static DetectionReport DetectCore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");
            if (!Directory.Exists(path) && !File.Exists(path))
                throw new DataBallException($"File not found: {path}");
            var probe = new FileProbe(path);
            if (!probe.IsDirectory && probe.Head.Length >= 12 && probe.Head.Span.Slice(8, 4).SequenceEqual("DUCK"u8))
                return new DetectionReport(Array.Empty<HandlerDetection>(), DetectionOutcome.Generic, null,
                    BuiltInPath.NativeBall, "native DuckDB magic", "native DuckDB magic");
            if (probe.Extension.Equals(".ball", StringComparison.OrdinalIgnoreCase) && probe.Head.Length >= 2
                && probe.Head.Span[0] == 'P' && probe.Head.Span[1] == 'K')
                throw new DataBallException("1.x ZIP .ball files are unsupported");

            BuiltInPath? builtIn = null;
            var fallbackReason = $"Unknown extension; no built-in path for '{path}'";
            if (probe.IsDirectory)
            {
                if (HiveFiles.List(path).Count > 0)
                    (builtIn, fallbackReason) = (BuiltInPath.ParquetView, "hive Parquet directory");
                else
                    fallbackReason = "Not a hive Parquet directory";
            }
            else
            {
                try
                {
                    var format = DetectFormat(path);
                    var name = format == Export.ExportType.Csv ? "CSV" : format.ToString();
                    builtIn = format switch
                    {
                        Export.ExportType.Parquet => BuiltInPath.ParquetView,
                        Export.ExportType.Csv => BuiltInPath.CsvImport,
                        Export.ExportType.Ball => BuiltInPath.BallImport,
                        _ => BuiltInPath.ArchiveImport
                    };
                    fallbackReason = $"Known {name} extension";
                }
                catch (DataBallException) { }
            }

            IFormatHandler[] snapshot;
            lock (HandlerGate)
            {
                LoadFolders();
                snapshot = Handlers.ToArray();
            }
            var entries = new List<HandlerDetection>();
            var candidates = new List<(IFormatHandler Handler, double Score, int Priority)>();
            foreach (var handler in snapshot)
            {
                try
                {
                    var score = probe.IsDirectory
                        ? new DetectionScore(0, new[] { "directories are not offered to handlers" })
                        : handler.Score(probe);
                    var min = handler.MinScore;
                    if (!double.IsFinite(min) || min is < 0 or > 1)
                        throw new DataBallException($"Invalid minScore for handler '{handler.Name}'");
                    var priority = handler.Priority;
                    entries.Add(new HandlerDetection(handler.Name, score.Score, min, priority, score.Reasons,
                        (handler as JsonFileType)?.Info.Origin));
                    if (!probe.IsDirectory && score.Score >= min)
                        candidates.Add((handler, score.Score, priority));
                }
                catch (Exception ex) when (ex is not DataBallException || !ex.Message.Contains($"'{handler.Name}'", StringComparison.Ordinal))
                {
                    throw new DataBallException($"Detection failed for handler '{handler.Name}': {ex.Message}", ex);
                }
            }
            if (candidates.Count == 0)
                return new DetectionReport(entries, builtIn is null ? DetectionOutcome.None : DetectionOutcome.Generic,
                    null, builtIn, fallbackReason, fallbackReason);
            var highestScore = candidates.Max(c => c.Score);
            var ordered = candidates.Where(c => Math.Abs(c.Score - highestScore) <= 1e-9)
                .OrderByDescending(c => c.Priority).ToArray();
            if (ordered.Length > 1 && ordered[0].Priority == ordered[1].Priority)
                return new DetectionReport(entries, DetectionOutcome.Ambiguous, null, builtIn, fallbackReason,
                    "Handlers tie after score and priority");
            if (ordered[0].Handler.DelegatesToGenericImport && builtIn is null)
                return new DetectionReport(entries, DetectionOutcome.None, null, builtIn, fallbackReason,
                    $"{ordered[0].Handler.Name} delegates to generic import; no built-in path for '{probe.Extension}'");
            return new DetectionReport(entries, DetectionOutcome.Match, ordered[0].Handler, builtIn, fallbackReason,
                $"{ordered[0].Handler.Name} has the highest score and priority");
        }

        /// <summary>
        /// Registers a format handler on the process-wide, host-owned registry.
        /// Highest score wins; priority breaks equal scores. Remaining ties are ambiguous.
        /// Generic CSV / Parquet / archive / <c>.ball</c> need no handler.
        /// Duplicate handlers registered by hand tie as ambiguous; tests should <see cref="ClearHandlers"/> first.
        /// </summary>
        public static void RegisterHandler(IFormatHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (HandlerGate)
                Handlers.Add(handler);
        }

        internal static void RegisterHandlerIfMissing(IFormatHandler handler)
        {
            lock (HandlerGate)
                if (!Handlers.Any(h => h.GetType() == handler.GetType()))
                    Handlers.Add(handler);
        }

        /// <summary>
        /// Clears the process-wide handler registry. Affects every registrant in this process
        /// (tests and host reset).
        /// </summary>
        public static void ClearHandlers()
        {
            lock (HandlerGate)
            {
                Handlers.Clear();
                LoadedTypes.Clear();
                TypesScanned = false;
            }
        }

        /// <summary>
        /// Opens <paramref name="path"/> into a new session. Overlay schema JSON is optional.
        /// Native DuckDB files open directly, read-only by default, before registered handlers.
        /// A snapshot pointer (<c>&lt;name&gt;.latest</c>, published by <see cref="PublishSnapshotAsync"/>) opens the generation it names, always read-only; see <see cref="OpenedSnapshot"/>.
        /// Equivalent to <see cref="Open(IReadOnlyList{string}, OpenOptions?)"/> with one input: a directory or a glob opens every file it names.
        /// Escapes the ambient synchronization context (Avalonia UI thread).
        /// </summary>
        /// <param name="path">File, hive Parquet directory, directory of files, or glob to open.</param>
        /// <param name="schemaPath">Optional overlay config JSON (merged onto the stored config for native files; tables must match the stored layout).</param>
        /// <param name="engine">Optional machine-local engine settings, never persisted in config.</param>
        /// <param name="writable">Allow changes to a native .ball session. A snapshot pointer cannot be opened writable.</param>
        /// <exception cref="DataBallException">Missing path/file, unknown format, or import failure.</exception>
        public static DataBall Open(string path, string? schemaPath = null, bool writable = false, EngineOptions? engine = null)
        {
            return Task.Run(() => OpenAsync(path, schemaPath, writable, engine)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async counterpart of <see cref="Open(string, string?, bool, EngineOptions?)"/>. Prefer this from UI hosts.
        /// </summary>
        public static async Task<DataBall> OpenAsync(
            string path,
            string? schemaPath = null,
            bool writable = false,
            EngineOptions? engine = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");
            return await OpenAsync(new[] { path }, new OpenOptions { SchemaPath = schemaPath, Writable = writable, Engine = engine }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Opens every file the <paramref name="inputs"/> name into one new session. An input is a file, a hive Parquet
        /// directory, a directory of files, or a glob with <c>*</c> and <c>?</c> in its last segment (matched without regard to case).
        /// A directory whose files are all Parquet is one hive source; any other directory opens its files, those of its subdirectories
        /// too with <see cref="OpenOptions.Recursive"/>. The files are sorted by full path (ordinal), duplicates removed, and each is detected as
        /// by <see cref="Detect"/>: a file from a directory or glob that is no known format is listed in <see cref="OpenReport.Ignored"/>, a file
        /// named by the caller that is no known format fails the open. Every file becomes a source (see <see cref="Sources"/>); the rows of all
        /// of them are staged first, then merged and split once, with column types merged as for an append.
        /// </summary>
        /// <remarks>
        /// A metadata field (<c>metadataFields</c>) is kept at the lowest level its values need: constant in every source and present in all
        /// of them, it goes to <see cref="Metadata"/>; constant within each source that has it but not the same everywhere, it becomes a column
        /// of the <c>source</c> table (NULL for a source without it); varying within any source, it stays a column of <c>"data"</c> for all rows.
        /// More than one source copies Parquet in instead of viewing it. A native <c>.ball</c> or a snapshot pointer opens only as the sole
        /// input.
        /// </remarks>
        /// <param name="inputs">Paths, directories and globs to open.</param>
        /// <param name="options">Optional schema overlay, recursion and engine settings.</param>
        /// <exception cref="DataBallException">No input, a missing file, a wildcard outside the last segment, nothing to open, an unrecognized named file, columns that cannot merge, or an import failure.</exception>
        public static DataBall Open(IReadOnlyList<string> inputs, OpenOptions? options = null)
        {
            return Task.Run(() => OpenAsync(inputs, options)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async counterpart of <see cref="Open(IReadOnlyList{string}, OpenOptions?)"/>. Prefer this from UI hosts.
        /// </summary>
        public static async Task<DataBall> OpenAsync(
            IReadOnlyList<string> inputs,
            OpenOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new OpenOptions();
            var plan = OpenInputs.Plan(inputs, options.Recursive);
            // One source opens as a single path does. An archive is always staged: its CSV entries are sources of their own.
            if (plan.Entries.Count == 1 && !IsArchive(plan.Entries[0]))
            {
                var db = await OpenOneAsync(plan.Entries[0], plan.Bytes, options, cancellationToken).ConfigureAwait(false);
                if (db.OpenReport is null && plan.Entries[0].Kind == OpenKind.Detected)
                    db.OpenReport = db.ReportSources(plan.Ignored);
                return db;
            }
            return await OpenManyAsync(plan, options, cancellationToken).ConfigureAwait(false);
        }

        private static bool IsArchive(OpenEntry entry) =>
            entry.Kind == OpenKind.Detected
            && entry.Report!.Winner is null or { DelegatesToGenericImport: true }
            && entry.Report.BuiltInPath == BuiltInPath.ArchiveImport;

        /// <summary>What <c>Open</c> did with its inputs; null for a native <c>.ball</c> or a snapshot, whose sources were recorded when it was written.</summary>
        public OpenReport? OpenReport { get; private set; }

        /// <summary>The sources the session holds, with the rows each contributed.</summary>
        private OpenReport ReportSources(IReadOnlyList<string> ignored, IReadOnlyDictionary<long, long>? rowsByKey = null)
        {
            var sources = _store.ReadSources();
            if (rowsByKey is null)
            {
                var counts = new Dictionary<long, long>();
                if (sources.Count == 1)
                {
                    // Every row came from the one source.
                    counts[sources[0].Key] = _store.RowCount();
                }
                else if (sources.Count > 1 && _store.DataTableExists() && _store.GetColumns().Any(c => c.Name.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var row in _store.Query($"SELECT {DuckDbStore.QuoteIdent(Lineage.KeyColumn)} AS k, count(*) AS n FROM \"data\" GROUP BY 1"))
                    {
                        if (row["k"] is not null)
                            counts[Convert.ToInt64(row["k"], CultureInfo.InvariantCulture)] = Convert.ToInt64(row["n"], CultureInfo.InvariantCulture);
                    }
                }
                rowsByKey = counts;
            }
            return new OpenReport(
                sources.Select(s => new OpenedSource(s, rowsByKey.TryGetValue(s.Key, out var n) ? n : 0)).ToList(),
                ignored.ToList());
        }

        /// <summary>Opens a sole entry exactly as a single-path open always did.</summary>
        private static async Task<DataBall> OpenOneAsync(OpenEntry entry, long inputBytes, OpenOptions options, CancellationToken cancellationToken)
        {
            var path = entry.OpenPath;
            var schemaPath = options.SchemaPath;
            var engine = options.Engine;
            if (entry.Kind == OpenKind.Pointer)
                return OpenSnapshot(path, entry.PointerTarget!, schemaPath, options.Writable, engine);

            if (entry.Kind == OpenKind.Native)
                return new DataBall(string.IsNullOrEmpty(schemaPath) ? null : Config.LoadConfig(schemaPath), null, path, readOnly: !options.Writable, native: true, engine);

            var (effective, store, routes) = ResolveOpenConfig(new[] { entry }, inputBytes, options);
            var handler = routes[0].Handler;
            var delegates = routes[0].Delegates;
            var report = entry.Report!;
            var db = new DataBall(effective, engine, temporary: store == StoreMode.File);
            try
            {
                var sourceType = routes[0].SourceType;
                if (delegates && report.BuiltInPath == BuiltInPath.ParquetView && !db.HasLayoutConfig)
                {
                    var source = SourceFile.Describe(path, sourceType, entry.HiveFiles);
                    db.RunImport(false, () =>
                    {
                        db._store.AttachParquetView(path, db._store.AddSource(source), entry.HiveFiles);
                        db.ExtractConfiguredMetadata("data", false);
                    });
                    return db;
                }

                if (handler is not null && !delegates)
                {
                    // The handler's rows are the file's rows: each carries the key of its source, which the handler cannot choose.
                    var key = db._store.AddSource(SourceFile.Describe(path, sourceType));
                    var batch = new List<IReadOnlyDictionary<string, object?>>(HandlerBatchSize);
                    IReadOnlyDictionary<string, object?>? last = null;
                    await foreach (var row in handler.Parse(path, db.Schema, cancellationToken).ConfigureAwait(false))
                    {
                        last = row;
                        batch.Add(KeyHandlerRow(row, key));
                        if (batch.Count == HandlerBatchSize)
                        {
                            db._store.AddRows(batch, db._expectedColumnTypes);
                            db.RememberRow(last);
                            AfterHandlerBatchForTests?.Invoke(db, batch.Count);
                            batch.Clear();
                        }
                    }
                    if (batch.Count > 0)
                    {
                        db._store.AddRows(batch, db._expectedColumnTypes);
                        db.RememberRow(last!);
                        AfterHandlerBatchForTests?.Invoke(db, batch.Count);
                    }
                    db.SplitIfLayout();
                    return db;
                }

                await db.ImportCoreAsync(path, null, sourceType).ConfigureAwait(false);
                return db;
            }
            catch (Exception ex)
            {
                db.Dispose();
                if (ex is DataBallException or OperationCanceledException)
                    throw;
                throw new DataBallException("Open failed", ex);
            }
        }

        /// <summary>How an entry is read: its handler, whether that handler delegates, the type its source records, and the route when several sources are staged.</summary>
        private sealed record EntryRoute(OpenEntry Entry, IFormatHandler? Handler, bool Delegates, string SourceType, SourceRoute? Route);

        /// <summary>
        /// The effective config (defaults, each handler's overlay in source order, then <c>schemaPath</c>), the store sized over the
        /// input bytes, and how each detected entry is read. Shared by a single-source open and a many-source open.
        /// </summary>
        private static (Config Config, StoreMode Store, List<EntryRoute> Routes) ResolveOpenConfig(
            IReadOnlyList<OpenEntry> entries, long inputBytes, OpenOptions options)
        {
            try
            {
                var effective = Config.CreateDefaults();
                var routes = new List<EntryRoute>(entries.Count);
                foreach (var entry in entries)
                {
                    var report = entry.Report!;
                    var handler = report.Winner;
                    var delegates = handler is null || handler.DelegatesToGenericImport;
                    if (delegates && report.BuiltInPath is null)
                        throw new DataBallException($"No built-in import path for this match{Environment.NewLine}{report}");
                    if (handler?.Overlay is { } overlay)
                        effective = Config.Merge(effective, overlay);
                    // What the source table records as the type: the handler's name (a JSON type's id) or the built-in path.
                    var type = handler?.Name ?? report.BuiltInPath?.ToString() ?? nameof(BuiltInPath.CsvImport);
                    SourceRoute? route = !delegates ? SourceRoute.Handler : report.BuiltInPath switch
                    {
                        BuiltInPath.CsvImport => SourceRoute.Csv,
                        BuiltInPath.ParquetView => entry.Hive ? SourceRoute.Hive : SourceRoute.Parquet,
                        BuiltInPath.ArchiveImport => SourceRoute.Archive,
                        _ => null,
                    };
                    routes.Add(new EntryRoute(entry, handler, delegates, type, route));
                }
                if (!string.IsNullOrEmpty(options.SchemaPath))
                    effective = Config.Merge(effective, Config.LoadConfig(options.SchemaPath));
                TableLayout.Validate(effective);
                return (effective, (options.Engine ?? new EngineOptions()).ResolveStore(inputBytes), routes);
            }
            catch (Exception ex) when (ex is not DataBallException and not OperationCanceledException)
            {
                throw new DataBallException("Open configuration failed", ex);
            }
        }

        /// <summary>
        /// A handler row as the session stores it: columns renamed as in a file (<c>_source_file</c> becomes <c>origin_source_file</c>)
        /// and carrying <paramref name="key"/>, the key of its source, which the handler cannot choose.
        /// </summary>
        private static Dictionary<string, object?> KeyHandlerRow(IReadOnlyDictionary<string, object?> row, long key)
        {
            var keyed = new Dictionary<string, object?>(row.Count + 1);
            foreach (var pair in row)
            {
                var name = Lineage.IncomingName(pair.Key);
                if (!keyed.TryAdd(name, pair.Value))
                    throw new DataBallException($"Column '{name}' already exists; it is where '{pair.Key}' would go, because source lineage reserves that name");
            }
            keyed[Lineage.KeyColumn] = key;
            return keyed;
        }

        /// <summary>True for a native DuckDB file; throws for a 1.x ZIP <c>.ball</c>.</summary>
        internal static bool IsNativeBall(string path)
        {
            var native = false;
            using (var sniff = File.OpenRead(path))
            {
                var magic = new byte[12];
                var count = sniff.Read(magic, 0, magic.Length);
                if (count >= 12 && magic.AsSpan(8, 4).SequenceEqual("DUCK"u8))
                    native = true;
                if (Path.GetExtension(path).Equals(".ball", StringComparison.OrdinalIgnoreCase)
                    && count >= 2 && magic[0] == 'P' && magic[1] == 'K')
                    throw new DataBallException("1.x ZIP .ball files are unsupported");
            }
            return native;
        }

        /// <summary>
        /// Opens the generation a snapshot pointer names, read-only. If that generation has just been deleted by
        /// retention, the pointer is read once more and the new target is tried; then the open fails clearly.
        /// </summary>
        private static DataBall OpenSnapshot(string pointerPath, string generationFile, string? schemaPath, bool writable, EngineOptions? engine)
        {
            if (writable)
                throw new DataBallException($"A snapshot is read-only; {pointerPath} cannot be opened with writable: true");
            var pointerFull = Path.GetFullPath(pointerPath);
            var directory = Path.GetDirectoryName(pointerFull)!;
            for (var attempt = 0; ; attempt++)
            {
                var generation = SnapshotFiles.ParseGeneration(pointerPath, generationFile);
                var generationPath = Path.Combine(directory, generationFile);
                if (File.Exists(generationPath))
                {
                    try
                    {
                        if (!IsNativeBall(generationPath))
                            throw new DataBallException($"Snapshot generation {generationPath} is not a native .ball");
                        var bytes = new FileInfo(generationPath).Length;
                        var db = new DataBall(string.IsNullOrEmpty(schemaPath) ? null : Config.LoadConfig(schemaPath), null, generationPath, readOnly: true, native: true, engine);
                        db.OpenedSnapshot = new SnapshotInfo(generation, generationPath, pointerFull, bytes, TimeSpan.Zero);
                        return db;
                    }
                    catch (Exception ex) when (ex is DataBallException or IOException && !File.Exists(generationPath))
                    {
                        // Deleted between the check and the open: handled like a missing generation.
                    }
                }
                if (attempt == 1)
                    throw new DataBallException($"Snapshot pointer {pointerPath} names generation {generationFile}, which does not exist");
                SnapshotFiles.GenerationMissingForTests?.Invoke(pointerPath);
                try
                {
                    generationFile = SnapshotFiles.TryReadPointer(pointerPath)
                        ?? throw new DataBallException($"{pointerPath} is no longer a snapshot pointer");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new DataBallException($"Snapshot pointer {pointerPath} cannot be read", ex);
                }
            }
        }

    }
}
