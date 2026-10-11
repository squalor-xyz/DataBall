// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using squalor.DataBall.Import;
using Xunit;

namespace squalor.DataBall.Tests
{
    /// <summary>
    /// db-26: a wide CSV append applies the CSV schema (canonical header names, configured and learned types,
    /// configured metadata extraction) to the staging rows before the merge, as a layout session does. One CSV
    /// rule for both modes: canonical names everywhere, metadata that differs on an append demotes to a column,
    /// and existing columns keep their physical type (the db-23 merge rule decides).
    /// </summary>
    public class WideCsvSchemaTests
    {
        private const string RequireConstant = """{"metadataPolicy":"requireConstant"}""";
        private const string PolicyFirst = """{"metadataPolicy":"first"}""";

        [Fact]
        public async Task WideAppend_UnitHeader_MergesIntoCanonicalColumn()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "id,Gain\n1,1.5\n");
                var b = WriteFile(dir, "b.csv", "id,Gain(dB)\n2,2.5\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Equal(new[] { "id:BIGINT", "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT id, Gain, _source_key FROM data ORDER BY id");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1L, rows[0]["id"]);
                Assert.Equal(1.5, rows[0]["Gain"]);
                Assert.Equal(1L, rows[0]["_source_key"]);
                Assert.Equal(2L, rows[1]["id"]);
                Assert.Equal(2.5, rows[1]["Gain"]);
                Assert.Equal(2L, rows[1]["_source_key"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_UnitHeader_NewColumnsKeepAppendOrder()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "id,Gain\n1,1.5\n");
                var b = WriteFile(dir, "b.csv", "Note,Gain(dB),id\nhi,2.5,2\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Equal(new[] { "id:BIGINT", "Gain:DOUBLE", "_source_key:BIGINT", "Note:VARCHAR" }, Columns(db));
                var rows = db.Query("SELECT id, Gain, Note FROM data ORDER BY id");
                Assert.Null(rows[0]["Note"]);
                Assert.Equal("hi", rows[1]["Note"]);
                Assert.Equal(2.5, rows[1]["Gain"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_RequireConstant_DifferentValue_Demotes()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "strict.json", RequireConstant);
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\nL1,2.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\nL2,3.0\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                Assert.Equal("L1", db.Metadata["Lot"]);

                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT", "Lot:VARCHAR" }, Columns(db));
                var rows = db.Query("SELECT Lot, Gain FROM data ORDER BY Gain");
                Assert.Equal(3, rows.Count);
                Assert.Equal("L1", rows[0]["Lot"]);
                Assert.Equal("L1", rows[1]["Lot"]);
                Assert.Equal("L2", rows[2]["Lot"]);
                Assert.Equal(3.0, rows[2]["Gain"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_RequireConstant_VaryingWithinAppendedFile_ThrowsAndRollsBack()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "strict.json", RequireConstant);
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\nL1,2.0\nL2,3.0\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);

                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(b, new ImportOptions { Append = true }));

                Assert.Contains("Metadata field 'Lot' is not constant", ex.GetBaseException().Message, StringComparison.Ordinal);
                Assert.Equal("L1", db.Metadata["Lot"]);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT Gain FROM data");
                Assert.Single(rows);
                Assert.Equal(1.0, rows[0]["Gain"]);
                Assert.Single(db.Sources);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_RequireConstant_ThrowsAfterEarlierFieldLifted_LeavesMetadataAsItWas()
        {
            var dir = TempDir();
            try
            {
                // Lot is lifted from the appended rows before Tester (stored T1) is found to vary; nothing may stay.
                var config = WriteFile(dir, "strict.json", RequireConstant);
                var a = WriteFile(dir, "a.csv", "Tester,Gain\nT1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Tester,Gain\nL5,T1,2.0\nL5,T2,3.0\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);

                await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(b, new ImportOptions { Append = true }));

                Assert.Single(db.Metadata);
                Assert.Equal("T1", db.Metadata["Tester"]);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                Assert.Single(db.Query("SELECT Gain FROM data"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_RequireConstant_ColumnField_VaryingWithinFile_Throws()
        {
            var dir = TempDir();
            try
            {
                // The second file demotes Lot to a column; the third varies Lot inside itself, so it must still throw.
                var config = WriteFile(dir, "strict.json", RequireConstant);
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\nL2,2.0\n");
                var c = WriteFile(dir, "c.csv", "Lot,Gain\nL3,3.0\nL4,4.0\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                Assert.Empty(db.Metadata);

                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(c, new ImportOptions { Append = true }));

                Assert.Contains("Metadata field 'Lot' is not constant", ex.GetBaseException().Message, StringComparison.Ordinal);
                Assert.Empty(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT", "Lot:VARCHAR" }, Columns(db));
                var rows = db.Query("SELECT Lot, Gain FROM data ORDER BY Gain");
                Assert.Equal(new object?[] { "L1", "L2" }, rows.Select(r => r["Lot"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_RequireConstant_ColumnField_ConstantFile_StaysColumn()
        {
            var dir = TempDir();
            try
            {
                // Lot is already a column; a third file with one Lot value is accepted and does not lift it.
                var config = WriteFile(dir, "strict.json", RequireConstant);
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\nL2,2.0\n");
                var c = WriteFile(dir, "c.csv", "Lot,Gain\nL3,3.0\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                await db.ImportAsync(c, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT", "Lot:VARCHAR" }, Columns(db));
                var rows = db.Query("SELECT Lot, Gain FROM data ORDER BY Gain");
                Assert.Equal(new object?[] { "L1", "L2", "L3" }, rows.Select(r => r["Lot"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_MetadataReconciliation_UsesCanonicalNames()
        {
            var dir = TempDir();
            try
            {
                // Lot(id) is the canonical field Lot (a BIGINT). The raw header differs from the stored key.
                var a = WriteFile(dir, "a.csv", "Lot,Gain\n7,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot(id),Gain\n8,3.0\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                Assert.Equal(7L, db.Metadata["Lot"]);

                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT", "Lot:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT Lot, Gain FROM data ORDER BY Gain");
                Assert.Equal(7L, rows[0]["Lot"]);
                Assert.Equal(8L, rows[1]["Lot"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_MetadataReconciliation_EqualValueUnderCanonicalName_StaysMetadata()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "Lot,Gain\n7,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot(id),Gain\n7,3.0\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Equal(7L, db.Metadata["Lot"]);
                Assert.Single(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                Assert.Equal(2, db.Query("SELECT Gain FROM data").Count);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_PolicyFirst_StoredValueWins()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "first.json", PolicyFirst);
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\nL1,2.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\nL2,3.0\nL3,4.0\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Equal("L1", db.Metadata["Lot"]);
                Assert.Single(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT Gain, _source_key FROM data ORDER BY Gain");
                Assert.Equal(new object?[] { 1.0, 2.0, 3.0, 4.0 }, rows.Select(r => r["Gain"]).ToArray());
                Assert.Equal(new object?[] { 1L, 1L, 2L, 2L }, rows.Select(r => r["_source_key"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_ExistingColumn_NewConstantFile_StaysColumn()
        {
            var dir = TempDir();
            try
            {
                // Lot varied in the first file, so it is a column. A later file with one Lot value does not lift it.
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\nL2,2.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\nL3,3.0\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                Assert.Empty(db.Metadata);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                Assert.Equal(new[] { "Lot:VARCHAR", "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT Lot FROM data ORDER BY Gain");
                Assert.Equal(new object?[] { "L1", "L2", "L3" }, rows.Select(r => r["Lot"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_NewConstantMetadataField_LiftsToMetadata()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "Gain\n1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\nL9,2.0\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Equal("L9", db.Metadata["Lot"]);
                Assert.Single(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                Assert.Equal(new object?[] { 1.0, 2.0 }, db.Query("SELECT Gain FROM data ORDER BY Gain").Select(r => r["Gain"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_EqualMetadata_Ignored()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\nL1,3.0\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Equal("L1", db.Metadata["Lot"]);
                Assert.Single(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                Assert.Equal(2, db.Query("SELECT Gain FROM data").Count);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_LiftConstant_DifferentValue_Demotes()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\nL2,3.0\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT", "Lot:VARCHAR" }, Columns(db));
                var rows = db.Query("SELECT Lot, Gain, _source_key FROM data ORDER BY Gain");
                Assert.Equal("L1", rows[0]["Lot"]);
                Assert.Equal(1L, rows[0]["_source_key"]);
                Assert.Equal("L2", rows[1]["Lot"]);
                Assert.Equal(2L, rows[1]["_source_key"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideImport_ExistingColumns_NotRecastOnConfigGrowth()
        {
            var dir = TempDir();
            try
            {
                // The Parquet file brings v as a DOUBLE holding 1.5. The config declares v a long, which an
                // import casts incoming values into; it no longer rewrites the columns already in the session.
                var config = WriteFile(dir, "types.json", """{"columns":{"v":"long"}}""");
                var parquet = Path.Combine(dir, "a.parquet");
                using (var writer = new DataBall())
                    writer.Query($"COPY (SELECT CAST(1 AS BIGINT) AS id, CAST(1.5 AS DOUBLE) AS v) TO '{parquet}' (FORMAT PARQUET)");
                var csv = WriteFile(dir, "b.csv", "id,v\n2,7\n");
                using var db = new DataBall(config);
                await db.ImportAsync(parquet);
                await db.ImportAsync(csv, new ImportOptions { Append = true });

                Assert.Equal(new[] { "id:BIGINT", "v:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT id, v FROM data ORDER BY id");
                Assert.Equal(1.5, rows[0]["v"]);
                Assert.Equal(7.0, rows[1]["v"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideReplace_FirstImport_TypingUnchanged()
        {
            var dir = TempDir();
            try
            {
                var csv = WriteFile(dir, "a.csv", "Lot,Tester,Gain(dB),Count(id),x\nL1,T1,1.5,2.7,hello\nL1,T1,2.5,3,world\n");
                using var db = new DataBall();
                await db.ImportAsync(csv);
                AssertSingleCsvState(db);

                // A replace over a live session gives the same state, with the source table started over.
                var other = WriteFile(dir, "other.csv", "k\n1\n");
                using var replaced = new DataBall();
                await replaced.ImportAsync(other);
                await replaced.ImportAsync(csv);
                AssertSingleCsvState(replaced);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideReplace_CsvOfOnlyMetadataColumns_LeavesNoDataTable()
        {
            var dir = TempDir();
            try
            {
                var rows = WriteFile(dir, "rows.csv", "Gain\n1.0\n");
                var onlyMeta = WriteFile(dir, "meta.csv", "Lot\nL9\n");
                using var db = new DataBall();
                await db.ImportAsync(rows);

                await db.ImportAsync(onlyMeta);

                Assert.Equal("L9", db.Metadata["Lot"]);
                Assert.Single(db.Metadata);
                Assert.Empty(db.Query("SELECT table_name FROM information_schema.tables WHERE table_name = 'data'"));
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_LearnedNeverNarrows_StillHolds()
        {
            var dir = TempDir();
            try
            {
                // The header unit teaches Count a long. A later append of 2.7 widens Count; it does not cast to 3.
                var a = WriteFile(dir, "a.csv", "Count(id),x\n1,a\n");
                var b = WriteFile(dir, "b.csv", "Count(id),x\n2.7,b\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                Assert.Equal(new[] { "Count:BIGINT", "x:VARCHAR", "_source_key:BIGINT" }, Columns(db));

                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Equal(new[] { "Count:DOUBLE", "x:VARCHAR", "_source_key:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT Count, x FROM data ORDER BY x");
                Assert.Equal(1.0, rows[0]["Count"]);
                Assert.Equal(2.7, rows[1]["Count"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_Declared_StillCastsIncoming()
        {
            var dir = TempDir();
            try
            {
                // A config-declared long always casts: 2.7 becomes 3, as on a layout session. The unit header
                // keeps the raw name away from the pre-schema cast, so the staging schema pass does the cast.
                var config = WriteFile(dir, "types.json", """{"columns":{"Count":"long"}}""");
                var a = WriteFile(dir, "a.csv", "Count(id)\n1\n");
                var b = WriteFile(dir, "b.csv", "Count(id)\n2.7\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Equal(new[] { "Count:BIGINT", "_source_key:BIGINT" }, Columns(db));
                Assert.Equal(new object?[] { 1L, 3L }, db.Query("SELECT Count FROM data ORDER BY Count").Select(r => r["Count"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_ArchiveEntries_MergeByCanonicalName()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "id,Gain\n1,1.5\n");
                var b = WriteFile(dir, "b.csv", "id,Gain(dB)\n2,2.5\n");
                var zip = Path.Combine(dir, "runs.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    archive.CreateEntryFromFile(a, "a.csv");
                    archive.CreateEntryFromFile(b, "b.csv");
                }
                using var db = new DataBall();
                await db.ImportAsync(zip);

                Assert.Equal(new[] { "id:BIGINT", "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT id, Gain, _source_key FROM data ORDER BY id");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1.5, rows[0]["Gain"]);
                Assert.Equal(2.5, rows[1]["Gain"]);
                Assert.Equal(new object?[] { 1L, 2L }, rows.Select(r => r["_source_key"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_ImportManagerCsv_MergesByCanonicalName()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "id,Gain\n1,1.5\n");
                var b = WriteFile(dir, "b.csv", "id,Gain(dB)\n2,2.5\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                ImportManager.ImportFromCsv(db, b, append: true);

                Assert.Equal(new[] { "id:BIGINT", "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                Assert.Equal(new object?[] { 1.5, 2.5 }, db.Query("SELECT Gain FROM data ORDER BY id").Select(r => r["Gain"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_ThenBounce_CollapsesRowsAcrossSpellings()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "id,Gain\n1,1.5\n2,2.5\n");
                var b = WriteFile(dir, "b.csv", "id,Gain(dB)\n1,1.5\n3,3.5\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                Assert.Equal(4, db.Query("SELECT * FROM data").Count);

                await db.Bounce();

                Assert.Equal(new[] { "id:BIGINT", "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT id, Gain, _source_key FROM data ORDER BY id");
                Assert.Equal(new object?[] { 1L, 2L, 3L }, rows.Select(r => r["id"]).ToArray());
                Assert.Equal(new object?[] { 1.5, 2.5, 3.5 }, rows.Select(r => r["Gain"]).ToArray());
                Assert.Equal(new object?[] { 1L, 1L, 2L }, rows.Select(r => r["_source_key"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_NullMetadataInAppendedFile_DemotesWithNull()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\n,3.0\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                var rows = db.Query("SELECT Lot, Gain FROM data ORDER BY Gain");
                Assert.Equal("L1", rows[0]["Lot"]);
                Assert.Null(rows[1]["Lot"]);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        // ---- round 2 ----

        [Fact]
        public async Task WideAppend_RequireConstant_DifferentValue_WithNewColumn_Demotes()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "strict.json", RequireConstant);
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain,Note\nL2,2.0,hi\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT", "Lot:VARCHAR", "Note:VARCHAR" }, Columns(db));
                var rows = db.Query("SELECT Lot, Gain, Note FROM data ORDER BY Gain");
                Assert.Equal(new object?[] { "L1", "L2" }, rows.Select(r => r["Lot"]).ToArray());
                Assert.Equal(new object?[] { null, "hi" }, rows.Select(r => r["Note"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_LiftConstant_DifferentValue_WithNewColumn_Demotes()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain,Note\nL2,2.0,hi\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT", "Lot:VARCHAR", "Note:VARCHAR" }, Columns(db));
                var rows = db.Query("SELECT Lot, Note FROM data ORDER BY Gain");
                Assert.Equal(new object?[] { "L1", "L2" }, rows.Select(r => r["Lot"]).ToArray());
                Assert.Equal(new object?[] { null, "hi" }, rows.Select(r => r["Note"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("requireConstant")]
        public async Task WideAppend_DifferentValue_WithWidening_Demotes(string? policy)
        {
            var dir = TempDir();
            try
            {
                var config = policy is null ? null : WriteFile(dir, "p.json", "{\"metadataPolicy\":\"" + policy + "\"}");
                var a = WriteFile(dir, "a.csv", "Lot,n\nL1,1\n");
                var b = WriteFile(dir, "b.csv", "Lot,n\nL2,1.5\n");
                using var db = config is null ? new DataBall() : new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                Assert.Equal(new[] { "n:DOUBLE", "_source_key:BIGINT", "Lot:VARCHAR" }, Columns(db));
                var rows = db.Query("SELECT Lot, n FROM data ORDER BY n");
                Assert.Equal(new object?[] { "L1", "L2" }, rows.Select(r => r["Lot"]).ToArray());
                Assert.Equal(new object?[] { 1.0, 1.5 }, rows.Select(r => r["n"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Layout_Append_Rematerialize_WithDemotedFieldAndNewColumn_Demotes()
        {
            var dir = TempDir();
            try
            {
                // The demoted Tester joins a new dimension (a re-split) in the same append that adds Extra.
                var config = WriteFile(dir, "layout.json", """{"columns":{"Meas":"long"},"tables":{"who":{"kind":"dimension","columns":["Tester"]},"g":{"kind":"measurements","columns":["Meas"]}}}""");
                var a = WriteFile(dir, "a.csv", "Tester,Meas\nT1,1\n");
                var b = WriteFile(dir, "b.csv", "Tester,Extra,Meas\nT2,x,2\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                var rows = db.Query("SELECT Tester, Extra, Meas FROM data ORDER BY Meas");
                Assert.Equal(new object?[] { "T1", "T2" }, rows.Select(r => r["Tester"]).ToArray());
                Assert.Equal(new object?[] { null, "x" }, rows.Select(r => r["Extra"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task Layout_Append_InPlace_WithDemotedFieldAndNewColumn_Demotes()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "layout.json", """{"columns":{"Meas":"long"},"tables":{"g":{"kind":"measurements","columns":["Meas"]}}}""");
                var a = WriteFile(dir, "a.csv", "Tester,Meas\nT1,1\n");
                var b = WriteFile(dir, "b.csv", "Tester,Extra,Meas\nT2,x,2\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                var rows = db.Query("SELECT Tester, Extra, Meas FROM data ORDER BY Meas");
                Assert.Equal(new object?[] { "T1", "T2" }, rows.Select(r => r["Tester"]).ToArray());
                Assert.Equal(new object?[] { null, "x" }, rows.Select(r => r["Extra"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_MetadataOnlyFile_KeepsSourceKey()
        {
            var dir = TempDir();
            try
            {
                // New metadata field in a file with no other column.
                var a = WriteFile(dir, "a.csv", "Gain\n1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot\nL9\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Equal("L9", db.Metadata["Lot"]);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT Gain, _source_key FROM data ORDER BY _source_key");
                Assert.Equal(new object?[] { 1.0, null }, rows.Select(r => r["Gain"]).ToArray());
                Assert.Equal(new object?[] { 1L, 2L }, rows.Select(r => r["_source_key"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_MetadataOnlyFile_EqualValue_KeepsSourceKey()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot\nL1\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Equal("L1", db.Metadata["Lot"]);
                var rows = db.Query("SELECT Gain, _source_key FROM data ORDER BY _source_key");
                Assert.Equal(new object?[] { 1.0, null }, rows.Select(r => r["Gain"]).ToArray());
                Assert.Equal(new object?[] { 1L, 2L }, rows.Select(r => r["_source_key"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        private const string RequireConstantLayout = """{"metadataPolicy":"requireConstant","columns":{"Meas":"long"},"tables":{"g":{"kind":"measurements","columns":["Meas"]}}}""";

        [Fact]
        public async Task LayoutAppend_RequireConstant_VaryingWithinAppendedFile_ThrowsAndLeavesSession()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "layout.json", RequireConstantLayout);
                var a = WriteFile(dir, "a.csv", "Tester,Meas\nT1,1\n");
                var b = WriteFile(dir, "b.csv", "Tester,Meas\nT1,2\nT2,3\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);

                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(b, new ImportOptions { Append = true }));

                Assert.Contains("Metadata field 'Tester' is not constant", ex.GetBaseException().Message, StringComparison.Ordinal);
                Assert.Single(db.Metadata);
                Assert.Equal("T1", db.Metadata["Tester"]);
                Assert.Equal(new[] { "Meas:BIGINT", "_source_key:BIGINT" }, Columns(db));
                var rows = db.Query("SELECT Meas FROM data");
                Assert.Equal(1L, Assert.Single(rows)["Meas"]);
                Assert.Single(db.Sources);
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task LayoutAppend_RequireConstant_DifferentValueAcrossFiles_StillDemotes()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "layout.json", RequireConstantLayout);
                var a = WriteFile(dir, "a.csv", "Tester,Meas\nT1,1\n");
                var b = WriteFile(dir, "b.csv", "Tester,Meas\nT2,2\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                var rows = db.Query("SELECT Tester, Meas FROM data ORDER BY Meas");
                Assert.Equal(new object?[] { "T1", "T2" }, rows.Select(r => r["Tester"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task LayoutAppend_RequireConstant_ColumnField_VaryingWithinFile_Throws()
        {
            var dir = TempDir();
            try
            {
                // The second file demotes Tester to a column; the third varies Tester inside itself, so it must still throw.
                var config = WriteFile(dir, "layout.json", RequireConstantLayout);
                var a = WriteFile(dir, "a.csv", "Tester,Meas\nT1,1\n");
                var b = WriteFile(dir, "b.csv", "Tester,Meas\nT2,2\n");
                var c = WriteFile(dir, "c.csv", "Tester,Meas\nT3,3\nT4,4\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });
                Assert.Empty(db.Metadata);
                var columnsBefore = Columns(db);

                var ex = await Assert.ThrowsAsync<DataBallException>(() => db.ImportAsync(c, new ImportOptions { Append = true }));

                Assert.Contains("Metadata field 'Tester' is not constant", ex.GetBaseException().Message, StringComparison.Ordinal);
                Assert.Empty(db.Metadata);
                Assert.Equal(columnsBefore, Columns(db));
                var rows = db.Query("SELECT Tester, Meas FROM data ORDER BY Meas");
                Assert.Equal(new object?[] { "T1", "T2" }, rows.Select(r => r["Tester"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task LayoutAppend_FieldAlreadyAColumn_NeverAlsoMetadata()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "layout.json", """{"columns":{"Meas":"long"},"tables":{"g":{"kind":"measurements","columns":["Meas"]}}}""");
                var a = WriteFile(dir, "a.csv", "Tester,Meas\nT1,1\nT2,2\n");
                var b = WriteFile(dir, "b.csv", "Tester,Meas\nT3,3\n");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                Assert.Empty(db.Metadata);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                var rows = db.Query("SELECT Tester FROM data ORDER BY Meas");
                Assert.Equal(new object?[] { "T1", "T2", "T3" }, rows.Select(r => r["Tester"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_NewMetadataFieldWithNulls_BecomesColumnWithLiftedValueBackfilled()
        {
            var dir = TempDir();
            try
            {
                var a = WriteFile(dir, "a.csv", "id\n1\n");
                var b = WriteFile(dir, "b.csv", "id,Lot\n2,L1\n3,\n");
                using var db = new DataBall();
                await db.ImportAsync(a);
                await db.ImportAsync(b, new ImportOptions { Append = true });

                Assert.Empty(db.Metadata);
                Assert.Equal(new[] { "id:BIGINT", "_source_key:BIGINT", "Lot:VARCHAR" }, Columns(db));
                var rows = db.Query("SELECT id, Lot FROM data ORDER BY id");
                Assert.Equal(new object?[] { "L1", "L1", null }, rows.Select(r => r["Lot"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_Parquet_PolicyFirst_StoredValueWins()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "first.json", PolicyFirst);
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var parquet = Path.Combine(dir, "b.parquet");
                using (var writer = new DataBall())
                    writer.Query($"COPY (SELECT 'L2' AS Lot, CAST(2.0 AS DOUBLE) AS Gain UNION ALL SELECT 'L3', CAST(3.0 AS DOUBLE)) TO '{parquet}' (FORMAT PARQUET)");
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(parquet, new ImportOptions { Append = true });

                Assert.Equal("L1", db.Metadata["Lot"]);
                Assert.Single(db.Metadata);
                Assert.Equal(new[] { "Gain:DOUBLE", "_source_key:BIGINT" }, Columns(db));
                Assert.Equal(new object?[] { 1.0, 2.0, 3.0 }, db.Query("SELECT Gain FROM data ORDER BY Gain").Select(r => r["Gain"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideAppend_NativeBall_PolicyFirst_StoredValueWins()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "first.json", PolicyFirst);
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var ball = Path.Combine(dir, "b.ball");
                using (var writer = new DataBall())
                {
                    writer.AddRows(new[]
                    {
                        new Dictionary<string, object?> { ["Lot"] = "L2", ["Gain"] = 2.0 },
                        new Dictionary<string, object?> { ["Lot"] = "L3", ["Gain"] = 3.0 },
                    });
                    writer.Save(ball);
                }
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                await db.ImportAsync(ball, new ImportOptions { Append = true });

                Assert.Equal("L1", db.Metadata["Lot"]);
                Assert.Single(db.Metadata);
                Assert.DoesNotContain("Lot:VARCHAR", Columns(db));
                Assert.Equal(new object?[] { 1.0, 2.0, 3.0 }, db.Query("SELECT Gain FROM data ORDER BY Gain").Select(r => r["Gain"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        [Fact]
        public async Task WideMergeOrAppend_PolicyFirst_StoredValueWins()
        {
            var dir = TempDir();
            try
            {
                var config = WriteFile(dir, "first.json", PolicyFirst);
                var a = WriteFile(dir, "a.csv", "Lot,Gain\nL1,1.0\n");
                var b = WriteFile(dir, "b.csv", "Lot,Gain\nL2,2.0\nL3,3.0\n");
                using var other = new DataBall();
                await other.ImportAsync(b);
                using var db = new DataBall(config);
                await db.ImportAsync(a);
                db.MergeOrAppend(other, append: true);

                Assert.Equal("L1", db.Metadata["Lot"]);
                Assert.Single(db.Metadata);
                Assert.DoesNotContain("Lot:VARCHAR", Columns(db));
                Assert.Equal(new object?[] { 1.0, 2.0, 3.0 }, db.Query("SELECT Gain FROM data ORDER BY Gain").Select(r => r["Gain"]).ToArray());
            }
            finally
            {
                TryDeleteDir(dir);
            }
        }

        private static void AssertSingleCsvState(DataBall db)
        {
            Assert.Equal(new[] { "Gain:DOUBLE", "Count:BIGINT", "x:VARCHAR", "_source_key:BIGINT" }, Columns(db));
            Assert.Equal(2, db.Metadata.Count);
            Assert.Equal("L1", db.Metadata["Lot"]);
            Assert.Equal("T1", db.Metadata["Tester"]);
            var rows = db.Query("SELECT Gain, Count, x, _source_key FROM data ORDER BY Gain");
            Assert.Equal(2, rows.Count);
            Assert.Equal(1.5, rows[0]["Gain"]);
            Assert.Equal(3L, rows[0]["Count"]);
            Assert.Equal("hello", rows[0]["x"]);
            Assert.Equal(1L, rows[0]["_source_key"]);
            Assert.Equal(2.5, rows[1]["Gain"]);
            Assert.Equal(3L, rows[1]["Count"]);
            Assert.Equal("world", rows[1]["x"]);
            Assert.Equal(1L, rows[1]["_source_key"]);
            var source = Assert.Single(db.Sources);
            Assert.Equal(1L, source.Key);
        }

        private static string[] Columns(DataBall db)
        {
            return db.Query("SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'main' AND table_name = 'data' ORDER BY ordinal_position")
                .Select(r => $"{r["column_name"]}:{r["data_type"]}")
                .ToArray();
        }

        private static string WriteFile(string dir, string name, string content)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-widecsv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
