// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using squalor.DataBall.Handlers;
using Xunit;

namespace squalor.DataBall.Tests
{
    [Collection("DataBall.Handlers")]
    public class HandlerTests : IDisposable
    {
        public HandlerTests() => DataBall.ClearHandlers();

        public void Dispose() => DataBall.ClearHandlers();

        [Fact]
        public void CustomCsv_CanHandle_UnitHeaderCsv()
        {
            var csv = "EVM(dB),I_Total(A)\n1,0.1\n";
            using var sniff = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            Assert.True(new CustomCsvHandler().CanHandle("meas.csv", sniff));
        }

        [Fact]
        public void CustomCsv_DoesNotHandle_PlainNameAgeCsv()
        {
            var csv = "Name,Age\nAlice,30\n";
            using var sniff = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            Assert.False(new CustomCsvHandler().CanHandle("people.csv", sniff));
        }

        [Fact]
        public async Task CustomCsv_Parse_ThrowsNotSupported()
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (var _ in new CustomCsvHandler().Parse(Fixture(), Config.CreateDefaults()))
                {
                }
            });
        }

        [Fact]
        public void Open_WithCustomCsvRegistered_SameRowCountAsGeneric()
        {
            DataBall.RegisterHandler(new CustomCsvHandler());
            using var db = DataBall.Open(Fixture());
            Assert.Equal(81, db.Query("SELECT * FROM data").Count);
        }

        [Fact]
        public void Open_WithHandler_AppliesOverlaySchema()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-h15-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var csv = Path.Combine(dir, "press.csv");
                File.WriteAllText(csv, "Wafer,Press(Torr),EVM(dB)\nW1,1,2.5\n");
                var overlay = Path.Combine(dir, "overlay.json");
                File.WriteAllText(overlay, """{"units":{"Torr":{"type":"double"}},"metadataFieldsAdd":["Wafer"]}""");

                DataBall.RegisterHandler(new CustomCsvHandler());
                using var withHandler = DataBall.Open(csv, overlay);
                DataBall.ClearHandlers();
                using var withoutHandler = DataBall.Open(csv, overlay);

                AssertSameOverlayShape(withHandler, withoutHandler);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_WithHandler_ExtractsConfiguredMetadata()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-h15-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var csv = Path.Combine(dir, "lot.csv");
                File.WriteAllText(csv, "Lot,EVM(dB)\nL1,1.5\nL1,2.0\n");
                DataBall.RegisterHandler(new CustomCsvHandler());
                using var db = DataBall.Open(csv);
                Assert.Equal("L1", db.Metadata["Lot"]?.ToString());
                var cols = db.Query("SELECT * FROM data LIMIT 1")[0];
                Assert.False(cols.ContainsKey("Lot"));
                Assert.True(cols.ContainsKey("EVM"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Stdf_CanHandle_StdfExtension_ParseThrowsUntilFixture()
        {
            var handler = new StdfHandler();
            using var sniff = new MemoryStream();
            Assert.True(handler.CanHandle("lot.stdf", sniff));
            Assert.True(handler.CanHandle("lot.std", sniff));
            Assert.False(handler.CanHandle("lot.csv", sniff));
            var ex = Assert.Throws<DataBallException>(() => Drain(handler, "lot.stdf"));
            Assert.Contains("STDF", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Touchstone_CanHandle_S2p_ParseThrowsUntilFixture()
        {
            var handler = new TouchstoneHandler();
            using var sniff = new MemoryStream();
            Assert.True(handler.CanHandle("dut.s2p", sniff));
            Assert.True(handler.CanHandle("dut.s1p", sniff));
            Assert.False(handler.CanHandle("dut.csv", sniff));
            var ex = Assert.Throws<DataBallException>(() => Drain(handler, "dut.s2p"));
            Assert.Contains("Touchstone", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Production_CanHandle_Prd_ParseThrowsUntilDialect()
        {
            var handler = new ProductionHandler();
            using var sniff = new MemoryStream();
            Assert.True(handler.CanHandle("wafer.prd", sniff));
            var ex = Assert.Throws<DataBallException>(() => Drain(handler, "wafer.prd"));
            Assert.Contains("Production", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Open_Handler_WritesInBatches()
        {
            var dir = HandlerDirectory();
            try
            {
                var batches = new List<int>();
                DataBall.AfterHandlerBatchForTests = (db, count) =>
                {
                    batches.Add(count);
                    var expected = batches.Sum();
                    var stored = db.Query("SELECT COUNT(*) AS n, MIN(Id) AS lo, MAX(Id) AS hi, COUNT(DISTINCT Id) AS unique_ids FROM data")[0];
                    Assert.Equal((long)expected, stored["n"]);
                    Assert.Equal(0L, stored["lo"]);
                    Assert.Equal((long)expected - 1, stored["hi"]);
                    Assert.Equal((long)expected, stored["unique_ids"]);
                };
                DataBall.RegisterHandler(new BatchHandler(false));
                using var db = DataBall.Open(Path.Combine(dir, "input.test"));
                Assert.Equal(new[] { 10000, 10000, 5000 }, batches);
                Assert.Equal(25000L, db.Count(new SessionFilter()));
            }
            finally
            {
                DataBall.AfterHandlerBatchForTests = null;
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Open_Handler_FailureAfterFirstBatch_Throws_NoLeak()
        {
            var dir = HandlerDirectory();
            string? storePath = null;
            try
            {
                var batches = new List<int>();
                DataBall.AfterHandlerBatchForTests = (db, count) =>
                {
                    storePath = db.StorePath;
                    batches.Add(count);
                };
                DataBall.RegisterHandler(new BatchHandler(true));
                var ex = Assert.Throws<DataBallException>(() => DataBall.Open(Path.Combine(dir, "input.test"),
                    engine: new EngineOptions { Store = StoreMode.File, TempDirectory = dir }));
                Assert.IsType<IOException>(ex.InnerException);
                Assert.Equal(new[] { 10000 }, batches);
                Assert.NotNull(storePath);
                Assert.False(File.Exists(storePath));
                using var reopened = new DataBall(databasePath: storePath);
                reopened.AddColumn("Id", new long[] { 1 });
                Assert.Equal(1L, reopened.Count(new SessionFilter()));
            }
            finally
            {
                DataBall.AfterHandlerBatchForTests = null;
                Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Open_Handler_FirstNonNullAfterBatch_KeepsType(bool layout)
        {
            var dir = HandlerDirectory();
            try
            {
                string? schema = null;
                if (layout)
                {
                    schema = Path.Combine(dir, "layout.json");
                    File.WriteAllText(schema, """{"tables":{"rows":{"kind":"rows","columns":["Id","X"]}}}""");
                }
                DataBall.RegisterHandler(new LateValueHandler());
                using var db = DataBall.Open(Path.Combine(dir, "input.test"), schema);
                Assert.Equal("DOUBLE", db.Query("DESCRIBE data").Single(row => (string)row["column_name"]! == "X")["column_type"]);
                Assert.Equal(1.5, Assert.IsType<double>(db.Query("SELECT X FROM data WHERE X IS NOT NULL")[0]["X"]));
                var column = db.ReadColumns(new SessionFilter { Columns = ["X"] })["X"];
                Assert.Equal(typeof(double), column.ElementType);
                Assert.Equal(1.5, ((ColumnData<double>)column).Values[10000]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AddRows_FirstNonNull_WidensInferredType(bool layout)
        {
            var dir = HandlerDirectory();
            try
            {
                string? schema = null;
                if (layout)
                {
                    schema = Path.Combine(dir, "layout.json");
                    File.WriteAllText(schema, """{"tables":{"dimension":{"kind":"dimension","columns":["X"]},"rows":{"kind":"rows","columns":["Id"]}}}""");
                }
                using var db = new DataBall(schema);
                db.AddRows([new Dictionary<string, object?> { ["Id"] = 0L, ["X"] = null }]);
                db.AddRows([new Dictionary<string, object?> { ["Id"] = 1L, ["X"] = 1.5 }]);
                Assert.Equal("DOUBLE", db.Query("DESCRIBE data").Single(row => (string)row["column_name"]! == "X")["column_type"]);
                Assert.Equal(1.5, Assert.IsType<double>(db.Query("SELECT X FROM data WHERE Id = 1")[0]["X"]));
                Assert.Null(db.Query("SELECT X FROM data WHERE Id = 0")[0]["X"]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Open_Handler_Layout_SplitsOnce()
        {
            var dir = HandlerDirectory();
            try
            {
                var schema = Path.Combine(dir, "layout.json");
                File.WriteAllText(schema, """{"tables":{"dimension":{"kind":"dimension","columns":["Key"]},"rows":{"kind":"rows","columns":["Id"]}}}""");
                var batchKinds = new List<string>();
                DataBall.AfterHandlerBatchForTests = (db, _) => batchKinds.Add((string)db.Query("SELECT table_type FROM information_schema.tables WHERE table_name='data'")[0]["table_type"]!);
                DataBall.RegisterHandler(new LayoutHandler());
                using var actual = DataBall.Open(Path.Combine(dir, "input.test"), schema);
                using var expected = new DataBall(schema);
                expected.AddRows(Enumerable.Range(0, 10001).Select(LayoutRow).ToList());
                Assert.Equal(new[] { "BASE TABLE", "BASE TABLE" }, batchKinds);
                Assert.Equal(expected.Query("SELECT * FROM data ORDER BY Id").Select(row => row.ToArray()), actual.Query("SELECT * FROM data ORDER BY Id").Select(row => row.ToArray()));
                Assert.Equal(expected.Query("SELECT * FROM dimension ORDER BY Key").Select(row => row.ToArray()), actual.Query("SELECT * FROM dimension ORDER BY Key").Select(row => row.ToArray()));
            }
            finally
            {
                DataBall.AfterHandlerBatchForTests = null;
                Directory.Delete(dir, true);
            }
        }

        private static IReadOnlyDictionary<string, object?> LayoutRow(int i)
            => new Dictionary<string, object?> { ["Id"] = (long)i, ["Key"] = (long)i };

        private sealed class LayoutHandler : IFormatHandler
        {
            public bool CanHandle(string path, Stream sniff) => path.EndsWith(".test", StringComparison.Ordinal);
            public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(string path, Config schema,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                for (var i = 0; i < 10001; i++)
                    yield return LayoutRow(i);
            }
        }

        private sealed class LateValueHandler : IFormatHandler
        {
            public bool CanHandle(string path, Stream sniff) => path.EndsWith(".test", StringComparison.Ordinal);
            public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(string path, Config schema,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                for (var i = 0; i < 10001; i++)
                    yield return new Dictionary<string, object?> { ["Id"] = (long)i, ["X"] = i == 10000 ? 1.5 : null };
            }
        }

        private static string HandlerDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-batches-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "input.test"), "handler input");
            return dir;
        }

        private sealed class BatchHandler(bool fail) : IFormatHandler
        {
            public bool CanHandle(string path, Stream sniff) => path.EndsWith(".test", StringComparison.Ordinal);

            public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Parse(string path, Config schema,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                for (var i = 0; i < 25000; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (fail && i == 10000)
                        throw new IOException("Handler failed after its first batch");
                    yield return new Dictionary<string, object?> { ["Id"] = (long)i };
                }
            }
        }

        private static void AssertSameOverlayShape(DataBall withHandler, DataBall withoutHandler)
        {
            var withRow = Assert.Single(withHandler.Query("SELECT * FROM data"));
            var withoutRow = Assert.Single(withoutHandler.Query("SELECT * FROM data"));
            Assert.Equal(withoutRow.Keys.OrderBy(k => k, StringComparer.Ordinal), withRow.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.False(withRow.ContainsKey("Wafer"));
            Assert.Equal("W1", withHandler.Metadata["Wafer"]?.ToString());
            Assert.Equal(withoutHandler.Metadata["Wafer"]?.ToString(), withHandler.Metadata["Wafer"]?.ToString());
            Assert.IsType<double>(withRow["Press"]);
            Assert.Equal(withoutRow["Press"]?.GetType(), withRow["Press"]?.GetType());
            Assert.IsType<double>(withRow["EVM"]);
            Assert.Equal(withoutRow["EVM"]?.GetType(), withRow["EVM"]?.GetType());
        }

        private static void Drain(IFormatHandler handler, string path)
        {
            handler.Parse(path, Config.CreateDefaults()).ToBlockingEnumerable().ToList();
        }

        private static string Fixture()
            => Path.Combine(AppContext.BaseDirectory, "fixtures", "semiconductor-sweep.csv");
    }
}
