namespace squalor.DataBall.Export;

/// <summary>
/// Defines the supported export formats for DataBall.
/// </summary>
public enum ExportType
{
    /// <summary>
    /// Export data as a CSV file.
    /// </summary>
    Csv,

    /// <summary>
    /// Export data as a Parquet file.
    /// </summary>
    Parquet,

    /// <summary>
    /// Export data to a SQLite database.
    /// </summary>
    Sqlite,

    /// <summary>
    /// Export data as an archive (ZIP, TAR.GZ, or TAR.XZ).
    /// </summary>
    Archive,

    /// <summary>
    /// Export data in the custom .ball format (ZIP with Parquet and metadata).
    /// </summary>
    DataBall
}