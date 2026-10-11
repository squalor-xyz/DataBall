// SPDX-License-Identifier: Apache-2.0
namespace squalor.DataBall
{
    /// <summary>Options for <see cref="DataBall.Open(System.Collections.Generic.IReadOnlyList{string}, OpenOptions?)"/>.</summary>
    public sealed class OpenOptions
    {
        /// <summary>Optional overlay config JSON, merged onto the defaults (and a handler's overlay) for non-native inputs, or onto the stored config of a native file.</summary>
        public string? SchemaPath { get; init; }

        /// <summary>Allow changes to a native <c>.ball</c> session. A snapshot pointer cannot be opened writable.</summary>
        public bool Writable { get; init; }

        /// <summary>
        /// Include subdirectories when a directory (or a glob) is expanded to files. Without it only the files directly
        /// inside the directory are opened. A directory named as an input whose files are all Parquet stays one hive source either way.
        /// </summary>
        public bool Recursive { get; init; }

        /// <summary>Optional machine-local engine settings, never persisted in config.</summary>
        public EngineOptions? Engine { get; init; }
    }
}
