// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
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
                if (Directory.EnumerateFiles(path, "*.parquet", SearchOption.AllDirectories).Any())
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
        /// Escapes the ambient synchronization context (Avalonia UI thread).
        /// </summary>
        /// <param name="path">File or hive Parquet directory to open.</param>
        /// <param name="schemaPath">Optional overlay config JSON (merged onto the stored config for native files; tables must match the stored layout).</param>
        /// <param name="engine">Optional machine-local engine settings, never persisted in config.</param>
        /// <param name="writable">Allow changes to a native .ball session.</param>
        /// <exception cref="DataBallException">Missing path/file, unknown format, or import failure.</exception>
        public static DataBall Open(string path, string? schemaPath = null, bool writable = false, EngineOptions? engine = null)
        {
            return Task.Run(() => OpenAsync(path, schemaPath, writable, engine)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async counterpart of <see cref="Open"/>. Prefer this from UI hosts.
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
            var directory = Directory.Exists(path);
            if (directory && !Directory.EnumerateFiles(path, "*.parquet", SearchOption.AllDirectories).Any())
                throw new DataBallException($"{path} is not a hive Parquet directory");
            if (!directory && !File.Exists(path))
                throw new DataBallException($"File not found: {path}");

            var native = false;
            if (!directory)
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

            if (native)
                return new DataBall(string.IsNullOrEmpty(schemaPath) ? null : Config.LoadConfig(schemaPath), null, path, readOnly: !writable, native: true, engine);

            var report = Detect(path);
            if (report.Outcome is DetectionOutcome.Ambiguous or DetectionOutcome.None)
                throw new DataBallException(report.ToString());
            var handler = report.Winner;
            Config effective;
            bool delegates;
            StoreMode store;
            try
            {
                delegates = handler is null || handler.DelegatesToGenericImport;
                if (delegates && report.BuiltInPath is null)
                    throw new DataBallException($"No built-in import path for this match{Environment.NewLine}{report}");
                effective = Config.CreateDefaults();
                if (handler?.Overlay is { } overlay)
                    effective = Config.Merge(effective, overlay);
                if (!string.IsNullOrEmpty(schemaPath))
                    effective = Config.Merge(effective, Config.LoadConfig(schemaPath));
                TableLayout.Validate(effective);
                store = (engine ?? new EngineOptions()).ResolveStore(new[] { path });
            }
            catch (Exception ex) when (ex is not DataBallException and not OperationCanceledException)
            {
                throw new DataBallException("Open configuration failed", ex);
            }
            var db = new DataBall(effective, engine, temporary: store == StoreMode.File);
            try
            {
                // What the source table records as the type: the handler's name (a JSON type's id) or the built-in path.
                var sourceType = handler?.Name ?? report.BuiltInPath?.ToString() ?? nameof(BuiltInPath.CsvImport);
                if (delegates && report.BuiltInPath == BuiltInPath.ParquetView && !db.HasLayoutConfig)
                {
                    var source = SourceFile.Describe(path, sourceType);
                    db.RunImport(false, () =>
                    {
                        db._store.AttachParquetView(path, db._store.AddSource(source));
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
                        // Columns are renamed as in a file (_source_file becomes origin_source_file); _source_key is the session's.
                        var keyed = new Dictionary<string, object?>(row.Count + 1);
                        foreach (var pair in row)
                        {
                            var name = Lineage.IncomingName(pair.Key);
                            if (!keyed.TryAdd(name, pair.Value))
                                throw new DataBallException($"Column '{name}' already exists; it is where '{pair.Key}' would go, because source lineage reserves that name");
                        }
                        keyed[Lineage.KeyColumn] = key;
                        batch.Add(keyed);
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

    }
}
