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
            db.AddColumn("Name", new string[] { "Test" });

            db.InitializeRow();
            db.ModifyField("Id", 2);
            db.Roll();

            Assert.Equal(2, db.Data.Rows.Count);
            Assert.Equal(2, db.Data.Columns["Id"][1]);
            Assert.Equal("Test", db.Data.Columns["Name"][1]);
        }

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

        [Fact]
        public void AddColumn_AddsNewColumn()
        {
            var db = new DataBall();
            db.AddColumn<int>("Id", Enumerable.Range(1, 5));

            Assert.Single(db.Data.Columns);
            Assert.Equal(5, db.Data.Rows.Count);
            Assert.Equal(3, db.Data.Columns["Id"][2]);
        }

        [Fact]
        public void RemoveColumn_RemovesColumn()
        {
            var db = new DataBall();
            db.AddColumn<int>("Id", new int[0]);
            db.AddColumn("Name", new string[0]);

            db.RemoveColumn("Name");

            Assert.Single(db.Data.Columns);
            Assert.Equal("Id", db.Data.Columns[0].Name);
        }

        [Fact]
        public void ImportFromCsv_AppendsData()
        {
            var csvPath = "test.csv";
            File.WriteAllText(csvPath, "Id,Name\n1,Test");
            var db = new DataBall();

            db.ImportFromCsv(csvPath, append: false);

            Assert.Equal(1, db.Data.Rows.Count);
            Assert.Equal("1", db.Data.Columns["Id"][0]);

            File.Delete(csvPath);
        }

        [Fact]
        public void ExportToParquet_WritesFile()
        {
            var db = new DataBall();
            db.AddColumn<int>("Id", new[] { 1 });
            var path = "test.parquet";

            db.ExportToParquet(path);

            Assert.True(File.Exists(path));
            File.Delete(path);
        }

        [Fact]
        public void MergeOrAppend_AlignsSchemas()
        {
            var db = new DataBall();
            db.AddColumn<int>("Id", new[] { 1 });
            var df = new DataFrame();
            df.Columns.Add(new StringDataFrameColumn("Name", new[] { "Test" }));

            db.MergeOrAppend(df, append: true);

            Assert.Equal(2, db.Data.Columns.Count);
            Assert.Equal(1, db.Data.Rows.Count);
            Assert.Null(db.Data.Columns["Name"][0]);
        }

        [Fact]
        public void Save_ToBallFile()
        {
            var db = new DataBall();
            db.AddColumn("Category", new[] { "A", "B", "A" });
            db.AddColumn<int>("Value", new[] { 1, 2, 3 });
            var path = "test";

            db.Save(path, new[] { "Category" });

            var ballPath = "test.ball";
            Assert.True(File.Exists(ballPath));

            var db2 = new DataBall();
            db2.ImportFromDataBall(ballPath);
            Assert.Equal(3, db2.Data.Rows.Count);

            File.Delete(ballPath);
        }
    }
}