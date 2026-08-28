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
            using var bounced = new DataBall();
            using var squished = new DataBall();
            bounced.AddColumn("Site", new[] { "A", "A", "B" });
            bounced.AddColumn<int>("Meas", new[] { 1, 1, 2 });
            squished.AddColumn("Site", new[] { "A", "A", "B" });
            squished.AddColumn<int>("Meas", new[] { 1, 1, 2 });
            await bounced.Bounce();
            await squished.Squish();
            Assert.Equal(bounced.Metadata.Count, squished.Metadata.Count);
            var bounceRows = bounced.Query("SELECT \"Site\", \"Meas\" FROM \"data\" ORDER BY \"Site\", \"Meas\"");
            var squishRows = squished.Query("SELECT \"Site\", \"Meas\" FROM \"data\" ORDER BY \"Site\", \"Meas\"");
            Assert.Equal(bounceRows.Count, squishRows.Count);
            for (int i = 0; i < bounceRows.Count; i++)
            {
                Assert.Equal(bounceRows[i]["Site"], squishRows[i]["Site"]);
                Assert.Equal(Convert.ToInt32(bounceRows[i]["Meas"]), Convert.ToInt32(squishRows[i]["Meas"]));
            }
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
                var lab1 = Path.Combine(dir, "Site=Lab1");
                var lab2 = Path.Combine(dir, "Site=Lab2");
                Assert.True(Directory.Exists(lab1));
                Assert.True(Directory.Exists(lab2));
                Assert.NotEmpty(Directory.GetFiles(lab1, "*.parquet", SearchOption.AllDirectories));
                Assert.NotEmpty(Directory.GetFiles(lab2, "*.parquet", SearchOption.AllDirectories));
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
                await Assert.ThrowsAsync<DataBallException>(() => db.Squish(dir, new[] { "Nope" }));
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
