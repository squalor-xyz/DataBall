using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace squalor.DataBall
{
    public sealed partial class DataBall
    {
        private static readonly object HandlerGate = new();
        private static readonly List<IFormatHandler> Handlers = new();

        /// <summary>
        /// Registers a format handler. First match from <see cref="IFormatHandler.CanHandle"/> wins.
        /// Generic CSV / Parquet / archive / <c>.ball</c> need no handler.
        /// </summary>
        public static void RegisterHandler(IFormatHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (HandlerGate)
                Handlers.Add(handler);
        }

        /// <summary>
        /// Clears the process-wide handler registry (tests and host reset).
        /// </summary>
        public static void ClearHandlers()
        {
            lock (HandlerGate)
                Handlers.Clear();
        }

        /// <summary>
        /// Opens <paramref name="path"/> into a new session. Overlay schema JSON is optional.
        /// Registered handlers run before generic import.
        /// </summary>
        /// <param name="path">File to open.</param>
        /// <param name="schemaPath">Optional overlay config JSON (merged onto native defaults).</param>
        /// <exception cref="DataBallException">Missing path/file, unknown format, or import failure.</exception>
        public static DataBall Open(string path, string? schemaPath = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new DataBallException("Path is required");
            if (!File.Exists(path))
                throw new DataBallException($"File not found: {path}");

            var db = new DataBall(schemaPath);
            try
            {
                var handler = FindHandler(path);
                if (handler is not null)
                {
                    var batch = new List<IReadOnlyDictionary<string, object?>>();
                    foreach (var row in handler.Parse(path).ToBlockingEnumerable())
                        batch.Add(row);
                    if (batch.Count > 0)
                        db.AddRows(batch);
                    return db;
                }

                db.ImportAsync(path).GetAwaiter().GetResult();
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
