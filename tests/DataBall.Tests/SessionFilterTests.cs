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

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-filter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
