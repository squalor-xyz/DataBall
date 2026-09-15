// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class BounceSquishTests
    {
        [Fact]
        public async Task Bounce_ExtractsConstantToMetadata_ColumnGone()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new[] { "A", "A" });
            db.AddColumn<int>("Meas", new[] { 1, 2 });
            await db.Bounce();
            Assert.Equal("A", db.Metadata["Site"]);
            var rows = db.Query("SELECT * FROM \"data\" ORDER BY \"Meas\"");
            Assert.Equal(2, rows.Count);
            Assert.False(rows[0].ContainsKey("Site"));
            Assert.False(rows[1].ContainsKey("Site"));
            Assert.Equal(1, Convert.ToInt32(rows[0]["Meas"]));
            Assert.Equal(2, Convert.ToInt32(rows[1]["Meas"]));
        }

        [Fact]
        public async Task Bounce_RemovesDuplicateRows()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new[] { "A", "A", "B" });
            db.AddColumn<int>("Meas", new[] { 1, 1, 2 });
            await db.Bounce();
            var rows = db.Query("SELECT \"Site\", \"Meas\" FROM \"data\" ORDER BY \"Site\", \"Meas\"");
            Assert.Equal(2, rows.Count);
            Assert.Equal("A", rows[0]["Site"]);
            Assert.Equal(1, Convert.ToInt32(rows[0]["Meas"]));
            Assert.Equal("B", rows[1]["Site"]);
            Assert.Equal(2, Convert.ToInt32(rows[1]["Meas"]));
        }

        [Fact]
        public async Task Bounce_DoesNotExtractMixedNulls()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new string?[] { "A", "A", null });
            db.AddColumn<int>("Meas", new[] { 1, 2, 3 });
            await db.Bounce();
            Assert.False(db.Metadata.ContainsKey("Site"));
            var rows = db.Query("SELECT \"Site\", \"Meas\" FROM \"data\" ORDER BY \"Meas\"");
            Assert.Equal(3, rows.Count);
            Assert.Equal("A", rows[0]["Site"]);
            Assert.Null(rows[2]["Site"]);
        }

        [Fact]
        public async Task Bounce_DoesNotExtractAllNullColumn()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new string?[] { null, null });
            db.AddColumn<int>("Meas", new[] { 1, 2 });
            await db.Bounce();
            Assert.False(db.Metadata.ContainsKey("Site"));
            var rows = db.Query("SELECT \"Site\", \"Meas\" FROM \"data\" ORDER BY \"Meas\"");
            Assert.Equal(2, rows.Count);
            Assert.Null(rows[0]["Site"]);
            Assert.Null(rows[1]["Site"]);
        }

        [Fact]
        public async Task Bounce_EmptyTable_NoOp()
        {
            using var db = new DataBall();
            await db.Bounce();
            Assert.Empty(db.Metadata);
            Assert.Equal(0, CountDataRows(db));
        }

        [Fact]
        public async Task Bounce_ZeroRowTable_DoesNotExtractColumns()
        {
            using var db = new DataBall();
            db.AddColumn("Site", Array.Empty<string?>());
            db.AddColumn<int>("Meas", Array.Empty<int>());
            await db.Bounce();
            Assert.False(db.Metadata.ContainsKey("Site"));
            Assert.False(db.Metadata.ContainsKey("Meas"));
            var cols = db.Query("""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'main' AND table_name = 'data'
                ORDER BY ordinal_position
                """);
            Assert.Equal(2, cols.Count);
            Assert.Equal("Site", cols[0]["column_name"]);
            Assert.Equal("Meas", cols[1]["column_name"]);
            Assert.Equal(0, CountDataRows(db));
        }

        [Fact]
        public async Task Bounce_PreservesExistingMetadata()
        {
            using var db = new DataBall();
            db.SetMetadata("Version", "1.0");
            db.AddColumn("Site", new[] { "A", "A" });
            db.AddColumn<int>("Meas", new[] { 1, 2 });
            await db.Bounce();
            Assert.Equal("1.0", db.Metadata["Version"]);
            Assert.Equal("A", db.Metadata["Site"]);
        }

        [Fact]
        public async Task Bounce_IsIdempotent()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new[] { "A", "A", "A" });
            db.AddColumn<int>("Meas", new[] { 1, 1, 2 });
            await db.Bounce();
            var meta1 = db.Metadata.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var rows1 = db.Query("SELECT * FROM \"data\" ORDER BY \"Meas\"");
            await db.Bounce();
            Assert.Equal(meta1.Count, db.Metadata.Count);
            Assert.Equal("A", db.Metadata["Site"]);
            var rows2 = db.Query("SELECT * FROM \"data\" ORDER BY \"Meas\"");
            Assert.Equal(rows1.Count, rows2.Count);
            Assert.Equal(Convert.ToInt32(rows1[0]["Meas"]), Convert.ToInt32(rows2[0]["Meas"]));
            Assert.Equal(Convert.ToInt32(rows1[1]["Meas"]), Convert.ToInt32(rows2[1]["Meas"]));
        }

        [Fact]
        public async Task Squish_WithoutPath_MatchesBounce()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new[] { "A", "A", "B" });
            db.AddColumn<int>("Meas", new[] { 1, 1, 2 });
            await db.Squish();
            Assert.False(db.Metadata.ContainsKey("Site"));
            var rows = db.Query("SELECT \"Site\", \"Meas\" FROM \"data\" ORDER BY \"Site\", \"Meas\"");
            Assert.Equal(2, rows.Count);
            Assert.Equal("A", rows[0]["Site"]?.ToString());
            Assert.Equal(1, Convert.ToInt32(rows[0]["Meas"]));
            Assert.Equal("B", rows[1]["Site"]?.ToString());
            Assert.Equal(2, Convert.ToInt32(rows[1]["Meas"]));
        }

        [Fact]
        public async Task Squish_WritesHiveDirs()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddColumn("Site", new[] { "Lab1", "Lab1", "Lab2" });
                db.AddColumn<int>("Meas", new[] { 1, 2, 3 });
                await db.Squish(dir, new[] { "Site" });
                await AssertHiveImported(dir, "Site=Lab1", "Site", "Lab1");
                await AssertHiveImported(dir, "Site=Lab2", "Site", "Lab2");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_DoesNotExtractPartitionColumnEvenIfConstant()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddColumn("Site", new[] { "Lab1", "Lab1" });
                db.AddColumn<int>("Meas", new[] { 1, 2 });
                await db.Squish(dir, new[] { "Site" });
                Assert.False(db.Metadata.ContainsKey("Site"));
                var rows = db.Query("SELECT \"Site\", \"Meas\" FROM \"data\" ORDER BY \"Meas\"");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Lab1", rows[0]["Site"]);
                Assert.Equal("Lab1", rows[1]["Site"]);
                Assert.True(Directory.Exists(Path.Combine(dir, "Site=Lab1")));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_MissingPartitionColumn_Throws()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddColumn("Site", new[] { "Lab1" });
                db.AddColumn<int>("Meas", new[] { 1 });
                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.Squish(dir, new[] { "Nope" }));
                Assert.Contains("Nope", ex.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("No data to export", ex.ToString(), StringComparison.Ordinal);
                Assert.False(db.Metadata.ContainsKey("Site"));
                Assert.False(db.Metadata.ContainsKey("Meas"));
                var cols = ColumnNames(db);
                Assert.Equal(new[] { "Site", "Meas" }, cols);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_MissingPartitionColumn_DoesNotExtractExistingColumns()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddColumn("Site", new[] { "Lab1", "Lab1" });
                db.AddColumn<int>("Meas", new[] { 1, 2 });
                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.Squish(dir, new[] { "Nope" }));
                Assert.Contains("Nope", ex.ToString(), StringComparison.Ordinal);
                Assert.False(db.Metadata.ContainsKey("Site"));
                var cols = ColumnNames(db);
                Assert.Contains("Site", cols);
                Assert.Contains("Meas", cols);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_WritesHiveDirs_WhenOnlyPartitionColumnRemains()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddColumn("Site", new[] { "Lab1", "Lab1", "Lab2" });
                db.AddColumn<int>("Meas", new[] { 1, 1, 1 });
                await db.Squish(dir, new[] { "Site" });
                Assert.Equal(1, Convert.ToInt32(db.Metadata["Meas"]));
                Assert.False(db.Metadata.ContainsKey("Site"));
                var rows = db.Query("SELECT \"Site\" FROM \"data\" ORDER BY \"Site\"");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Lab1", rows[0]["Site"]);
                Assert.Equal("Lab2", rows[1]["Site"]);
                AssertHiveParquet(dir, "Site=Lab1");
                AssertHiveParquet(dir, "Site=Lab2");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_WritesHiveDirs_OnlyPartitionColumn()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddColumn("Site", new[] { "Lab1", "Lab2" });
                await db.Squish(dir, new[] { "Site" });
                AssertHiveParquet(dir, "Site=Lab1");
                AssertHiveParquet(dir, "Site=Lab2");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_ReplacesStaleHivePartitions()
        {
            var dir = TempDir();
            try
            {
                using (var first = new DataBall())
                {
                    first.AddColumn("Site", new[] { "Lab1", "Lab2" });
                    first.AddColumn<int>("Meas", new[] { 1, 2 });
                    await first.Squish(dir, new[] { "Site" });
                    AssertHiveParquet(dir, "Site=Lab1");
                    AssertHiveParquet(dir, "Site=Lab2");
                }

                using var second = new DataBall();
                second.AddColumn("Site", new[] { "Lab3", "Lab4" });
                second.AddColumn<int>("Meas", new[] { 3, 4 });
                await second.Squish(dir, new[] { "Site" });
                Assert.False(Directory.Exists(Path.Combine(dir, "Site=Lab1")));
                Assert.False(Directory.Exists(Path.Combine(dir, "Site=Lab2")));
                AssertHiveParquet(dir, "Site=Lab3");
                AssertHiveParquet(dir, "Site=Lab4");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_DirectoryContainsForeignFiles_ThrowsAndPreservesThem()
        {
            var dir = TempDir();
            try
            {
                var keep = Path.Combine(dir, "DO_NOT_DELETE.txt");
                File.WriteAllText(keep, "keep");
                using var db = new DataBall();
                db.AddColumn("Site", new[] { "Lab1", "Lab2" });
                db.AddColumn<int>("Meas", new[] { 1, 2 });
                await Assert.ThrowsAsync<DataBallException>(() => db.Squish(dir, new[] { "Site" }));
                Assert.True(File.Exists(keep), keep);
                Assert.Equal("keep", File.ReadAllText(keep));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_DirectoryWithOnlyHivePartitions_Overwrites()
        {
            var dir = TempDir();
            try
            {
                var stale = Path.Combine(dir, "Site=A");
                Directory.CreateDirectory(stale);
                File.WriteAllText(Path.Combine(stale, "data_0.parquet"), "stale");
                using var db = new DataBall();
                db.AddColumn("Site", new[] { "Lab1", "Lab2" });
                db.AddColumn<int>("Meas", new[] { 1, 2 });
                await db.Squish(dir, new[] { "Site" });
                Assert.False(Directory.Exists(stale));
                AssertHiveParquet(dir, "Site=Lab1");
                AssertHiveParquet(dir, "Site=Lab2");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Squish_PathWithoutPartitionColumns_DoesNotWrite()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddColumn("Site", new[] { "Lab1", "Lab2" });
                db.AddColumn<int>("Meas", new[] { 1, 2 });
                var outDir = Path.Combine(dir, "out");
                await db.Squish(outDir, null);
                Assert.False(Directory.Exists(outDir));
                Assert.Empty(Directory.GetFiles(dir, "*.parquet", SearchOption.AllDirectories));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Bounce_DoesNotCreateChunkMetadataTables()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new[] { "A", "A" });
            db.AddColumn<int>("Meas", new[] { 1, 2 });
            await db.Bounce();
            var tables = db.Query("""
                SELECT table_name FROM information_schema.tables
                WHERE table_schema IN ('main', 'temp')
                """);
            Assert.DoesNotContain(tables, r =>
                string.Equals(Convert.ToString(r["table_name"]), "ChunkMetadataTables", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Bounce_ExtractsLastConstantColumn_DataTableMayBeGone()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new[] { "A", "A" });
            await db.Bounce();
            Assert.Equal("A", db.Metadata["Site"]);
            Assert.Equal(0, CountDataRows(db));
        }

        [Fact]
        public async Task Bounce_QuotedKeywordColumnOrder()
        {
            using var db = new DataBall();
            db.AddColumn<int>("Order", new[] { 1, 1 });
            db.AddColumn("Name", new[] { "A", "B" });
            await db.Bounce();
            Assert.Equal(1, Convert.ToInt32(db.Metadata["Order"]));
            var rows = db.Query("SELECT \"Name\" FROM \"data\" ORDER BY \"Name\"");
            Assert.Equal(2, rows.Count);
            Assert.False(rows[0].ContainsKey("Order"));
            Assert.Equal("A", rows[0]["Name"]);
            Assert.Equal("B", rows[1]["Name"]);
        }

        [Fact]
        public async Task Bounce_TypedConstantsBoolAndLong()
        {
            using var db = new DataBall();
            db.AddColumn<bool>("Flag", new[] { true, true });
            db.AddColumn<long>("N", new[] { 5L, 5L });
            db.AddColumn("Name", new[] { "A", "B" });
            await db.Bounce();
            Assert.True(Assert.IsType<bool>(db.Metadata["Flag"]));
            Assert.IsType<long>(db.Metadata["N"]);
            Assert.Equal(5L, Convert.ToInt64(db.Metadata["N"]));
            var rows = db.Query("SELECT \"Name\" FROM \"data\" ORDER BY \"Name\"");
            Assert.Equal(2, rows.Count);
            Assert.False(rows[0].ContainsKey("Flag"));
            Assert.False(rows[0].ContainsKey("N"));
        }

        [Fact]
        public async Task InitializeRow_AfterBounce_CopiesLastCommittedRow()
        {
            using var db = new DataBall();
            foreach (var seq in new[] { 5, 4, 3, 2, 1 })
            {
                db.InitializeRow(new Dictionary<string, object?>
                {
                    ["Seq"] = seq,
                    ["Name"] = "r" + seq
                });
                db.CommitRow();
            }

            await db.Bounce();
            db.InitializeRow();
            db.ModifyField("Marker", "last");
            db.CommitRow();
            var row = Assert.Single(db.Query("SELECT Seq FROM data WHERE Marker = 'last'"));
            Assert.Equal(1, Convert.ToInt32(row["Seq"]));
        }

        private static string[] ColumnNames(DataBall db)
        {
            return db.Query("""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'main' AND table_name = 'data'
                ORDER BY ordinal_position
                """).Select(r => Convert.ToString(r["column_name"]) ?? string.Empty).ToArray();
        }

        private static async Task AssertHiveImported(string root, string hiveDir, string column, string value)
        {
            var path = Path.Combine(root, hiveDir);
            var files = Directory.GetFiles(path, "*.parquet", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
            using var imported = new DataBall();
            await imported.ImportAsync(files[0]);
            var rows = imported.Query($"SELECT \"{column}\" FROM \"data\"");
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal(value, r[column]?.ToString()));
        }

        private static void AssertHiveParquet(string root, string hiveDir)
        {
            var path = Path.Combine(root, hiveDir);
            Assert.True(Directory.Exists(path), path);
            Assert.NotEmpty(Directory.GetFiles(path, "*.parquet", SearchOption.AllDirectories));
        }

        private static long CountDataRows(DataBall db)
        {
            var exists = db.Query("""
                SELECT COUNT(*) AS c FROM information_schema.tables
                WHERE table_schema = 'main' AND table_name = 'data'
                """);
            if (Convert.ToInt32(exists[0]["c"]) == 0)
                return 0;
            var count = db.Query("SELECT COUNT(*) AS c FROM \"data\"");
            return Convert.ToInt64(count[0]["c"]);
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-pr5", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
