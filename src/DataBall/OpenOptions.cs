// SPDX-License-Identifier: Apache-2.0
namespace squalor.DataBall
{
    /// <summary>What a many-file open does with a file it cannot read or stage.</summary>
    public enum BadFilePolicy
    {
        /// <summary>The open throws, naming the file. No session is left.</summary>
        Abort = 0,

        /// <summary>The file is left out and listed in <see cref="OpenReport.Skipped"/>; the other files land.</summary>
        Skip = 1,
    }

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

        /// <summary>What happens to a file that cannot be read or staged. <see cref="BadFilePolicy.Abort"/> by default.</summary>
        public BadFilePolicy OnBadFile { get; init; } = BadFilePolicy.Abort;
    }
}
