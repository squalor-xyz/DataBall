// SPDX-License-Identifier: Apache-2.0
namespace squalor.DataBall
{
    /// <summary>The kind of file a path names, by its extension: how an import routes it. Only exports have a public format (<see cref="ExportType"/>).</summary>
    internal enum PathFormat
    {
        /// <summary>A CSV file.</summary>
        Csv,

        /// <summary>A Parquet file, or a directory (hive Parquet).</summary>
        Parquet,

        /// <summary>A ZIP, TAR, TAR.GZ or TAR.XZ archive.</summary>
        Archive,

        /// <summary>A native DuckDB <c>.ball</c> file.</summary>
        Ball,
    }
}
