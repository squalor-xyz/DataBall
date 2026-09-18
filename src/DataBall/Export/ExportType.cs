// SPDX-License-Identifier: Apache-2.0
namespace squalor.DataBall.Export
{
    /// <summary>
    /// Defines the supported export formats for DataBall.
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
        Archive,

        /// <summary>
        /// Export as custom .ball format (ZIP with Parquet and metadata).
        /// </summary>
        Ball
    }
}
