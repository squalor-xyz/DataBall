// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using squalor.DataBall.Export;

namespace squalor.DataBall.Tests
{
    public class ReadTests
    {
        [Fact]
        public void ReadColumns_TypedArrays_MatchQuery()
        {
            using var db = TypedSession();
            var columns = db.ReadColumns(new SessionFilter());
            var expected = db.Query("SELECT * FROM data");
            Assert.Equal((long)expected.Count, (long)columns.RowCount);
            Assert.Equal(8, columns.Columns.Count);
            CheckColumn<long>(columns["Id"], expected);
            CheckColumn<double>(columns["Value"], expected);
            CheckColumn<bool>(columns["Flag"], expected);
            CheckColumn<DateTime>(columns["Time"], expected);
            CheckColumn<string>(columns["Label"], expected);
            CheckColumn<DateTime>(columns["Day"], expected);
            CheckColumn<int>(columns["Integer"], expected);
            CheckColumn<float>(columns["Float"], expected);
            Assert.Equal(new long[] { 1, 2, 3 }, ((ColumnData<long>)columns["Id"]).Values);
            Assert.Same((object)columns["Id"], (object)columns["ID"]);
        }

        [Fact]
        public void ReadColumns_NullMask()
        {
            using var db = new DataBall();
            db.AddColumn("Text", new string?[] { "a", null, "c" });
            db.AddColumn("Number", new long[] { 1, 2, 3 });
            db.Store.Execute("UPDATE data SET Number = NULL WHERE Number = 1");
            db.AddColumn("Complete", new[] { 1.0, 2.0, 3.0 });
            var columns = db.ReadColumns(new SessionFilter());
            Assert.Equal(new[] { false, true, false }, columns["Text"].Nulls);
            Assert.Equal(new[] { true, false, false }, columns["Number"].Nulls);
            Assert.Equal(new string?[] { "a", null, "c" }, (string[])columns["Text"].Values);
            Assert.Equal(new long[] { 0, 2, 3 }, (long[])columns["Number"].Values);
            Assert.Null((object?)columns["Complete"].Nulls);
        }

        [Fact]
        public void ReadColumns_ProjectionAndPredicates()
        {
            using var db = TypedSession();
            var columns = db.ReadColumns(new SessionFilter
            {
                Columns = ["label", "ID"],
                Predicates = [new ColumnPredicate { Column = "Value", Op = PredicateOp.Ge, Value = 2.0 }]
            });
            Assert.Equal(2L, (long)columns.RowCount);
            Assert.Equal("Label", (string)columns.Columns[0].Name);
            Assert.Equal("Id", (string)columns.Columns[1].Name);
            Assert.Equal(2, (int)columns.Columns.Count);
            Assert.Equal(new[] { "b", "c" }, (string[])columns["Label"].Values);
            Assert.Equal(new long[] { 2, 3 }, (long[])columns["Id"].Values);
            var empty = db.ReadColumns(new SessionFilter
            {
                Columns = ["Id"],
                Predicates = [new ColumnPredicate { Column = "Id", Op = PredicateOp.Ge, Value = 100 }]
            });
            Assert.Equal(0L, (long)empty.RowCount);
            Assert.Empty((long[])empty["Id"].Values);
            Assert.Null((object?)empty["Id"].Nulls);
        }

        [Fact]
        public void ReadColumns_LayoutSession_ReadsThroughView()
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures");
            using var db = DataBall.Open(Path.Combine(fixture, "semiconductor-sweep.csv"),
                Path.Combine(fixture, "semiconductor-sweep.tables.json"));
            Assert.Equal("VIEW", db.Query("SELECT table_type FROM information_schema.tables WHERE table_name='data'")[0]["table_type"]);
            var filter = new SessionFilter { Columns = ["EVM", "Temp"] };
            var columns = db.ReadColumns(filter);
            var expected = db.Filter(filter);
            Assert.Equal(81L, (long)columns.RowCount);
            CheckColumn<double>(columns["EVM"], expected);
            CheckColumn<double>(columns["Temp"], expected);
        }

