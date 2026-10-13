// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// db-16: one way in (<c>Open</c> / <c>ImportAsync</c>) and one way out (<c>SaveAsync</c> / <c>ExportAsync</c>).
    /// </summary>
    [Collection("DataBall.Handlers")]
    public class EntryPointTests
    {
        [Fact]
        public async Task Open_OnePathAndMany_SameOptions()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAda,36\nBob,41\n");
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """{ "columns": { "Name": "string", "Age": "int" } }""");

                // One options object serves the single-path and the many-path form.
                var options = new OpenOptions { SchemaPath = config };
                using var one = DataBall.Open(csv, options);
                using var many = DataBall.Open(new[] { csv }, options);
                const string sql = "SELECT Name, Age FROM data ORDER BY Name";
                var oneRows = one.Query(sql);
                Assert.Equal(new[] { "Ada:36", "Bob:41" }, oneRows.Select(r => $"{r["Name"]}:{r["Age"]}").ToArray());
                Assert.All(oneRows, r => Assert.IsType<int>(r["Age"]));
                var manyRows = many.Query(sql);
                Assert.Equal(oneRows.Select(r => $"{r["Name"]}:{r["Age"]}"), manyRows.Select(r => $"{r["Name"]}:{r["Age"]}"));
                Assert.All(manyRows, r => Assert.IsType<int>(r["Age"]));
                Assert.Equal(one.Schema.Columns, many.Schema.Columns);

                // Writable travels in the same options: both forms open a native .ball for changes, and both refuse without it.
                var ball = Path.Combine(dir, "session.ball");
                await one.SaveAsync(ball);
                var writable = new OpenOptions { Writable = true };
                using (var a = DataBall.Open(ball, writable))
                    a.SetMetadata("who", "path");
                using (var b = DataBall.Open(new[] { ball }, writable))
                {
                    Assert.Equal("path", b.Metadata["who"]);
                    b.SetMetadata("who", "list");
                }
                var readOnly = new OpenOptions();
                using (var c = DataBall.Open(ball, readOnly))
                {
                    Assert.Equal("list", c.Metadata["who"]);
                    Assert.Throws<DataBallException>(() => c.SetMetadata("who", "no"));
                }
                using (var d = DataBall.Open(new[] { ball }, readOnly))
                    Assert.Throws<DataBallException>(() => d.SetMetadata("who", "no"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task OpenAsync_CancellationTokenLast()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAda,36\n");
                var ball = Path.Combine(dir, "session.ball");
                using (var seed = DataBall.Open(csv))
                    await seed.SaveAsync(ball);

                using var live = new CancellationTokenSource();
                // The token is the last parameter: it is named after the path, or after the options.
                using (var byName = await DataBall.OpenAsync(csv, cancellationToken: live.Token))
                    Assert.Single(byName.Query("SELECT * FROM data"));
                using (var byOptions = await DataBall.OpenAsync(csv, new OpenOptions(), live.Token))
                    Assert.Single(byOptions.Query("SELECT * FROM data"));
                using (var byList = await DataBall.OpenAsync(new[] { csv }, cancellationToken: live.Token))
                    Assert.Single(byList.Query("SELECT * FROM data"));

                // A cancelled token stops every route: a staged CSV and a native .ball, by path or by list.
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DataBall.OpenAsync(csv, cancellationToken: cancelled.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DataBall.OpenAsync(csv, new OpenOptions(), cancelled.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DataBall.OpenAsync(ball, cancellationToken: cancelled.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DataBall.OpenAsync(new[] { ball }, cancellationToken: cancelled.Token));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_ManyInputs_Append()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id,v\n1,10\n");
                var b = Csv(dir, "b.csv", "id,v\n2,20\n");
                var c = Csv(dir, "c.csv", "id,v\n3,30\n");
                using var db = new DataBall();

                await db.ImportAsync(new[] { a });
                Assert.Equal(new long[] { 1 }, Ids(db));

                // Append keeps what is there and adds every input, in the order named.
                await db.ImportAsync(new[] { b, c }, new ImportOptions { Append = true });
                Assert.Equal(new long[] { 1, 2, 3 }, Ids(db));
                Assert.Equal(new[] { a, b, c }, db.Sources.Select(s => s.Path).ToArray());

                // Without Append the inputs replace the session's rows (the later ones still add to the first).
                await db.ImportAsync(new[] { b, c });
                Assert.Equal(new long[] { 2, 3 }, Ids(db));
                Assert.Equal(new[] { b, c }, db.Sources.Select(s => s.Path).ToArray());

                // One bad input and nothing of the others lands.
                var missing = Path.Combine(dir, "missing.csv");
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(new[] { a, missing }, new ImportOptions { Append = true }));
                Assert.Equal(new long[] { 2, 3 }, Ids(db));

                // A cancelled token stops the import before it writes.
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.ImportAsync(new[] { a }, new ImportOptions { Append = true }, cancelled.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.ImportAsync(a, new ImportOptions { Append = true }, cancelled.Token));
                Assert.Equal(new long[] { 2, 3 }, Ids(db));

                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(Array.Empty<string>()));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_ManyInputs_LaterFailureAfterEarlierWrite_LeavesSessionAsItWas()
        {
            var dir = TempDir();
            try
            {
                var seed = Csv(dir, "seed.csv", "id\n9\n");
                var a = Csv(dir, "a.csv", "id\n1\n");
                var bad = Csv(dir, "bad.csv", "id\nx\n");
                using var db = new DataBall();
                await db.ImportAsync(seed);
                var sourcesBefore = db.Sources.Select(s => s.Path).ToArray();

                // "a" is merged first; "bad" then cannot join the BIGINT id column, so "a" must not stay either.
                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(new[] { a, bad }, new ImportOptions { Append = true }));

                Assert.Equal(new long[] { 9 }, Ids(db));
                Assert.Equal(sourcesBefore, db.Sources.Select(s => s.Path).ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ImportAsync_FromDataBall_ReplacesMergeOrAppend()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id,v\n1,10\n");
                var b = Csv(dir, "b.csv", "id,v\n2,20\n3,30\n");
                using var db = new DataBall();
                using var other = new DataBall();
                await db.ImportAsync(a);
                await other.ImportAsync(b);

                await db.ImportAsync(other, new ImportOptions { Append = true });
                Assert.Equal(new long[] { 1, 2, 3 }, Ids(db));
                Assert.Equal(new[] { a, b }, db.Sources.Select(s => s.Path).ToArray());

                // Without Append the other session replaces the rows.
                await db.ImportAsync(other);
                Assert.Equal(new long[] { 2, 3 }, Ids(db));
                Assert.Equal(new[] { b }, db.Sources.Select(s => s.Path).ToArray());

                // The other session is only read.
                Assert.Equal(new long[] { 2, 3 }, Ids(other));

                other.Dispose();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => db.ImportAsync(other));
                await Assert.ThrowsAsync<ArgumentNullException>(() => db.ImportAsync((DataBall)null!));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ExportAsync_FormatOptions_CsvParquetArchive()
        {
            var dir = TempDir();
            try
            {
                // Rows added by hand carry no source lineage, so the files hold exactly the two columns.
                using var db = new DataBall();
                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["id"] = 1L, ["v"] = 10L },
                    new Dictionary<string, object?> { ["id"] = 2L, ["v"] = 20L },
                });

                var csv = Path.Combine(dir, "out.csv");
                await db.ExportAsync(csv, new ExportOptions { Format = ExportType.Csv });
                Assert.Equal(new[] { "id,v", "1,10", "2,20" }, File.ReadAllLines(csv));

                var parquet = Path.Combine(dir, "out.parquet");
                await db.ExportAsync(parquet, new ExportOptions { Format = ExportType.Parquet });
                using (var back = DataBall.Open(parquet))
                    Assert.Equal(new long[] { 1, 2 }, Ids(back));

                foreach (var name in new[] { "out.zip", "out.tar.gz" })
                {
                    var archive = Path.Combine(dir, name);
                    await db.ExportAsync(archive, new ExportOptions { Format = ExportType.Archive });
                    using var back = DataBall.Open(archive);
                    Assert.Equal(new long[] { 1, 2 }, Ids(back));
                }

                // PartitionBy writes a hive directory, one column=value folder per value, that Open reads back as one source.
                var hive = Path.Combine(dir, "hive");
                await db.ExportAsync(hive, new ExportOptions { Format = ExportType.Parquet, PartitionBy = new[] { "id" } });
                Assert.Equal(new[] { "id=1", "id=2" }, Directory.GetDirectories(hive).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                Assert.All(Directory.GetDirectories(hive), d => Assert.Single(Directory.GetFiles(d, "*.parquet")));
                using (var back = DataBall.Open(hive))
                    Assert.Equal(new long[] { 1, 2 }, Ids(back));

                // No partition columns: the one file lands inside the directory.
                var flat = Path.Combine(dir, "flat");
                await db.ExportAsync(flat, new ExportOptions { Format = ExportType.Parquet, PartitionBy = Array.Empty<string>() });
                Assert.True(File.Exists(Path.Combine(flat, "data.parquet")));

                // PartitionBy belongs to Parquet; any other format refuses it before writing.
                foreach (var (format, name) in new[] { (ExportType.Csv, "p.csv"), (ExportType.Archive, "p.zip") })
                {
                    var refused = Path.Combine(dir, name);
                    var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ExportAsync(refused, new ExportOptions { Format = format, PartitionBy = new[] { "id" } }));
                    Assert.Contains("PartitionBy", ex.Message, StringComparison.Ordinal);
                    Assert.False(File.Exists(refused));
                }

                // The format names the file kind; .ball is saved with SaveAsync, so ExportType has no Ball.
                Assert.Equal(new[] { "Archive", "Csv", "Parquet" }, Enum.GetNames<ExportType>().OrderBy(n => n, StringComparer.Ordinal).ToArray());
                await Assert.ThrowsAsync<ArgumentNullException>(() => db.ExportAsync(csv, null!));
                await Assert.ThrowsAsync<DataBallException>(() => db.ExportAsync(" ", new ExportOptions { Format = ExportType.Csv }));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ExportAsync_PartitionBy_HonoursApplyFilter()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["id"] = 1L, ["v"] = 10L },
                    new Dictionary<string, object?> { ["id"] = 2L, ["v"] = 20L },
                    new Dictionary<string, object?> { ["id"] = 3L, ["v"] = 30L },
                });
                db.ApplyFilter(new SessionFilter
                {
                    Predicates = [new ColumnPredicate { Column = "id", Op = PredicateOp.Ge, Value = 2L }],
                });

                // Both partitioned forms write only the filtered rows, as the plain Parquet export does.
                var hive = Path.Combine(dir, "hive");
                await db.ExportAsync(hive, new ExportOptions { Format = ExportType.Parquet, PartitionBy = new[] { "id" } });
                Assert.Equal(new[] { "id=2", "id=3" }, Directory.GetDirectories(hive).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                using (var back = DataBall.Open(hive))
                    Assert.Equal(new long[] { 2, 3 }, Ids(back));

                var flat = Path.Combine(dir, "flat");
                await db.ExportAsync(flat, new ExportOptions { Format = ExportType.Parquet, PartitionBy = Array.Empty<string>() });
                using (var back = DataBall.Open(Path.Combine(flat, "data.parquet")))
                    Assert.Equal(new long[] { 2, 3 }, Ids(back));

                db.ApplyFilter(null);
                var all = Path.Combine(dir, "all");
                await db.ExportAsync(all, new ExportOptions { Format = ExportType.Parquet, PartitionBy = new[] { "id" } });
                Assert.Equal(3, Directory.GetDirectories(all).Length);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ExportAsync_PartitionBy_RefusesFilterThatDropsPartitionColumn()
        {
            var dir = TempDir();
            try
            {
                using var db = new DataBall();
                db.AddRows(new[]
                {
                    new Dictionary<string, object?> { ["id"] = 1L, ["v"] = 10L },
                    new Dictionary<string, object?> { ["id"] = 2L, ["v"] = 20L },
                });
                db.ApplyFilter(new SessionFilter { Columns = new[] { "v" } });

                // The projection has no "id", so the partitions cannot be written. The refusal comes before any file or folder is made.
                var hive = Path.Combine(dir, "hive");
                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ExportAsync(hive, new ExportOptions { Format = ExportType.Parquet, PartitionBy = new[] { "id" } }));
                Assert.Equal("Partition column 'id' is not in the filtered columns", ex.Message);
                Assert.False(Directory.Exists(hive) && Directory.EnumerateFileSystemEntries(hive).Any());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Ctor_ExistingFile_ThrowsPointingToOpen()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "session.ball");
                // A new path creates a session.
                using (var created = new DataBall(databasePath: path))
                    created.SetMetadata("who", "ctor");
                var length = new FileInfo(path).Length;

                var ex = Assert.Throws<DataBallException>(() => new DataBall(databasePath: path));
                Assert.Contains("Open(path, new OpenOptions { Writable = true })", ex.Message, StringComparison.Ordinal);
                Assert.Contains(path, ex.Message, StringComparison.Ordinal);
                Assert.Equal(length, new FileInfo(path).Length);

                // Any file that exists is refused, not only a session.
                var stray = Path.Combine(dir, "notes.txt");
                File.WriteAllText(stray, "not a database");
                Assert.Throws<DataBallException>(() => new DataBall(databasePath: stray));
                Assert.Equal("not a database", File.ReadAllText(stray));

                // The way back in is Open, writable.
                using var reopened = DataBall.Open(path, new OpenOptions { Writable = true });
                Assert.Equal("ctor", reopened.Metadata["who"]);
                reopened.SetMetadata("who", "open");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static long[] Ids(DataBall db) =>
            db.Query("SELECT id FROM data ORDER BY id").Select(r => Convert.ToInt64(r["id"])).ToArray();

        private static string Csv(string dir, string name, string text)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-entry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
