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
        /// Registered handlers run before generic import.
        /// Escapes the ambient synchronization context (Avalonia UI thread).
        /// </summary>
        /// <param name="path">File to open.</param>
        /// <param name="schemaPath">Optional overlay config JSON (merged onto native defaults).</param>
        /// <exception cref="DataBallException">Missing path/file, unknown format, or import failure.</exception>
        public static DataBall Open(string path, string? schemaPath = null)
        {
            return Task.Run(() => OpenAsync(path, schemaPath)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async counterpart of <see cref="Open"/>. Prefer this from UI hosts.
        /// </summary>
        public static async Task<DataBall> OpenAsync(
            string path,
            string? schemaPath = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");
            if (!File.Exists(path))
                throw new DataBallException($"File not found: {path}");

            var db = new DataBall(schemaPath);
            try
            {
                var handler = FindHandler(path);
                if (handler is not null && !handler.DelegatesToGenericImport)
                {
                    var batch = new List<IReadOnlyDictionary<string, object?>>();
                    await foreach (var row in handler.Parse(path, db.Schema, cancellationToken).ConfigureAwait(false))
                        batch.Add(row);
                    if (batch.Count > 0)
                        db.AddRows(batch);
                    return db;
                }

                await db.ImportAsync(path).ConfigureAwait(false);
                return db;
            }
            catch
            {
                db.Dispose();
                throw;
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
