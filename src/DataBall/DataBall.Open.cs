// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace squalor.DataBall
{
    public sealed partial class DataBall
    {
        internal const int HandlerBatchSize = 10_000;
        internal static Action<DataBall, int>? AfterHandlerBatchForTests;

        private static readonly object HandlerGate = new();
        private static readonly List<IFormatHandler> Handlers = new();

        /// <summary>
        /// Registers a format handler on the process-wide, host-owned registry.
        /// First match from <see cref="IFormatHandler.CanHandle"/> wins.
        /// Generic CSV / Parquet / archive / <c>.ball</c> need no handler.
        /// Duplicates are the caller's problem; tests should <see cref="ClearHandlers"/> first.
        /// </summary>
        public static void RegisterHandler(IFormatHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (HandlerGate)
                Handlers.Add(handler);
        }

        /// <summary>
        /// Clears the process-wide handler registry. Affects every registrant in this process
        /// (tests and host reset).
        /// </summary>
        public static void ClearHandlers()
        {
            lock (HandlerGate)
                Handlers.Clear();
        }

        /// <summary>
        /// Opens <paramref name="path"/> into a new session. Overlay schema JSON is optional.
        /// Native DuckDB files open directly, read-only by default, before registered handlers.
        /// Escapes the ambient synchronization context (Avalonia UI thread).
        /// </summary>
        /// <param name="path">File to open.</param>
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
            if (!File.Exists(path))
                throw new DataBallException($"File not found: {path}");

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

            if (native)
                return new DataBall(schemaPath, null, path, readOnly: !writable, native: true, engine);

            var db = new DataBall(schemaPath, null, null, readOnly: false, native: false, engine,
                temporary: (engine ?? new EngineOptions()).ResolveStore(new[] { path }) == StoreMode.File);
            try
            {
                var handler = FindHandler(path);
                if (handler is not null && !handler.DelegatesToGenericImport)
                {
                    var batch = new List<IReadOnlyDictionary<string, object?>>(HandlerBatchSize);
                    await foreach (var row in handler.Parse(path, db.Schema, cancellationToken).ConfigureAwait(false))
                    {
                        batch.Add(row);
                        if (batch.Count == HandlerBatchSize)
                        {
                            db._store.AddRows(batch, db._expectedColumnTypes);
                            db.RememberRow(batch[^1]);
                            AfterHandlerBatchForTests?.Invoke(db, batch.Count);
                            batch.Clear();
                        }
                    }
                    if (batch.Count > 0)
                    {
                        db._store.AddRows(batch, db._expectedColumnTypes);
                        db.RememberRow(batch[^1]);
                        AfterHandlerBatchForTests?.Invoke(db, batch.Count);
                    }
                    db.SplitIfLayout();
                    return db;
                }

                await db.ImportAsync(path).ConfigureAwait(false);
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

        private static IFormatHandler? FindHandler(string path)
        {
            IFormatHandler[] snapshot;
            lock (HandlerGate)
                snapshot = Handlers.ToArray();
            if (snapshot.Length == 0)
                return null;

            using var sniff = File.OpenRead(path);
            foreach (var handler in snapshot)
            {
                sniff.Position = 0;
                if (handler.CanHandle(path, sniff))
                    return handler;
            }

            return null;
        }
    }
}
