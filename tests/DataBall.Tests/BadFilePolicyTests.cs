// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// db-24: <c>OpenOptions.OnBadFile</c> decides whether a bad file fails a many-file open (<c>Abort</c>, the default) or is
    /// left out and listed in <see cref="OpenReport.Skipped"/> (<c>Skip</c>). A type conflict between files aborts under either.
    /// </summary>
    [Collection("DataBall.Handlers")]
    public class BadFilePolicyTests
    {
        private static readonly OpenOptions Skip = new() { OnBadFile = BadFilePolicy.Skip };

        [Fact]
        public void Open_BadFile_Abort_ThrowsNamingTheFile()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id\n1\n");
                var bad = BadParquet(dir, "b.parquet");
                var c = Csv(dir, "c.csv", "id\n3\n");

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, bad, c }));
                Assert.Contains(bad, ex.Message);

                // The failed open left nothing that blocks the same files opening without the bad one.
                using var db = DataBall.Open(new[] { a, c });
                Assert.Equal(2, Rows(db));
                Assert.Empty(db.OpenReport!.Skipped);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_BadFile_Abort_LeavesNoStoreBehind()
        {
            var dir = TempDir();
            var temp = Path.Combine(dir, "temp");
            Directory.CreateDirectory(temp);
            try
            {
                var a = Csv(dir, "a.csv", "id\n1\n");
                var bad = BadParquet(dir, "b.parquet");
                var engine = new EngineOptions { Store = StoreMode.File, TempDirectory = temp };

                var reserved = new List<string>();
                DuckDbStore.ExclusivePathReservedForTests = path => { lock (reserved) reserved.Add(path); };
                try
                {
                    Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, bad }, new OpenOptions { Engine = engine }));
                }
                finally
                {
                    DuckDbStore.ExclusivePathReservedForTests = null;
                }
                // The failed session reserved its store file in the open-file gate, and the gate is clear once it has failed.
                var ours = reserved.Where(p => p.StartsWith(temp, StringComparison.Ordinal)).ToList();
                Assert.NotEmpty(ours);
                foreach (var path in ours)
                    Assert.False(DuckDbStore.HoldsExclusivePathForTests(path), $"still in the open-file gate: {path}");
                // The temporary file store of the failed session is gone, not held open.
                Assert.Empty(Directory.GetFileSystemEntries(temp));

                using var db = DataBall.Open(new[] { a, Csv(dir, "c.csv", "id\n2\n") }, new OpenOptions { Engine = engine });
                Assert.Equal(2, Rows(db));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_BadFile_Skip_OtherFilesLand()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id\n1\n");
                var bad = BadParquet(dir, "b.parquet");
                var c = Csv(dir, "c.csv", "id\n3\n");

                using var db = DataBall.Open(new[] { a, bad, c }, Skip);

                Assert.Equal(new[] { a, c }, db.Sources.Select(s => s.Path).ToArray());
                // The skipped file sits between the others and leaves no gap in the source keys.
                Assert.Equal(new[] { 1L, 2L }, db.Sources.Select(s => s.Key).ToArray());
                Assert.Equal(2, Rows(db));
                Assert.Equal(new[] { (a, 1L), (c, 1L) }, db.OpenReport!.Sources.Select(s => (s.Source.Path, s.Rows)).ToArray());

                var skipped = Assert.Single(db.OpenReport.Skipped);
                Assert.Equal(bad, skipped.Path);
                Assert.False(string.IsNullOrWhiteSpace(skipped.Message));
                Assert.Null(skipped.Row);
                Assert.Null(skipped.Column);
                Assert.Null(skipped.Value);

                // Nothing of the skipped file is left in the session: no staging table, no source row.
                Assert.Empty(db.Query("SELECT table_name FROM duckdb_tables() WHERE table_name LIKE '_stg_%'"));
                Assert.Equal(2L, Convert.ToInt64(db.Query("SELECT count(*) AS n FROM source")[0]["n"]));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_AllBad_Skip_Throws()
        {
            var dir = TempDir();
            try
            {
                var one = BadParquet(dir, "one.parquet");
                var two = BadParquet(dir, "two.parquet");

                var many = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { one, two }, Skip));
                Assert.Contains(one, many.Message);
                Assert.Contains(two, many.Message);

                var single = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { one }, Skip));
                Assert.Contains(one, single.Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_UnknownFile_NamedIsSkipped_ExpandedIsIgnored()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id\n1\n");
                var notes = Csv(dir, "notes.txt", "not data");

                // Named by the caller: Abort throws as it always did; Skip lists it as skipped.
                var named = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, notes }));
                Assert.Contains("notes.txt", named.Message);
                using (var db = DataBall.Open(new[] { a, notes }, Skip))
                {
                    Assert.Equal(new[] { a }, db.Sources.Select(s => s.Path).ToArray());
                    var skipped = Assert.Single(db.OpenReport!.Skipped);
                    Assert.Equal(notes, skipped.Path);
                    Assert.Empty(db.OpenReport.Ignored);
                }

                // Found by expanding a directory: ignored, under either policy, and never skipped.
                using var expanded = DataBall.Open(new[] { dir }, Skip);
                Assert.Equal(new[] { notes }, expanded.OpenReport!.Ignored.ToArray());
                Assert.Empty(expanded.OpenReport.Skipped);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_BadArchiveEntry_SkipsOnlyTheEntry()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id,Lot\n5,L1\n");
                var zip = Path.Combine(dir, "lots.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    AddEntry(archive, "e1.csv", "id,Lot\n1,L1\n2,L1\n");
                    // Lot varies within this entry, which requireConstant refuses.
                    AddEntry(archive, "e2.csv", "id,Lot\n3,L1\n4,L2\n");
                }
                var require = Path.Combine(dir, "require.json");
                File.WriteAllText(require, """{ "metadataPolicy": "requireConstant" }""");

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, zip }, new OpenOptions { SchemaPath = require }));
                Assert.Contains(zip + "!e2.csv", ex.Message);

                using var db = DataBall.Open(new[] { a, zip }, new OpenOptions { SchemaPath = require, OnBadFile = BadFilePolicy.Skip });
                Assert.Equal(new[] { a, zip + "!e1.csv" }, db.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(3, Rows(db));
                var skipped = Assert.Single(db.OpenReport!.Skipped);
                Assert.Equal(zip + "!e2.csv", skipped.Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_CsvValueThatCannotBeTyped_IsSkipped_FileAndArchiveEntry()
        {
            var dir = TempDir();
            try
            {
                // The header n(dB) types the column as a number, so the text in the second row fails to stage.
                var a = Csv(dir, "a.csv", "n(dB)\n1\n");
                var bad = Csv(dir, "b.csv", "n(dB)\nabc\n");
                var zip = Path.Combine(dir, "more.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    AddEntry(archive, "e1.csv", "n(dB)\n2\n");
                    AddEntry(archive, "e2.csv", "n(dB)\nxyz\n");
                }

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, bad }));
                Assert.Contains(bad, ex.Message);
                var inArchive = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, zip }));
                Assert.Contains(zip + "!e2.csv", inArchive.Message);

                using var db = DataBall.Open(new[] { a, bad, zip }, Skip);
                Assert.Equal(new[] { a, zip + "!e1.csv" }, db.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(new[] { 1L, 2L }, db.Sources.Select(s => s.Key).ToArray());
                Assert.Equal(new[] { bad, zip + "!e2.csv" }, db.OpenReport!.Skipped.Select(s => s.Path).ToArray());
                Assert.Equal(2, Rows(db));
                Assert.Empty(db.Query("SELECT table_name FROM duckdb_tables() WHERE table_name LIKE '_stg_%'"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_SkippedCsv_LeavesNoLearnedTypeBehind()
        {
            var dir = TempDir();
            try
            {
                // The skipped file learns n as a number from its header before its own value fails; the other file must not inherit it.
                var bad = Csv(dir, "a.csv", "n(dB)\nabc\n");
                var good = Csv(dir, "b.csv", "n\n1\n2\n");
                using var alone = DataBall.Open(new[] { good });
                using var db = DataBall.Open(new[] { bad, good }, Skip);

                Assert.Equal(alone.Store.GetColumns().Single(c => c.Name == "n").DuckDbType, db.Store.GetColumns().Single(c => c.Name == "n").DuckDbType);
                const string sql = "SELECT n FROM data ORDER BY n";
                Assert.Equal(alone.Query(sql), db.Query(sql));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_UnreadableArchive_SkipsTheWholeArchive()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id\n1\n");
                var broken = Path.Combine(dir, "broken.zip");
                File.WriteAllText(broken, "this is not a zip archive");

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, broken }));
                Assert.Contains(broken, ex.Message);

                using var db = DataBall.Open(new[] { a, broken }, Skip);
                Assert.Equal(new[] { a }, db.Sources.Select(s => s.Path).ToArray());
                var skipped = Assert.Single(db.OpenReport!.Skipped);
                Assert.Equal(broken, skipped.Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_TypeConflictBetweenFiles_AbortsUnderSkip_NamingBoth()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id,Program\n1,5\n");
                var b = Parquet(dir, "b.parquet", "SELECT 2 AS id, 'P' AS Program");

                foreach (var options in new[] { new OpenOptions(), Skip })
                {
                    var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, b }, options));
                    Assert.Contains("Program", ex.Message);
                    Assert.Contains(a, ex.Message);
                    Assert.Contains(b, ex.Message);
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_DataColumnTypeConflictBetweenFiles_AbortsUnderSkip_NamingBoth()
        {
            var dir = TempDir();
            try
            {
                // Gain is a data column (not a metadata field): the merge of the two types refuses it.
                var a = Csv(dir, "a.csv", "id,Gain\n1,5\n");
                var b = Parquet(dir, "b.parquet", "SELECT 2 AS id, 'P' AS Gain");

                foreach (var options in new[] { new OpenOptions(), Skip })
                {
                    var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, b }, options));
                    Assert.Contains("Gain", ex.Message);
                    Assert.Contains(a, ex.Message);
                    Assert.Contains(b, ex.Message);
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_RequireConstantWithinAFile_SkipsThatFile_AsIfNotThere()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id,Lot\n1,L1\n2,L1\n");
                var varying = Csv(dir, "v.csv", "id,Lot\n4,L1\n5,L2\n");
                var b = Csv(dir, "b.csv", "id,Lot\n3,L2\n");
                var require = Path.Combine(dir, "require.json");
                File.WriteAllText(require, """{ "metadataPolicy": "requireConstant" }""");

                using var db = DataBall.Open(new[] { a, varying, b }, new OpenOptions { SchemaPath = require, OnBadFile = BadFilePolicy.Skip });
                var skipped = Assert.Single(db.OpenReport!.Skipped);
                Assert.Equal(varying, skipped.Path);
                Assert.Contains("not constant", skipped.Message);
                Assert.Equal(new[] { a, b }, db.Sources.Select(s => s.Path).ToArray());

                // The same session as opening the other two files alone: the skipped file leaves no trace in config or columns.
                using var rest = DataBall.Open(new[] { a, b }, new OpenOptions { SchemaPath = require });
                Assert.Equal(SourceColumns(rest), SourceColumns(db));
                Assert.Equal(DataColumns(rest), DataColumns(db));
                // Lot is a per-source column here, so it is read through the source table.
                const string sql = "SELECT d.id, d._source_key, s.Lot FROM data d JOIN source s ON d._source_key = s.source_key ORDER BY d.id";
                Assert.Equal(rest.Query(sql), db.Query(sql));

                // The restart leaves no staging table of the first pass behind.
                Assert.Empty(db.Query("SELECT table_name FROM duckdb_tables() WHERE table_name LIKE '_stg_%'"));

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, varying, b }, new OpenOptions { SchemaPath = require }));
                Assert.Contains(varying, ex.Message);
                Assert.Contains("Lot", ex.Message);
                Assert.Contains("not constant", ex.Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_HandlerThatThrows_IsSkipped()
        {
            var dir = TempDir();
            DataBall.ClearHandlers();
            try
            {
                DataBall.RegisterHandler(new ExplodingHandler());
                var a = Csv(dir, "a.csv", "id\n1\n");
                // Sorted last, so no later source reuses its key: a staging table it left behind would survive to the end.
                var boom = Csv(dir, "z.boom", "anything");
                var c = Csv(dir, "c.csv", "id\n3\n");

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, boom, c }));
                Assert.Contains(boom, ex.Message);
                Assert.Contains("handler exploded", ex.Message);

                using var db = DataBall.Open(new[] { a, boom, c }, Skip);
                Assert.Equal(new[] { a, c }, db.Sources.Select(s => s.Path).ToArray());
                var skipped = Assert.Single(db.OpenReport!.Skipped);
                Assert.Equal(boom, skipped.Path);
                Assert.Contains("handler exploded", skipped.Message);
                Assert.Equal(new[] { 1L, 2L }, db.Sources.Select(s => s.Key).ToArray());
                Assert.Equal(2, Rows(db));
                Assert.Empty(db.Query("SELECT table_name FROM duckdb_tables() WHERE table_name LIKE '_stg_%'"));
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_RecursiveGlob_IgnoresStrayFiles()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "x1.csv", "id\n1\n");
                var b = Csv(dir, Path.Combine("sub", "x2.csv"), "id\n2\n");
                var underTemporary = Csv(dir, Path.Combine("_temporary", "0", "x3.csv"), "id\n9\n");
                var underHidden = Csv(dir, Path.Combine(".hidden", "x4.csv"), "id\n8\n");
                var underscore = Csv(dir, "_x5.csv", "id\n7\n");

                using var db = DataBall.Open(new[] { Path.Combine(dir, "*.csv") }, new OpenOptions { Recursive = true });

                Assert.Equal(new[] { a, b }.OrderBy(p => p, StringComparer.Ordinal).ToArray(), db.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(2, Rows(db));
                Assert.Equal(
                    new[] { underTemporary, underHidden, underscore }.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
                    db.OpenReport!.Ignored.ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Open_NativeAmongOtherInputs_IsRefusedUnderBothPolicies()
        {
            var dir = TempDir();
            try
            {
                var csv = Csv(dir, "a.csv", "id\n1\n");
                var notes = Csv(dir, "notes.txt", "not data");
                var ball = Path.Combine(dir, "s.ball");
                using (var seed = DataBall.Open(csv))
                    await seed.SaveAsync(ball);

                foreach (var options in new[] { new OpenOptions(), Skip })
                {
                    var withCsv = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { ball, csv }, options));
                    Assert.Contains("native", withCsv.Message);
                    // Under Skip the unknown file is skipped first; the native input is refused before that skip can hide it.
                    Assert.Throws<DataBallException>(() => DataBall.Open(new[] { ball, notes }, options));
                    var expanded = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { dir }, options));
                    Assert.Contains("native", expanded.Message);
                }

                using var one = DataBall.Open(new[] { ball });
                Assert.Equal(1L, Rows(one));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Open_NativeInADirectoryWithIgnoredFiles_OpensAsNative()
        {
            var seeds = TempDir();
            var withStray = TempDir();
            var withUnknown = TempDir();
            try
            {
                // Ignored files (a stray .DS_Store, an unrecognized notes.txt) are not inputs: the .ball is the sole input.
                var csv = Csv(seeds, "seed.csv", "id\n1\n");
                using (var seed = DataBall.Open(csv))
                {
                    await seed.SaveAsync(Path.Combine(withStray, "s.ball"));
                    await seed.SaveAsync(Path.Combine(withUnknown, "s.ball"));
                }
                Csv(withStray, ".DS_Store", "x");
                Csv(withUnknown, "notes.txt", "not data");

                foreach (var options in new[] { new OpenOptions(), Skip })
                {
                    foreach (var folder in new[] { withStray, withUnknown })
                    {
                        using (var viaString = DataBall.Open(folder))
                        {
                            Assert.Equal(1L, Rows(viaString));
                            Assert.Null(viaString.OpenReport);
                        }

                        using var viaInputs = DataBall.Open(new[] { folder }, options);
                        Assert.Equal(1L, Rows(viaInputs));
                        Assert.Null(viaInputs.OpenReport);
                    }
                }
            }
            finally
            {
                Directory.Delete(seeds, true);
                Directory.Delete(withStray, true);
                Directory.Delete(withUnknown, true);
            }
        }

        [Fact]
        public void Open_RequireConstant_IsCheckedPerSource_ProbeMatchesOpeningWithoutTheOffender()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id,Lot\n1,L1\n2,L1\n");
                // b's Lot varies within the file, and its n(dB) header teaches n a number that c's text cannot merge with.
                var b = Csv(dir, "b.csv", "id,Lot,n(dB)\n3,L1,1.5\n4,L2,2.5\n");
                var c = Csv(dir, "c.csv", "id,Lot,n\n5,L1,abc\n");
                var require = Path.Combine(dir, "require.json");
                File.WriteAllText(require, """{ "metadataPolicy": "requireConstant" }""");
                var schema = new OpenOptions { SchemaPath = require };

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, b, c }, schema));
                Assert.Contains(b, ex.Message);
                Assert.Contains("not constant", ex.Message);

                using var db = DataBall.Open(new[] { a, b, c }, new OpenOptions { SchemaPath = require, OnBadFile = BadFilePolicy.Skip });
                Assert.Equal(new[] { a, c }, db.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(new[] { 1L, 2L }, db.Sources.Select(s => s.Key).ToArray());
                var skipped = Assert.Single(db.OpenReport!.Skipped);
                Assert.Equal(b, skipped.Path);
                Assert.Contains("not constant", skipped.Message);

                // The session is the one opening a and c alone makes: b left no learned type or column behind.
                using var alone = DataBall.Open(new[] { a, c }, schema);
                Assert.Equal(DataColumns(alone), DataColumns(db));
                Assert.Equal(SourceColumns(alone), SourceColumns(db));
                Assert.Equal(alone.Metadata, db.Metadata);
                const string sql = "SELECT d.id, d._source_key, d.n FROM data d ORDER BY d.id";
                Assert.Equal(alone.Query(sql), db.Query(sql));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_NonRecursiveGlob_IgnoresUnderscoreStrays_UnlessThePatternStartsWithOne()
        {
            var dir = TempDir();
            try
            {
                var x = Csv(dir, "x.csv", "id\n1\n");
                var underscore = Csv(dir, "_x.csv", "id\n2\n");

                using (var star = DataBall.Open(new[] { Path.Combine(dir, "*.csv") }))
                {
                    Assert.Equal(new[] { x }, star.Sources.Select(s => s.Path).ToArray());
                    Assert.Equal(new[] { underscore }, star.OpenReport!.Ignored.ToArray());
                }

                using var underscorePattern = DataBall.Open(new[] { Path.Combine(dir, "_*.csv") });
                Assert.Equal(new[] { underscore }, underscorePattern.Sources.Select(s => s.Path).ToArray());
                Assert.Empty(underscorePattern.OpenReport!.Ignored);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_RecursiveGlob_StrayDirectoriesStayIgnored_EvenForAnUnderscorePattern()
        {
            var dir = TempDir();
            try
            {
                var part = Csv(dir, "_part1.csv", "id\n1\n");
                var underTemporary = Csv(dir, Path.Combine("_temporary", "0", "_y.csv"), "id\n9\n");

                using var db = DataBall.Open(new[] { Path.Combine(dir, "_*.csv") }, new OpenOptions { Recursive = true });
                Assert.Equal(new[] { part }, db.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(new[] { underTemporary }, db.OpenReport!.Ignored.ToArray());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_NoFileToOpen_StrayFilesHaveTheirOwnWording()
        {
            var dir = TempDir();
            try
            {
                var underscore = Csv(dir, "_x.csv", "id\n1\n");

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { Path.Combine(dir, "*.csv") }));
                Assert.Contains("stray", ex.Message);
                Assert.Contains(underscore, ex.Message);
                Assert.DoesNotContain("not recognized as any format", ex.Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_AllBadWithOneFileLeft_NamesEveryFileAndTheCause()
        {
            var dir = TempDir();
            try
            {
                var notes = Csv(dir, "notes.txt", "not data");
                var bad = BadParquet(dir, "bad.parquet");

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { notes, bad }, Skip));
                Assert.Contains(notes, ex.Message);
                Assert.Contains(bad, ex.Message);
                Assert.Contains("No magic bytes", ex.Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_EnvironmentFailure_AbortsEvenUnderSkip()
        {
            var dir = TempDir();
            DataBall.ClearHandlers();
            try
            {
                DataBall.RegisterHandler(new EnvironmentFailingHandler());
                var a = Csv(dir, "a.csv", "id\n1\n");
                var memory = Csv(dir, "c_oom.env", "anything");

                // Running out of memory is not a bad file: Skip does not hide it.
                var memoryEx = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, memory }, Skip));
                Assert.Contains(memory, memoryEx.Message);
                Assert.IsType<OutOfMemoryException>(memoryEx.InnerException);
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        /// <summary>Claims <c>.env</c> files and fails with an I/O error, or with an out-of-memory error for a name containing <c>_oom</c>.</summary>
        private sealed class EnvironmentFailingHandler : IFormatHandler
        {
            public bool CanHandle(string path, Stream sniff) => path.EndsWith(".env", StringComparison.OrdinalIgnoreCase);

            public string Name => "EnvironmentFailingHandler";

            public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(
                string path,
                Config schema,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _ = schema;
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                if (path.Contains("_oom", StringComparison.Ordinal))
                    throw new OutOfMemoryException();
                if (path.Length >= 0)
                    throw new IOException("disk gone");
                yield break;
            }
        }

        [Fact]
        public void Open_UnreadableFile_Skip_OtherFilesLand()
        {
            var dir = TempDir();
            try
            {
                var a = Csv(dir, "a.csv", "id\n1\n");
                var locked = Csv(dir, "locked.csv", "id\n2\n");
                using var hold = Lock(locked);
                if (hold is null)
                    return;

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, locked }));
                Assert.Contains(locked, ex.Message);

                using var db = DataBall.Open(new[] { a, locked }, Skip);
                Assert.Equal(new[] { a }, db.Sources.Select(s => s.Path).ToArray());
                var skipped = Assert.Single(db.OpenReport!.Skipped);
                Assert.Equal(locked, skipped.Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_AmbiguousFile_IsSkippedUnderSkip_WhetherNamedOrExpanded()
        {
            var dir = TempDir();
            DataBall.ClearHandlers();
            try
            {
                // Two handlers that tie on score and priority make the file ambiguous.
                DataBall.RegisterHandler(new TieHandler());
                DataBall.RegisterHandler(new TieHandler());
                var a = Csv(dir, "a.csv", "id\n1\n");
                var tie = Csv(dir, "x.tie", "anything");

                Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, tie }));
                Assert.Throws<DataBallException>(() => DataBall.Open(new[] { dir }));

                using (var named = DataBall.Open(new[] { a, tie }, Skip))
                {
                    Assert.Equal(new[] { a }, named.Sources.Select(s => s.Path).ToArray());
                    var skipped = Assert.Single(named.OpenReport!.Skipped);
                    Assert.Equal(tie, skipped.Path);
                    Assert.Contains("Ambiguous", skipped.Message);
                }

                using var expanded = DataBall.Open(new[] { dir }, Skip);
                Assert.Equal(new[] { a }, expanded.Sources.Select(s => s.Path).ToArray());
                var expandedSkipped = Assert.Single(expanded.OpenReport!.Skipped);
                Assert.Equal(tie, expandedSkipped.Path);
                Assert.Empty(expanded.OpenReport.Ignored);
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_SkippedFilesHandlerOverlay_StillShapesTheConfig()
        {
            var dir = TempDir();
            DataBall.ClearHandlers();
            try
            {
                // The overlay comes from the handler that matched the file, not from its rows, so a skipped file keeps it.
                DataBall.RegisterHandler(new OverlayExplodingHandler());
                var a = Csv(dir, "a.csv", "id\n1\n");
                var bad = Csv(dir, "z.over", "anything");

                using var db = DataBall.Open(new[] { a, bad }, Skip);
                Assert.Equal(new[] { a }, db.Sources.Select(s => s.Path).ToArray());
                Assert.Single(db.OpenReport!.Skipped);
                Assert.Contains("Site", db.Schema.MetadataFields);
            }
            finally
            {
                DataBall.ClearHandlers();
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_CastFailureAgainstATypeLearnedFromAnEarlierFile_IsAConflict_UnderBothPolicies()
        {
            var dir = TempDir();
            try
            {
                // a.csv teaches n a number from its header; b.csv's text cannot be that number, and cannot merge with it.
                var a = Csv(dir, "a.csv", "n(dB)\n1.5\n");
                var b = Csv(dir, "b.csv", "n\nabc\n");

                foreach (var options in new[] { new OpenOptions(), Skip })
                {
                    var ex = Assert.ThrowsAny<DataBallException>(() => DataBall.Open(new[] { a, b }, options));
                    Assert.Contains("Cannot merge", ex.Message);
                    Assert.Contains("column 'n'", ex.Message);
                    Assert.Contains(a, ex.Message);
                    Assert.Contains(b, ex.Message);
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_CastFailureAgainstADeclaredType_IsABadFile()
        {
            var dir = TempDir();
            try
            {
                var declared = Path.Combine(dir, "declared.json");
                File.WriteAllText(declared, """{ "columns": { "n": "double" } }""");
                var a = Csv(dir, "a.csv", "n\n1.5\n");
                var b = Csv(dir, "b.csv", "n\nabc\n");

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, b }, new OpenOptions { SchemaPath = declared }));
                Assert.Contains(b, ex.Message);
                Assert.DoesNotContain("Cannot merge", ex.Message);

                using var db = DataBall.Open(new[] { a, b }, new OpenOptions { SchemaPath = declared, OnBadFile = BadFilePolicy.Skip });
                Assert.Equal(new[] { a }, db.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(b, Assert.Single(db.OpenReport!.Skipped).Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_CastFailureAgainstTheFilesOwnHeaderType_IsABadFile()
        {
            var dir = TempDir();
            try
            {
                // b.csv's own header types n as a number too: its text is b's own bad value, not a conflict with a.csv.
                var a = Csv(dir, "a.csv", "n(dB)\n1.5\n");
                var b = Csv(dir, "b.csv", "n(dB)\nabc\n");

                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(new[] { a, b }));
                Assert.Contains(b, ex.Message);
                Assert.DoesNotContain("Cannot merge", ex.Message);

                using var db = DataBall.Open(new[] { a, b }, Skip);
                Assert.Equal(new[] { a }, db.Sources.Select(s => s.Path).ToArray());
                Assert.Equal(b, Assert.Single(db.OpenReport!.Skipped).Path);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>Claims <c>.tie</c> files with the same score and priority as every other <c>TieHandler</c>: an ambiguous match.</summary>
        private sealed class TieHandler : IFormatHandler
        {
            public bool CanHandle(string path, Stream sniff) => path.EndsWith(".tie", StringComparison.OrdinalIgnoreCase);

            public string Name => "TieHandler";

            public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(
                string path,
                Config schema,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _ = (path, schema);
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }
        }

        /// <summary>Claims <c>.over</c> files, overlays a metadata field <c>Site</c>, and throws from the first read.</summary>
        private sealed class OverlayExplodingHandler : IFormatHandler
        {
            public bool CanHandle(string path, Stream sniff) => path.EndsWith(".over", StringComparison.OrdinalIgnoreCase);

            public string Name => "OverlayExplodingHandler";

            public Config? Overlay => new Config { MetadataFieldsAdd = { "Site" } };

            public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(
                string path,
                Config schema,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _ = schema;
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                if (path.Length >= 0)
                    throw new InvalidOperationException("overlay handler exploded");
                yield break;
            }
        }

        /// <summary>Makes <paramref name="path"/> unreadable for the test: an exclusive lock on Windows, no read permission elsewhere.
        /// Returns null (and the test does nothing) when the file stays readable, as it does for a root user on Unix.</summary>
        private static IDisposable? Lock(string path)
        {
            if (OperatingSystem.IsWindows())
                return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            File.SetUnixFileMode(path, UnixFileMode.None);
            try
            {
                File.OpenRead(path).Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                return new Restore(path);
            }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return null;
        }

        private sealed class Restore(string path) : IDisposable
        {
            public void Dispose()
            {
                if (OperatingSystem.IsWindows())
                    return;
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        /// <summary>
        /// Claims <c>.boom</c> files. It yields one full staging batch, so its staging table exists, then throws: a skip must drop that table.
        /// </summary>
        private sealed class ExplodingHandler : IFormatHandler
        {
            public bool CanHandle(string path, Stream sniff) => path.EndsWith(".boom", StringComparison.OrdinalIgnoreCase);

            public string Name => "ExplodingHandler";

            public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(
                string path,
                Config schema,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _ = schema;
                await Task.Yield();
                for (var i = 0; i <= DataBall.HandlerBatchSize; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new Dictionary<string, object?> { ["Name"] = "row " + i };
                }
                throw new InvalidOperationException("handler exploded");
            }
        }

        private static void AddEntry(ZipArchive archive, string name, string text)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(text);
        }

        private static string Parquet(string dir, string name, string select)
        {
            var path = Path.Combine(dir, name);
            using var writer = new DataBall();
            writer.Query($"COPY ({select}) TO {DuckDbStore.QuoteString(path)} (FORMAT PARQUET)");
            return path;
        }

        /// <summary>A file named as Parquet whose bytes are not Parquet: it is found, then fails to stage.</summary>
        private static string BadParquet(string dir, string name) => Csv(dir, name, "not a parquet file");

        private static string Csv(string dir, string name, string text)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        private static long Rows(DataBall db) => Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data")[0]["n"]);

        private static string[] DataColumns(DataBall db) => db.Store.GetColumns().Select(c => c.Name).ToArray();

        private static string[] SourceColumns(DataBall db) => db.Store.GetColumnsOf("source").Select(c => c.Name).ToArray();

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-badfile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
