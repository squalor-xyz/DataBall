// SPDX-License-Identifier: Apache-2.0
namespace squalor.DataBall
{
    /// <summary>
    /// Defines the supported export formats for <see cref="DataBall.ExportAsync(string, ExportOptions)"/>. A native <c>.ball</c> is saved with <see cref="DataBall.SaveAsync"/>.
    /// </summary>
    public enum ExportType
    {
        /// <summary>
        /// Export as CSV format.
        /// </summary>
        Csv,

        /// <summary>
        /// Export as Parquet format (optionally partitioned).
        /// </summary>
        Parquet,

        /// <summary>
        /// Export as archive (ZIP, TAR.GZ, TAR.XZ).
        /// </summary>
        Archive
    }
}
