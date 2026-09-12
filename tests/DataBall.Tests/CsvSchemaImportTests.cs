// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using squalor.DataBall.Import;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class CsvSchemaImportTests
    {
        [Fact]
        public void ImportCsv_ParenUnits_RenamesAndTypesDouble()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "meas.csv");
                File.WriteAllText(csv, "EVM(dB),I_Total(A)\n1.5,0.02\n2.25,0.03\n");
                using var db = new DataBall();
                ImportManager.ImportFromCsv(db, csv, append: false);
                var rows = db.Query("SELECT EVM, I_Total FROM data ORDER BY EVM");
                Assert.Equal(2, rows.Count);
                Assert.Equal(1.5, Assert.IsType<double>(rows[0]["EVM"]), 3);
                Assert.Equal(0.02, Assert.IsType<double>(rows[0]["I_Total"]), 3);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ImportCsv_LotMetadata_MovesToMetaWhenConstant()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "meas.csv");
                File.WriteAllText(csv, "Lot,EVM(dB)\nL1,1.5\nL1,2.0\n");
                using var db = new DataBall();
                ImportManager.ImportFromCsv(db, csv, append: false);
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
        public void ImportCsv_LotVaries_ThrowsWhenRequireConstant()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "meas.csv");
                File.WriteAllText(csv, "Lot,EVM(dB)\nL1,1.5\nL2,2.0\n");
                using var db = new DataBall();
                var ex = Assert.Throws<DataBallException>(() => ImportManager.ImportFromCsv(db, csv, append: false));
                var msg = (ex.InnerException ?? ex).Message;
                Assert.Contains("Lot", msg, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Import_HeaderWithTrailingComma_Succeeds()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "trail.csv");
                File.WriteAllText(csv, "Freq(GHz),EVM(dB),\n2.4,-30,\n");
                using var db = DataBall.Open(csv);
                var row = Assert.Single(db.Query("SELECT Freq, EVM FROM data"));
                Assert.IsType<double>(row["Freq"]);
                Assert.IsType<double>(row["EVM"]);
                Assert.Equal(2.4, Convert.ToDouble(row["Freq"]), 3);
                Assert.Equal(-30.0, Convert.ToDouble(row["EVM"]), 3);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Import_LeadingBlankLine_StillAppliesSchema()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "blank.csv");
                File.WriteAllText(csv, "\nFreq(GHz),EVM(dB)\n2.4,-30\n");
                using var db = DataBall.Open(csv);
                var row = Assert.Single(db.Query("SELECT Freq, EVM FROM data"));
                Assert.IsType<double>(row["Freq"]);
                Assert.IsType<double>(row["EVM"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Import_SemicolonDelimiter_AppliesSchema()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "semi.csv");
                File.WriteAllText(csv, "Freq(GHz);EVM(dB)\n2.4;-30\n");
                using var db = DataBall.Open(csv);
                var row = Assert.Single(db.Query("SELECT Freq, EVM FROM data"));
                Assert.IsType<double>(row["Freq"]);
                Assert.IsType<double>(row["EVM"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ImportCsv_ClassificationOverlay()
        {
            var dir = TempDir();
            try
            {
                var config = Path.Combine(dir, "config.json");
                File.WriteAllText(config, """{ "classification": ["Pass"] }""");
                using var db = new DataBall(config);
                Assert.Equal(ParameterRole.Classification, db.Schema.ResolveRole("Pass"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-csv-schema-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
