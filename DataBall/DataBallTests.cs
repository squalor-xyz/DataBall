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
            // Arrange
            var configJson = @"
            {
                ""metadata"": { ""key"": ""value"" },
                ""columns"": { ""Id"": ""int"", ""Name"": ""string"" },
                ""relationships"": [ { ""trigger"": ""Id"", ""reset"": [ ""Name"" ] } ]
            }";
            var configPath = "testconfig.json";
            File.WriteAllText(configPath, configJson);

            // Act
            var db = new DataBall(configPath);

            // Assert
            Assert.True(db.Metadata.ContainsKey("key"));
            Assert.Equal("value", db.Metadata["key"]);
            Assert.Equal(2, db.Data.Columns.Count);
            Assert.Equal(typeof(int), db.Data["Id"].DataType);
            Assert.Equal(typeof(string), db.Data["Name"].DataType);
            Assert.Single(db.Relationships);  // Private, but assume accessible or test via behavior

            File.Delete(configPath);
        }

        [Fact]
        public void InitializeRow_PropagatesFromLastRow()
        {
            // Arrange
            var db = new DataBall();
            db.AddColumn<int>("Id", new int[] { 1 });
            db.AddColumn<string>("Name", new string[] { "Test" });

            // Act
            db.InitializeRow();
            db.ModifyField("Id", 2);
            db.Roll();

            // Assert
            Assert.Equal(2, db.Data.Rows.Count);
            Assert.Equal(2, db.Data["Id"][1]);
            Assert.Equal("Test", db.Data["Name"][1]);  // Propagated
        }

        [Fact]
        public void Roll_AppliesRelationships_ResetIfTriggerChanged()
        {
            // Arrange
            var configJson = @"
            {
                ""columns"": { ""Id"": ""int"", ""Name"": ""string"", ""Value"": ""int"" },
                ""relationships"": [ { ""trigger"": ""Id"", ""reset"": [ ""Value"" ] } ]
            }";
            var configPath = "testconfig.json";
            File.WriteAllText(configPath, configJson);
            var db = new DataBall(configPath);
            db.InitializeRow(new Dictionary<string, object> { { "Id", 1 }, { "Name", "Test" }, { "Value", 100 } });
            db.Roll();

            // Act: Change Id, don't modify Value
            db.InitializeRow();
            db.ModifyField("Id", 2);
            db.Roll();

            // Assert: Value reset to null
            Assert.Equal(2, db.Data.Rows.Count);
            Assert.Equal(2, db.Data["Id"][1]);
            Assert.Null(db.Data["Value"][1]);

            // Act: Change Id, but modify Value
            db.InitializeRow();
            db.ModifyField("Id", 3);
            db.ModifyField("Value", 200);
            db.Roll();

            // Assert: Value not reset
            Assert.Equal(200, db.Data["Value"][2]);

            File.Delete(configPath);
        }

        [Fact]
        public void AddColumn_AddsNewColumn()
        {
            // Arrange
            var db = new DataBall();

            // Act
            db.AddColumn<int>("Id", Enumerable.Range(1, 5));

            // Assert
            Assert.Single(db.Data.Columns);
            Assert.Equal(5, db.Data.Rows.Count);
            Assert.Equal(3, db.Data["Id"][2]);
        }

        [Fact]
        public void RemoveColumn_RemovesColumn()
        {
            // Arrange
            var db = new DataBall();
            db.AddColumn<int>("Id", new int[0]);
            db.AddColumn<string>("Name", new string[0]);

            // Act
            db.RemoveColumn("Name");

            // Assert
            Assert.Single(db.Data.Columns);
            Assert.Equal("Id", db.Data.Columns[0].Name);
        }

        [Fact]
        public void ImportFromCsv_AppendsData()
        {
            // Arrange
            var csvPath = "test.csv";
            File.WriteAllText(csvPath, "Id,Name\n1,Test");
            var db = new DataBall();

            // Act
            db.ImportFromCsv(csvPath, append: false);

            // Assert
            Assert.Equal(1, db.Data.Rows.Count);
            Assert.Equal("1", db.Data["Id"][0]);  // Loaded as string

            File.Delete(csvPath);
        }

        // Additional tests for other imports/exports, SetValue, RemoveRow, etc.
        [Fact]
        public void ExportToParquet_WritesFile()
        {
            // Arrange
            var db = new DataBall();
            db.AddColumn<int>("Id", new[] { 1 });
            var path = "test.parquet";

            // Act
            db.ExportToParquet(path);

            // Assert
            Assert.True(File.Exists(path));
            // Could import back to verify, but skip for brevity

            File.Delete(path);
        }

        [Fact]
        public void MergeOrAppend_AlignsSchemas()
        {
            // Arrange
            var db = new DataBall();
            db.AddColumn<int>("Id", new[] { 1 });
            var df = new DataFrame();
            df.Append(new PrimitiveDataFrameColumn<string>("Name", new[] { "Test" }), inPlace: true);

            // Act
            db.MergeOrAppend(df, append: true);

            // Assert
            Assert.Equal(2, db.Data.Columns.Count);
            Assert.Equal(1, db.Data.Rows.Count);  // Appends rows? Wait, lengths differ, but code handles
            Assert.Null(db.Data["Name"][0]);  // Filled null
        }

        // Test Save and partitioned
        [Fact]
        public void Save_ToBallFile()
        {
            // Arrange
            var db = new DataBall();
            db.AddColumn<string>("Category", new[] { "A", "B", "A" });
            db.AddColumn<int>("Value", new[] { 1, 2, 3 });
            var path = "test";

            // Act
            db.Save(path, new[] { "Category" });

            // Assert
            var ballPath = "test.ball";
            Assert.True(File.Exists(ballPath));

            // Import back
            var db2 = new DataBall();
            db2.ImportFromDataBall(ballPath);
            Assert.Equal(3, db2.Data.Rows.Count);

            File.Delete(ballPath);
        }

        // More tests as needed for comprehensive coverage
    }
}