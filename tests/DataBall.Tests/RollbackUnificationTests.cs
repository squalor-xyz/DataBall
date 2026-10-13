// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class RollbackUnificationTests
    {
        [Fact]
        public void CommitRow_FailureAfterAlter_RestoresViaSnapshot()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice" });
            db.AddColumn<int>("Age", new[] { 30 });
            var columnsBefore = StoredColumns(db);
            var expectedBefore = ExpectedKeys(db);
            var metadataBefore = Metadata(db);

            // "Extra" is a new column: its ALTER runs before the insert, and the insert then fails on the non-int "Age".
            db.InitializeRow();
            db.ModifyField("Age", "not-an-int");
            db.ModifyField("Extra", 1);
            Assert.ThrowsAny<Exception>(() => db.CommitRow());

            Assert.DoesNotContain("Extra", StoredColumns(db));
            Assert.Equal(columnsBefore, StoredColumns(db));
            Assert.Equal(expectedBefore, ExpectedKeys(db));
            Assert.Equal(metadataBefore, Metadata(db));
            Assert.Single(db.Query("SELECT Name FROM data"));

            // The pending row survived: once "Age" is valid, the same commit succeeds and keeps the new field.
            db.ModifyField("Age", 31);
            db.CommitRow();
            var rows = db.Query("SELECT Name, Age, Extra FROM data ORDER BY rowid");
            Assert.Equal(2, rows.Count);
            Assert.Equal(31, Convert.ToInt32(rows[1]["Age"], CultureInfo.InvariantCulture));
            Assert.Equal(1, Convert.ToInt32(rows[1]["Extra"], CultureInfo.InvariantCulture));
        }

        [Fact]
        public void CommitRow_FailureAfterRelationshipReset_KeepsPendingRowAsItWas()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-rollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """
                    {
                      "columns": { "Id": "long", "Name": "string", "Age": "int" },
                      "relationships": [ { "trigger": "Id", "reset": ["Name"] } ]
                    }
                    """);
                using var db = new DataBall(config);
                db.AddColumn<long>("Id", new[] { 1L });
                db.AddColumn("Name", new[] { "Alice" });
                db.AddColumn<int>("Age", new[] { 30 });

                // Changing "Id" makes the commit reset "Name"; the insert then fails on the non-int "Age".
                db.InitializeRow();
                db.ModifyField("Id", 2L);
                db.ModifyField("Age", "not-an-int");
                Assert.ThrowsAny<Exception>(() => db.CommitRow());

                // The failed commit left the pending row as it was, so its reset of "Name" did not stick:
                // with "Id" back at its original value nothing resets "Name" any more, and it keeps "Alice".
                db.ModifyField("Id", 1L);
                db.ModifyField("Age", 31);
                db.CommitRow();
                var rows = db.Query("SELECT Id, Name, Age FROM data ORDER BY rowid");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[1]["Name"]);
                Assert.Equal(31, Convert.ToInt32(rows[1]["Age"], CultureInfo.InvariantCulture));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Bounce_WideFailure_RestoresViaSnapshot()
        {
            using var db = new DataBall();
            db.AddColumn("Site", new[] { "A", "A" });
            // A constant NaN is extracted to metadata, and the metadata JSON cannot hold NaN: the write fails after "Site" was extracted.
            db.AddColumn<double>("Ratio", new[] { double.NaN, double.NaN });
            db.AddColumn<int>("Meas", new[] { 1, 2 });
            var columnsBefore = StoredColumns(db);
            var expectedBefore = ExpectedKeys(db);
            var metadataBefore = Metadata(db);

            await Assert.ThrowsAsync<DataBallException>(() => db.Bounce());

            Assert.Equal(columnsBefore, StoredColumns(db));
            Assert.Equal(expectedBefore, ExpectedKeys(db));
            Assert.Equal(metadataBefore, Metadata(db));
            var rows = db.Query("SELECT Site, Meas FROM data ORDER BY Meas");
            Assert.Equal(2, rows.Count);
            Assert.Equal("A", rows[0]["Site"]);
        }

        [Fact]
        public void AddColumn_WideFailureAfterCreateTable_LeavesNoTable()
        {
            using var db = new DataBall();

            // The column list fails while its values are appended, after "data" was created for it.
            Assert.Throws<InvalidOperationException>(() => db.AddColumn<int>("Meas", new FailingList(3, failAt: 1)));

            Assert.Empty(StoredColumns(db));
            Assert.Empty(ExpectedKeys(db));
            Assert.Equal(0, Convert.ToInt32(db.Query("SELECT count(*) AS n FROM information_schema.tables WHERE table_name = 'data'")[0]["n"], CultureInfo.InvariantCulture));

            // Nothing was left behind, so the same column can be added.
            db.AddColumn<int>("Meas", new[] { 1, 2 });
            Assert.Equal(2, db.Query("SELECT Meas FROM data").Count);
        }

        private sealed class FailingList : IReadOnlyList<int>
        {
            private readonly int _count;
            private readonly int _failAt;

            public FailingList(int count, int failAt)
            {
                _count = count;
                _failAt = failAt;
            }

            public int Count => _count;

            public int this[int index] => index == _failAt ? throw new InvalidOperationException("injected read failure") : index;

            public IEnumerator<int> GetEnumerator() => Enumerable.Range(0, _count).GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static string[] StoredColumns(DataBall db) =>
            db.Query("""
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'main' AND table_name = 'data'
                ORDER BY ordinal_position
                """)
                .Select(r => Convert.ToString(r["column_name"], CultureInfo.InvariantCulture)!)
                .ToArray();

        private static string[] ExpectedKeys(DataBall db) =>
            db.ExpectedColumnTypes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

        private static KeyValuePair<string, object?>[] Metadata(DataBall db) =>
            db.Metadata.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
    }
}
