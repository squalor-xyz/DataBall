// DataBallTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Microsoft.Data.Analysis;
using System.Text.Json;
using squalor.DataBall;

namespace squalor.DataBall.Tests
{
    public class DataBallTests
    {
        [Fact]
        public void Constructor_WithConfig_LoadsMetadataAndColumns()
        {
            var configJson = @"
            {
                ""metadata"": { ""key"": ""value"" },
                ""columns"": { ""Id"": ""int"", ""Name"": ""string"" },
                ""relationships"": [ { ""trigger"": ""Id"", ""reset"": [ ""Name"" ] } ]
            }";
            var configPath = "testconfig.json";
            File.WriteAllText(configPath, configJson);

            var db = new DataBall(configPath);

            Assert.True(db.Metadata.ContainsKey("key"));
            Assert.Equal("value", db.Metadata["key"]);
            Assert.Equal(2, db.Data.Columns.Count);
            Assert.Equal(typeof(int), db.Data.Columns["Id"].DataType);
            Assert.Equal(typeof(string), db.Data.Columns["Name"].DataType);

            File.Delete(configPath);
        }

        [Fact]
        public void InitializeRow_PropagatesFromLastRow()
        {
            var db = new DataBall();
            db.AddColumn<int>("Id", new int[] { 1 });
            db.AddColumn("Name", new string[] { "Test" }); // Use non-generic for string

            db.InitializeRow();
            db.ModifyField("Id", 2);
            db.Roll();

            Assert.Equal(2, db.Data.Rows.Count);
            Assert.Equal(2, db.Data.Columns["Id"][1]);
            Assert.Equal("Test", db.Data.Columns["Name"][1]);
        }

        // Add non-generic AddColumn for string
        // In DataBall.cs, add:
        public void AddColumn(string name, IEnumerable<string> values)
        {
            var column = new StringDataFrameColumn(name, values);
            Data.Columns.Add(column);
            Logger.Debug($"Added string column {name}");
        }

        // Similar for other tests...
        [Fact]
        public void Roll_AppliesRelationships_ResetIfTriggerChanged()
        {
            var configJson = @"
            {
                ""columns"": { ""Id"": ""int"", ""Name"": ""string"", ""Value"": ""int"" },
                ""relationships"": [ { ""trigger"": ""Id"", ""reset"": [ ""Value"" ] } ]
            }";
            var configPath = "testconfig.json";
            File.WriteAllText(configPath, configJson);
            var db = new DataBall(configPath);
            db.InitializeRow(new Dictionary<string, object?> { { "Id", 1 }, { "Name", "Test" }, { "Value", 100 } });
            db.Roll();

            db.InitializeRow();
            db.ModifyField("Id", 2);
            db.Roll();

            Assert.Equal(2, db.Data.Rows.Count);
            Assert.Equal(2, db.Data.Columns["Id"][1]);
            Assert.Null(db.Data.Columns["Value"][1]);

            db.InitializeRow();
            db.ModifyField("Id", 3);
            db.ModifyField("Value", 200);
            db.Roll();

            Assert.Equal(200, db.Data.Columns["Value"][2]);

            File.Delete(configPath);
        }

        // Update other tests similarly, using AddColumn for string types without <T>
    }
}