        [Fact]
        public void ReadColumns_UnknownColumn_Throws()
        {
            using var db = TypedSession();
            var ex = Assert.Throws<DataBallException>(() => db.ReadColumns(new SessionFilter { Columns = ["Missing"] }));
            Assert.Contains("Missing", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ReadRowsAsync_RowsEqualFilter()
        {
            using var db = TypedSession();
            db.Store.Execute("CREATE TABLE sorted AS SELECT * FROM data ORDER BY Id; DROP TABLE data; ALTER TABLE sorted RENAME TO data");
            var filter = new SessionFilter();
            var expected = db.Filter(filter);
            Assert.Equal(new long[] { 1, 2, 3 }, expected.Select(r => (long)r["Id"]!).ToArray());
            var actual = new List<IReadOnlyDictionary<string, object?>>();
            await foreach (var row in db.ReadRowsAsync(filter))
                actual.Add(row);
            Assert.Equal(expected.Count, actual.Count);
            Assert.NotSame(actual[0], actual[1]);
            for (var i = 0; i < expected.Count; i++)
                Assert.Equal(expected[i].ToArray(), actual[i].ToArray());
            var projected = new SessionFilter
            {
                Columns = ["Label", "Id"],
                Predicates = [new ColumnPredicate { Column = "Id", Op = PredicateOp.Eq, Value = 2 }]
            };
            actual.Clear();
            await foreach (var row in db.ReadRowsAsync(projected))
                actual.Add(row);
            Assert.Equal(Assert.Single(db.Filter(projected)).ToArray(), Assert.Single(actual).ToArray());
        }

        [Fact]
        public async Task ReadRowsAsync_EarlyBreak_ReleasesReader()
        {
            using var db = new DataBall();
            db.Store.Execute("CREATE TABLE data AS SELECT range AS Id FROM range(1000000)");
            var seen = 0;
            await foreach (var row in db.ReadRowsAsync(new SessionFilter()))
            {
                Assert.Equal(0L, row["Id"]);
                seen++;
                break;
            }
            Assert.Equal(1, seen);
            db.AddRow(new Dictionary<string, object?> { ["Id"] = 1000000L });
            Assert.Equal(1000001L, db.Count(new SessionFilter()));
        }

        [Fact]
        public async Task ReadRowsAsync_WriteDuringEnumeration_Throws()
        {
            using var db = TypedSession();
            await using var first = db.ReadRowsAsync(new SessionFilter()).GetAsyncEnumerator();
            Assert.True(await first.MoveNextAsync());
            var ex = Assert.Throws<DataBallException>(() => db.AddRow(new Dictionary<string, object?> { ["Id"] = 4L }));
            Assert.Contains("stream", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<DataBallException>(() => db.SetMetadata("test", "value"));
            await first.DisposeAsync();
            db.SetMetadata("test", "value");
        }

        [Fact]
        public async Task ReadRowsAsync_Cancellation_ThrowsOperationCanceled()
        {
            using var db = TypedSession();
            using var cancellation = new CancellationTokenSource();
            await using var rows = db.ReadRowsAsync(new SessionFilter(), cancellation.Token).GetAsyncEnumerator();
            Assert.True(await rows.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rows.MoveNextAsync());
            db.SetMetadata("after", "cancelled");
        }

        [Theory]
        [InlineData("Query")]
        [InlineData("Count")]
        [InlineData("Filter")]
        [InlineData("ReadColumns")]
        [InlineData("ExportAsync")]
        [InlineData("SaveAsync")]
        [InlineData("Delete")]
        public async Task ReadRowsAsync_ReadDuringEnumeration_Throws(string operation)
        {
            using var db = new DataBall();
            db.Store.Execute("CREATE TABLE data AS SELECT range AS Id FROM range(1000000)");
            var path = Path.Combine(Path.GetTempPath(), "databall-stream-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var rows = db.ReadRowsAsync(new SessionFilter()).GetAsyncEnumerator())
                {
                    if (!await rows.MoveNextAsync())
                        throw new InvalidOperationException("Fixture did not yield a row");
                    var ex = await Assert.ThrowsAsync<DataBallException>(async () =>
                    {
                        switch (operation)
                        {
                            case "Query": db.Query("SELECT 1"); break;
                            case "Count": db.Count(new SessionFilter()); break;
                            case "Filter": db.Filter(new SessionFilter()); break;
                            case "ReadColumns": db.ReadColumns(new SessionFilter()); break;
                            case "ExportAsync": await db.ExportAsync(path + ".csv", ExportType.Csv); break;
                            case "SaveAsync": await db.SaveAsync(path + ".ball"); break;
                            case "Delete": db.Query("DELETE FROM data WHERE Id = -1"); break;
                        }
                    });
                    Assert.Equal("A row stream is open; finish or dispose it first", ex.Message);
                }
                long count = 0;
                await foreach (var row in db.ReadRowsAsync(new SessionFilter()))
                    count++;
                Assert.Equal(1000000L, count);
            }
            finally
            {
                File.Delete(path + ".csv");
                File.Delete(path + ".ball");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReadRowsAsync_RejectedMetadata_LeavesStateUnchanged(bool directStore)
        {
            using var db = TypedSession();
            await using var rows = db.ReadRowsAsync(new SessionFilter()).GetAsyncEnumerator();
            if (!await rows.MoveNextAsync())
                throw new InvalidOperationException("Fixture did not yield a row");
            Assert.Throws<DataBallException>(() =>
            {
                if (directStore) db.Store.SetMetadata("rejected", "value");
                else db.SetMetadata("rejected", "value");
            });
            Assert.False(db.Metadata.ContainsKey("rejected"));
        }

        [Fact]
        public async Task ReadRowsAsync_RejectedInitializeRow_LeavesStateUnchanged()
        {
            using var db = TypedSession();
            var path = Path.Combine(Path.GetTempPath(), "databall-rejected-row-" + Guid.NewGuid().ToString("N") + ".ball");
            try
            {
                await using (var rows = db.ReadRowsAsync(new SessionFilter()).GetAsyncEnumerator())
                {
                    if (!await rows.MoveNextAsync())
                        throw new InvalidOperationException("Fixture did not yield a row");
                    Assert.Throws<DataBallException>(() => db.InitializeRow());
                }
                await db.SaveAsync(path);
                using var saved = DataBall.Open(path);
                Assert.Equal(3L, saved.Count(new SessionFilter()));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task ReadRowsAsync_DisposeSessionMidStream_Ends()
        {
            using var db = new DataBall();
            db.Store.Execute("CREATE TABLE data AS SELECT range AS Id FROM range(1000000)");
            await using var rows = db.ReadRowsAsync(new SessionFilter()).GetAsyncEnumerator();
            if (!await rows.MoveNextAsync())
                throw new InvalidOperationException("Fixture did not yield a row");
            db.Dispose();
            await Assert.ThrowsAsync<DataBallException>(async () => await rows.MoveNextAsync());
        }

        [Theory]
        [InlineData("SMALLINT", "12", typeof(int))]
        [InlineData("TINYINT", "12", typeof(int))]
        [InlineData("UTINYINT", "12", typeof(int))]
        [InlineData("USMALLINT", "12", typeof(int))]
        [InlineData("UINTEGER", "4294967295", typeof(long))]
        [InlineData("DECIMAL(5,2)", "12.34", typeof(double))]
        [InlineData("UBIGINT", "18446744073709551615", typeof(double))]
        [InlineData("HUGEINT", "18446744073709551616", typeof(double))]
        public void ReadColumns_ReadSideTypes_RoundTrip(string sqlType, string literal, Type elementType)
        {
            using var db = new DataBall();
            db.Store.Execute($"CREATE TABLE data AS SELECT CAST({literal} AS {sqlType}) AS X");
            var value = db.Query("SELECT X FROM data")[0]["X"]!;
            var expected = value is System.Numerics.BigInteger big ? (object)(double)big
                : Convert.ChangeType(value, elementType, CultureInfo.InvariantCulture);
            var column = db.ReadColumns(new SessionFilter())["X"];
            Assert.Equal(elementType, column.ElementType);
            Assert.Equal(expected, column.Values.GetValue(0));
        }

        [Theory]
        [InlineData("TIME", "'12:34:56'", "12:34:56")]
        [InlineData("UUID", "'550e8400-e29b-41d4-a716-446655440000'", "550e8400-e29b-41d4-a716-446655440000")]
        [InlineData("BLOB", "'abc'", "abc")]
        [InlineData("INTEGER[]", "[1,2]", "[1, 2]")]
        [InlineData("INTERVAL", "'2 days'", "2 days")]
        [InlineData("UHUGEINT", "18446744073709551616", "18446744073709551616")]
        [InlineData("STRUCT(a INTEGER)", "{'a': 1}", "{'a': 1}")]
        [InlineData("MAP(VARCHAR, INTEGER)", "MAP(['a'], [1])", "{a=1}")]
        public void ReadColumns_StringFallback_DuckDbText(string sqlType, string literal, string expected)
        {
            using var db = new DataBall();
            db.Store.Execute($"CREATE TABLE data AS SELECT CAST({literal} AS {sqlType}) AS X");
            var column = (ColumnData<string>)db.ReadColumns(new SessionFilter())["X"];
            Assert.Equal(expected, column.Values[0]);
        }

        [Fact]
        public void ReadColumns_AboveArrayLimit_NamesLimit()
        {
            using var db = new DataBall();
            db.Store.Execute("CREATE VIEW data AS SELECT range AS Id FROM range(2147483648)");
            var ex = Assert.Throws<DataBallException>(() => db.ReadColumns(new SessionFilter()));
            Assert.Contains("int.MaxValue", ex.Message, StringComparison.Ordinal);
        }

        private static DataBall TypedSession()
        {
            var db = new DataBall();
            db.AddColumn("Id", new long[] { 1, 2, 3 });
            db.AddColumn("Value", new[] { 1.5, 2.5, 3.5 });
            db.AddColumn("Flag", new[] { true, false, true });
            db.AddColumn("Time", new[] { new DateTime(2024, 1, 1), new DateTime(2024, 2, 1), new DateTime(2024, 3, 1) });
            db.AddColumn("Label", new[] { "a", "b", "c" });
            db.Store.Execute("ALTER TABLE data ADD COLUMN Day DATE; UPDATE data SET Day = CAST(Time AS DATE); ALTER TABLE data ADD COLUMN Integer INTEGER; UPDATE data SET Integer = CAST(Id AS INTEGER); ALTER TABLE data ADD COLUMN Float FLOAT; UPDATE data SET Float = CAST(Value AS FLOAT)");
            return db;
        }

        private static void CheckColumn<T>(ColumnData column, IReadOnlyList<Dictionary<string, object?>> expected)
        {
            Assert.Equal(typeof(T), (Type)column.ElementType);
            var values = Assert.IsType<T[]>((object)column.Values);
            Assert.Equal(expected.Select(row => (T)row[(string)column.Name]!).ToArray(), values);
        }

    }
}
