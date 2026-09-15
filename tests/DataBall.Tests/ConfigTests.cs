// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class ConfigTests
    {
        [Fact]
        public async Task Schema_MutatingReturnedConfig_DoesNotAffectSession()
        {
            var dir = TempDir();
            try
            {
                var csv = Path.Combine(dir, "meas.csv");
                File.WriteAllText(csv, "EVM(dB)\n1.5\n");
                using var db = new DataBall();
                db.Schema.Columns["EVM"] = "int";
                await db.ImportAsync(csv);
                var row = Assert.Single(db.Query("SELECT EVM FROM data"));
                Assert.Equal(1.5, Assert.IsType<double>(row["EVM"]), 3);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Merge_DoesNotMutateInputSpecs()
        {
            var spec = new ParameterSpec();
            var baseline = new Config();
            baseline.Parameters["Foo"] = spec;
            var overlay = new Config();
            overlay.Stimulus.Add("Foo");
            Config.Merge(baseline, overlay);
            Assert.True(string.IsNullOrWhiteSpace(spec.Role));
        }

        [Fact]
        public async Task ApplyImportedConfig_DoesNotMutatePreMergeConfig()
        {
            var dir = TempDir();
            try
            {
                var overlay = Path.Combine(dir, "session.json");
                File.WriteAllText(overlay, """{ "parameters": { "Foo": {} } }""");
                using var db = new DataBall(overlay);
                var spec = db.Schema.Parameters["Foo"];
                Assert.True(string.IsNullOrWhiteSpace(spec.Role));

                var ball = Path.Combine(dir, "overlay.ball");
                using (var zip = ZipFile.Open(ball, ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(zip.CreateEntry("metadata.json").Open(), Encoding.UTF8))
                        writer.Write("{}");
                    using (var writer = new StreamWriter(zip.CreateEntry("config.json").Open(), Encoding.UTF8))
                        writer.Write("""{ "stimulus": ["Foo"] }""");
                }

                await db.ImportAsync(ball);
                Assert.True(string.IsNullOrWhiteSpace(spec.Role));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Query_MutatingReturnedRow_DoesNotAffectStore()
        {
            using var db = new DataBall();
            db.AddColumn("Name", new[] { "Alice" });
            var row = Assert.Single(db.Query("SELECT Name FROM data"));
            row["Name"] = "MUTATED";
            var again = Assert.Single(db.Query("SELECT Name FROM data"));
            Assert.Equal("Alice", again["Name"]);
        }

        [Fact]
        public void LoadConfig_MissingFile_Throws()
        {
            var path = Path.Combine(Path.GetTempPath(), "databall-missing-config-" + Guid.NewGuid().ToString("N") + ".json");
            var ex = Assert.Throws<DataBallException>(() => Config.LoadConfig(path));
            Assert.Equal("Failed to load config", ex.Message);
        }

        [Fact]
        public void LoadConfig_InvalidJson_Throws()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path, "{ this is not json }");
                var ex = Assert.Throws<DataBallException>(() => Config.LoadConfig(path));
                Assert.Equal("Failed to load config", ex.Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ParseColumnType_Int32Alias()
        {
            Assert.Equal(typeof(int), Config.ParseColumnType("int32"));
        }

        [Fact]
        public void ParseColumnType_BooleanAlias()
        {
            Assert.Equal(typeof(bool), Config.ParseColumnType("boolean"));
        }

        [Fact]
        public void ParseColumnType_DateTimeAlias()
        {
            Assert.Equal(typeof(DateTime), Config.ParseColumnType("datetime"));
        }

        [Fact]
        public void ParseColumnType_Blank_Throws()
        {
            var ex = Assert.Throws<DataBallException>(() => Config.ParseColumnType(""));
            Assert.Equal("Unknown column type ''", ex.Message);
        }

        [Fact]
        public void ParseColumnType_Unknown_Throws()
        {
            var ex = Assert.Throws<DataBallException>(() => Config.ParseColumnType("nope"));
            Assert.Equal("Unknown column type 'nope'", ex.Message);
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-t2-config", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
