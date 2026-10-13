// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;

namespace squalor.DataBall
{
    /// <summary>Options for <see cref="DataBall.ExportAsync(string, ExportOptions)"/>.</summary>
    public sealed class ExportOptions
    {
        /// <summary>What file the export writes: one CSV, one Parquet file or an archive. A native <c>.ball</c> is written with <see cref="DataBall.SaveAsync"/>.</summary>
        public required ExportType Format { get; init; }

        /// <summary>
        /// Columns to write hive-partitioned Parquet by: the path is then a directory holding one <c>column=value</c> folder per value.
        /// Valid only with <see cref="ExportType.Parquet"/>; any other format throws <see cref="DataBallException"/>. An empty list writes
        /// <c>data.parquet</c> inside the directory. Null (the default) writes one file. Like every export, a partitioned one honours
        /// <see cref="DataBall.ApplyFilter"/>: it writes only the rows (and columns) the current filter selects. A filter whose column list
        /// leaves out a partition column throws <see cref="DataBallException"/> before anything is written.
        /// </summary>
        public IReadOnlyList<string>? PartitionBy { get; init; }
    }
}
