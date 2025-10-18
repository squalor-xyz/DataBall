using System;
using System.IO;
using Microsoft.Data.Analysis;
using Xunit;
using Parquet;
using Parquet.Data;
using squalor.DataBall.Export;
using squalor.DataBall.Import;

namespace squalor.DataBall.Tests;

/// <summary>
/// Unit tests for the <see cref="DataBall"/> class.
/// </summary>
public class DataBallTests
{
    /// <summary>
    /// Tests that loading a configuration sets metadata and column types correctly.
    /// </summary>
    [Fact]
    public void LoadConfig_SetsMetadataAndTypes()
    {
        // TODO: Implement config load test
    }

    /// <summary>
    /// Tests that the row builder applies relationships correctly during commit.
    /// </summary>
    [Fact]
    public void RowBuilder_AppliesRelationships()
    {
        // TODO: Implement row builder test
    }

    /// <summary>
    /// Tests that the Bounce operation extracts constant columns to metadata.
    /// </summary>
    [Fact]
    public void Bounce_ExtractsConstants()
    {
        // TODO: Implement Bounce test
    }

    /// <summary>
    /// Tests round-trip import and export of Parquet data.
    /// </summary>
    [Fact]
    public void ImportExport_ParquetRoundTrip()
    {
        var db = new DataBall();
        db.AddColumn("Id", new int[] { 1, 2 });
        db.AddColumn("Name", new string[] { "A", "B" });

        var tempPath = Path.GetTempFileName() + ".parquet";
        try
        {
            ExportManager.Roll(db, ExportType.Parquet, tempPath);
            var db2 = new DataBall();
            ImportManager.ImportFromParquet(db2, tempPath);

            Assert.Equal(2, db2.Data.Rows.Count);
            Assert.Equal(1, db2.Data["Id"][0]);
            Assert.Equal("A", db2.Data["Name"][0]);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Tests SQLite import and export round-trip.
    /// </summary>
    [Fact]
    public void ImportExport_SqliteRoundTrip()
    {
        var db = new DataBall();
        db.AddColumn("Id", new int[] { 1, 2 });
        db.AddColumn("Name", new string[] { "A", "B" });

        var tempPath = Path.GetTempFileName() + ".sqlite";
        try
        {
            ExportManager.Roll(db, ExportType.Sqlite, tempPath);
            var db2 = new DataBall();
            ImportManager.ImportFromSqlite(db2, tempPath);

            Assert.Equal(2, db2.Data.Rows.Count);
            Assert.Equal("1", db2.Data["Id"][0]); // SQLite stores as TEXT
            Assert.Equal("A", db2.Data["Name"][0]);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}