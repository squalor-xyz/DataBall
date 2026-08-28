using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class RelationshipConfigTests
    {
        [Fact]
        public void LoadConfig_ReadmeTriggerReset_Binds()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path, """
                    {
                      "relationships": [
                        { "trigger": "Name", "reset": ["Age", "Date"] }
                      ]
                    }
                    """);
                var config = Config.LoadConfig(path);
                var rel = Assert.Single(config.Relationships);
                Assert.Equal("Name", rel.TriggerField);
                Assert.Equal(new[] { "Age", "Date" }, rel.ResetFields);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void LoadConfig_PascalTriggerFieldResetFields_Binds()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path, """
                    {
                      "Relationships": [
                        { "TriggerField": "Name", "ResetFields": ["Age"] }
                      ]
                    }
                    """);
                var config = Config.LoadConfig(path);
                var rel = Assert.Single(config.Relationships);
                Assert.Equal("Name", rel.TriggerField);
                Assert.Equal(new[] { "Age" }, rel.ResetFields);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void LoadConfig_BothNames_PrefersTriggerReset()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path, """
                    {
                      "relationships": [
                        {
                          "trigger": "Name",
                          "TriggerField": "Other",
                          "reset": ["Age"],
                          "ResetFields": ["Nope"]
                        }
                      ]
                    }
                    """);
                var config = Config.LoadConfig(path);
                var rel = Assert.Single(config.Relationships);
                Assert.Equal("Name", rel.TriggerField);
                Assert.Equal(new[] { "Age" }, rel.ResetFields);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void LoadConfig_LowercaseConfigKeys_BindsRelationshipsAndColumns()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "config.json");
                File.WriteAllText(path, """
                    {
                      "metadata": { "Operator": "Alice" },
                      "columns": { "Name": "string", "Age": "int" },
                      "relationships": [
                        { "trigger": "Name", "reset": ["Age"] }
                      ]
                    }
                    """);
                var config = Config.LoadConfig(path);
                Assert.Equal("string", config.Columns["Name"]);
                Assert.Equal("int", config.Columns["Age"]);
                var rel = Assert.Single(config.Relationships);
                Assert.Equal("Name", rel.TriggerField);
                Assert.Equal(new[] { "Age" }, rel.ResetFields);

                using var db = new DataBall(path);
                Assert.Equal("Alice", db.Metadata["Operator"]);
                db.InitializeRow(new Dictionary<string, object?> { ["Name"] = "Alice", ["Age"] = 30 });
                db.CommitRow();
                db.InitializeRow();
                db.ModifyField("Name", "Bob");
                db.CommitRow();
                var rows = db.Query("SELECT * FROM \"data\" ORDER BY rowid");
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alice", rows[0]["Name"]);
                Assert.Equal(30, Convert.ToInt32(rows[0]["Age"]));
                Assert.Equal("Bob", rows[1]["Name"]);
                Assert.Null(rows[1]["Age"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Relationship_Write_UsesTriggerReset()
        {
            var json = JsonSerializer.Serialize(new Relationship
            {
                TriggerField = "Name",
                ResetFields = new List<string> { "Age", "Date" }
            });
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("Name", doc.RootElement.GetProperty("trigger").GetString());
            var reset = doc.RootElement.GetProperty("reset");
            Assert.Equal(2, reset.GetArrayLength());
            Assert.Equal("Age", reset[0].GetString());
            Assert.Equal("Date", reset[1].GetString());
            Assert.False(doc.RootElement.TryGetProperty("TriggerField", out _));
            Assert.False(doc.RootElement.TryGetProperty("ResetFields", out _));
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "databall-pr3", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
