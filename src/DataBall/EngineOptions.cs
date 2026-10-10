// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
namespace squalor.DataBall
{
    /// <summary>Storage mode for a new session or a non-native input.</summary>
    public enum StoreMode
    {
        /// <summary>Choose by input bytes; an empty session starts in memory.</summary>
        Auto,
        /// <summary>Use an in-memory store.</summary>
        Memory,
        /// <summary>Use a temporary file store, deleted on dispose.</summary>
        File
    }

    /// <summary>Machine-local engine settings. These are never persisted in a .ball config.</summary>
    public sealed class EngineOptions
    {
        /// <summary>Storage mode. Ignored when an explicit databasePath is given; native .ball files open directly.</summary>
        public StoreMode Store { get; set; } = StoreMode.Auto;
        /// <summary>Maximum input bytes for memory; null uses 25% of GC available memory.</summary>
        public long? InMemoryMaxBytes { get; set; }
        /// <summary>DuckDB memory limit, such as "8GB"; null uses its default.</summary>
        public string? MemoryLimit { get; set; }
        /// <summary>DuckDB worker threads; null uses its default.</summary>
        public int? Threads { get; set; }
        /// <summary>DuckDB spill and temporary store directory; null uses the engine default for spill and OS temp for the store.</summary>
        public string? TempDirectory { get; set; }

        internal void Validate()
        {
            if (InMemoryMaxBytes < 0)
                throw new DataBallException("InMemoryMaxBytes must be non-negative");
            if (Threads <= 0)
                throw new DataBallException("Threads must be positive");
        }

        internal StoreMode ResolveStore(IEnumerable<string> inputPaths)
        {
            Validate();
            if (Store != StoreMode.Auto)
                return Store;
            long inputBytes = 0;
            foreach (var path in inputPaths)
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        inputBytes = checked(inputBytes + new FileInfo(file).Length);
                }
                else
                {
                    if (!File.Exists(path))
                        throw new DataBallException($"File not found: {path}");
                    inputBytes = checked(inputBytes + new FileInfo(path).Length);
                }
            }
            return inputBytes > (InMemoryMaxBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4)
                ? StoreMode.File : StoreMode.Memory;
        }
    }
}
