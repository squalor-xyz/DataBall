using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Analysis;
using Xunit;
using NLog;
using squalor.DataBall.Import;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// Contains unit tests for the <see cref="DataBall"/> class.
    /// </summary>
    public class DataBallTests
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Tests adding columns to a DataBall instance.
        /// </summary>
        [Fact]
        public void TestAddColumn()
        {
            var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice", "Bob" });
            db.AddColumn<int>("Age", new[] { 30, 25 });
            Assert.Equal(2, db.DataFrame.Rows.Count);
            Assert.Equal("Alice", db.DataFrame["Name"][0]);
            Assert.Equal(30, db.DataFrame["Age"][0]);
        }

        /// <summary>
        /// Tests adding a single row with string and integer columns.
        /// </summary>
        [Fact]
        public void TestAnother()
        {
            var db = new DataBall();
            db.AddColumn("Name", new[] { "Charlie" });
            db.AddColumn<int>("Age", new[] { 40 });
            Assert.Equal(1, db.DataFrame.Rows.Count);
            Assert.Equal("Charlie", db.DataFrame["Name"][0]);
            Assert.Equal(40, db.DataFrame["Age"][0]);
        }

        /// <summary>
        /// Benchmarks the performance of import, Bounce, Squish, and export operations.
        /// </summary>
        [Fact]
        public async Task BenchmarkImportBounceSquishExport()
        {
            var db = new DataBall();
            var start = DateTime.Now;
            await ImportManager.ImportFromParquet(db, "large.parquet", true);
            var importTime = (DateTime.Now - start).TotalSeconds;

            start = DateTime.Now;
            await db.Bounce();
            var bounceTime = (DateTime.Now - start).TotalSeconds;

            start = DateTime.Now;
            await db.Squish("partitioned", new[] { "Category" });
            var squishTime = (DateTime.Now - start).TotalSeconds;

            start = DateTime.Now;
            db.Save("large.ball");
            var saveTime = (DateTime.Now - start).TotalSeconds;

            File.WriteAllText("docs/Performance.md", $"Import: {importTime}s\nBounce: {bounceTime}s\nSquish: {squishTime}s\nSave: {saveTime}s");
        }
    }
}