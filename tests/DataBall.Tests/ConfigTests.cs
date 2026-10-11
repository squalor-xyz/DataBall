// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
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
                var importedConfig = Path.Combine(dir, "imported.json");
                File.WriteAllText(importedConfig, """{"stimulus":["Foo"]}""");
                using (var source = new DataBall(importedConfig, databasePath: ball))
                    source.SetMetadata("source", "fixture");

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

        [Theory]
        [InlineData("source")]
        [InlineData("Source")]
        [InlineData("_source")]
        [InlineData("_source_key")]
        [InlineData("_SOURCE_file")]
        public void Config_ReservedSourceNames_Rejected(string name)
        {
            var config = new Config { Tables = new() { [name] = new TableSpec { Kind = "measurements", Columns = new() { "value" } } } };
            var merge = Assert.Throws<DataBallException>(() => Config.Merge(Config.CreateDefaults(), config));
            Assert.Equal($"Reserved table name: {name}", merge.Message);
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "reserved.json");
                File.WriteAllText(path, config.ToJson());
                var load = Assert.Throws<DataBallException>(() => Config.LoadConfig(path));
                Assert.Contains("Reserved table name: " + name, load.GetBaseException().Message);
                var open = Assert.Throws<DataBallException>(() => new DataBall(path));
                Assert.Contains("Reserved table name: " + name, open.GetBaseException().Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("columns", "_source_key")]
        [InlineData("columns", "_Source_other")]
        [InlineData("key", "_source_key")]
        [InlineData("key", "_SOURCE_file")]
        public void Config_TablesNamingSourceColumns_Rejected(string list, string column)
        {
            var spec = new TableSpec { Kind = "dimension", Columns = new() { "value" } };
            if (list == "columns")
                spec.Columns.Add(column);
            else
            {
                spec.Columns.Clear();
                spec.Key.Add(column);
            }
            var config = new Config { Tables = new() { ["t"] = spec } };
            var merge = Assert.Throws<DataBallException>(() => Config.Merge(Config.CreateDefaults(), config));
            Assert.Equal($"Reserved column name in tables.t.{list}: {column}", merge.Message);
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "reserved.json");
                File.WriteAllText(path, config.ToJson());
                Assert.Contains($"Reserved column name in tables.t.{list}: {column}", Assert.Throws<DataBallException>(() => Config.LoadConfig(path)).GetBaseException().Message);
                Assert.Throws<DataBallException>(() => new DataBall(path));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Config_TablesWithOtherUnderscoreColumns_Allowed()
        {
            var config = new Config { Tables = new() { ["t"] = new TableSpec { Kind = "dimension", Columns = new() { "_row_note", "origin_source_file" }, Key = new() { "origin_source_file" } } } };
            var merged = Config.Merge(Config.CreateDefaults(), config);
            Assert.True(merged.Tables.ContainsKey("t"));
        }

        [Theory]
        [InlineData("sources")]
        [InlineData("my_source")]
        [InlineData("src")]
        public void Config_SimilarToSourceNames_Allowed(string name)
        {
            var config = new Config { Tables = new() { [name] = new TableSpec { Kind = "measurements", Columns = new() { "value" } } } };
            var merged = Config.Merge(Config.CreateDefaults(), config);
            Assert.True(merged.Tables.ContainsKey(name));
        }

        [Theory]
        [InlineData("_databall")]
        [InlineData("meta")]
        public void Config_ReservedTableNames_Rejected(string name)
        {
            var config = new Config { Tables = new() { [name] = new TableSpec { Kind = "measurements", Columns = new() { "value" } } } };
            Assert.Throws<DataBallException>(() => Config.Merge(Config.CreateDefaults(), config));
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "reserved.json");
                File.WriteAllText(path, config.ToJson());
                Assert.Throws<DataBallException>(() => Config.LoadConfig(path));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
