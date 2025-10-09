using System.ComponentModel;

namespace squalor.DataBall;

/// <summary>
/// Enum defining the supported export formats for the <see cref="DataBall.Roll(ExportType, string?, string[])"/> method.
/// </summary>
public enum ExportType
{
    /// <summary>
    /// Export to CSV format, optionally chunked for large files.
    /// </summary>
    Csv,

    /// <summary>
    /// Export to Parquet format, with optional partitioning by columns.
    /// </summary>
    Parquet,

    /// <summary>
    /// Export to SQLite database format.
    /// </summary>
    Sqlite,

    /// <summary>
    /// Export to Archive format (ZIP, TAR.GZ, or TAR.XZ containing CSVs).
    /// </summary>
    Archive,

    /// <summary>
    /// Export to native DataBall format (.ball ZIP with partitioned Parquet and JSON metadata).
    /// </summary>
    DataBall
}