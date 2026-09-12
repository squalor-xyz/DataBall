using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using squalor.DataBall.Export;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class SessionFilterTests
    {
        [Fact]
        public void Filter_Equals_StringColumn()
        {
            using var db = People();
            var rows = db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Name", Op = PredicateOp.Eq, Value = "Alice" }
                ]
            });
            var row = Assert.Single(rows);
            Assert.Equal("Alice", row["Name"]);
        }

        [Fact]
        public void Filter_Range_DoubleColumn()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "pout.csv");
                File.WriteAllText(csv, "Pout,EVM\n0,-50\n5,-40\n10,-30\n");
                using var db = DataBall.Open(csv);
                var rows = db.Filter(new SessionFilter
                {
                    Predicates =
                    [
                        new ColumnPredicate { Column = "Pout", Op = PredicateOp.Range, Value = 0, ValueTo = 5 }
                    ]
                });
                var pouts = rows.Select(r => Convert.ToDouble(r["Pout"])).OrderBy(x => x).ToArray();
                Assert.Equal(new[] { 0.0, 5.0 }, pouts);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Filter_In_List()
        {
            using var db = People();
            var rows = db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Name", Op = PredicateOp.In, Value = new[] { "Alice", "Carol" } }
                ]
            });
            var names = rows.Select(r => r["Name"]?.ToString()).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "Alice", "Carol" }, names);
        }

        [Fact]
        public void Filter_And_MultiplePredicates()
        {
            using var db = People();
            var rows = db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Name", Op = PredicateOp.Eq, Value = "Alice" },
                    new ColumnPredicate { Column = "Age", Op = PredicateOp.Ge, Value = 30 }
                ]
            });
            var row = Assert.Single(rows);
            Assert.Equal("Alice", row["Name"]);
        }

        [Fact]
        public void Filter_UnknownColumn_Throws()
        {
            using var db = People();
            var ex = Assert.Throws<DataBallException>(() => db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Nope", Op = PredicateOp.Eq, Value = 1 }
                ]
            }));
            Assert.Contains("Nope", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Filter_DoesNotMutateTable()
        {
            using var db = People();
            _ = db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Name", Op = PredicateOp.Eq, Value = "Alice" }
                ]
            });
            Assert.Equal(3L, Convert.ToInt64(db.Query("SELECT COUNT(*) AS n FROM data")[0]["n"]));
        }

        [Fact]
        public void Filter_SelectsOnlyRequestedColumns()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "meas.csv");
                File.WriteAllText(csv, "Pout,EVM,Gain\n0,-50,20\n");
                using var db = DataBall.Open(csv);
                var row = Assert.Single(db.Filter(new SessionFilter
                {
                    Columns = ["Pout", "EVM"]
                }));
                Assert.Equal(2, row.Count);
                Assert.True(row.ContainsKey("Pout"));
                Assert.True(row.ContainsKey("EVM"));
                Assert.False(row.ContainsKey("Gain"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Filter_ObfuscatorFixture_OneTemp()
        {
            var csv = Path.Combine(AppContext.BaseDirectory, "fixtures", "semiconductor-sweep.csv");
            using var db = DataBall.Open(csv);
            var rows = db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Temp", Op = PredicateOp.Eq, Value = 25.0 }
                ]
            });
            Assert.Equal(27, rows.Count);
        }

        [Fact]
        public async System.Threading.Tasks.Task ApplyFilter_ExportCsv_OnlyMatchingRows()
        {
            var dir = TempDir();
            try
            {
                using var db = People();
                db.ApplyFilter(new SessionFilter
                {
                    Predicates =
                    [
                        new ColumnPredicate { Column = "Name", Op = PredicateOp.Eq, Value = "Alice" }
                    ]
                });
                var filtered = Path.Combine(dir, "filtered.csv");
                await db.ExportAsync(filtered, ExportType.Csv);
                using (var opened = DataBall.Open(filtered))
                    Assert.Equal("Alice", Assert.Single(opened.Query("SELECT Name FROM data"))["Name"]);

                db.ApplyFilter(null);
                var all = Path.Combine(dir, "all.csv");
                await db.ExportAsync(all, ExportType.Csv);
                using var restored = DataBall.Open(all);
                Assert.Equal(3, restored.Query("SELECT Name FROM data").Count);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Filter_NoDataTable_Throws()
        {
            using var db = new DataBall();
            Assert.Throws<DataBallException>(() => db.Filter(new SessionFilter()));
        }

        [Fact]
        public void Filter_TimestampWithSubMillisecondPrecision_MatchesStoredRow()
        {
            using var db = TimestampSession();
            var stored = StoredSubMillisecond(db);
            var rows = db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Ts", Op = PredicateOp.Eq, Value = stored }
                ]
            });
            var row = Assert.Single(rows);
            Assert.Equal(stored, Assert.IsType<DateTime>(row["Ts"]));
        }

        [Fact]
        public void Filter_TimestampInclusiveUpperBound_IncludesRow()
        {
            using var db = TimestampSession();
            var stored = StoredSubMillisecond(db);
            var rows = db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Ts", Op = PredicateOp.Le, Value = stored }
                ]
            });
            Assert.Contains(rows, r => Equals(r["Ts"], stored));
            Assert.Equal(2, rows.Count);
        }

        [Fact]
        public void Filter_DateOnlyPredicate_FiltersOrThrows()
        {
            using var db = new DataBall();
            db.AddColumn<DateTime>("Day", new[]
            {
                new DateTime(2024, 1, 1),
                new DateTime(2024, 1, 2),
                new DateTime(2024, 6, 1)
            });
            var rows = db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Day", Op = PredicateOp.Ge, Value = new DateOnly(2024, 1, 2) }
                ]
            });
            Assert.Equal(2, rows.Count);
            var days = rows.Select(r => Assert.IsType<DateTime>(r["Day"]).Date).OrderBy(d => d).ToArray();
            Assert.Equal(new[] { new DateTime(2024, 1, 2), new DateTime(2024, 6, 1) }, days);
        }

        [Fact]
        public void Filter_EqualsNull_MatchesNullRows()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new string?[] { "Alice", null, "Bob" });
            var rows = db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Name", Op = PredicateOp.Eq, Value = null }
                ]
            });
            var row = Assert.Single(rows);
            Assert.Null(row["Name"]);
        }

        [Fact]
        public void Filter_NonFiniteDouble_ThrowsDataBallException()
        {
            using var db = new DataBall();
            db.AddColumn<double>("X", new[] { 1.0 });
            var ex = Assert.Throws<DataBallException>(() => db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "X", Op = PredicateOp.Eq, Value = double.NaN }
                ]
            }));
            Assert.DoesNotContain("Referenced column", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Filter_RangeNullBound_ThrowsDataBallException()
        {
            using var db = People();
            Assert.Throws<DataBallException>(() => db.Filter(new SessionFilter
            {
                Predicates =
                [
                    new ColumnPredicate { Column = "Age", Op = PredicateOp.Range, Value = 20, ValueTo = null }
                ]
            }));
        }

        [Fact]
        public async System.Threading.Tasks.Task ApplyFilter_ExportCsv_TimestampFilter_RowCountMatchesFilter()
        {
            var dir = TempDir();
            try
            {
                using var db = TimestampSession();
                var stored = StoredSubMillisecond(db);
                var filter = new SessionFilter
                {
                    Predicates =
                    [
                        new ColumnPredicate { Column = "Ts", Op = PredicateOp.Eq, Value = stored }
                    ]
                };
                var expected = db.Filter(filter).Count;
                db.ApplyFilter(filter);
                var csv = Path.Combine(dir, "filtered.csv");
                await db.ExportAsync(csv, ExportType.Csv);
                using var opened = DataBall.Open(csv);
                Assert.Equal(expected, opened.Query("SELECT * FROM data").Count);
                Assert.Equal(1, expected);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static DataBall People()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "people.csv");
                File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\nCarol,40\n");
                return DataBall.Open(csv);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static DataBall TimestampSession()
        {
            var db = new DataBall();
            db.AddColumn<DateTime>("Ts", new[]
            {
                new DateTime(2024, 1, 1),
                new DateTime(2024, 1, 1).AddTicks(12345)
            });
            return db;
        }

        private static DateTime StoredSubMillisecond(DataBall db)
        {
            var values = db.Query("SELECT Ts FROM data ORDER BY Ts")
                .Select(r => Assert.IsType<DateTime>(r["Ts"]))
                .ToArray();
            Assert.Equal(2, values.Length);
            Assert.NotEqual(values[0], values[1]);
            return values[1];
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-filter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
