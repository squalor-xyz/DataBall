// SPDX-License-Identifier: Apache-2.0
using System.IO;
using squalor.DataBall;
using squalor.DataBall.Cli;
using Xunit;

namespace squalor.DataBall.Cli.Tests;

public class CliSmokeTests
{
    [Fact]
    public async Task Query_AutoStore_UsesInputSize()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "cli-engine", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var csv = Path.Combine(dir, "input.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var app = typeof(CommandFactory).Assembly.GetType("squalor.DataBall.Cli.CliApp")!;
            var query = app.GetMethod("QueryAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var task = (Task<int>)query.Invoke(null, new object[]
            {
                csv, "SELECT (path IS NULL OR path = '') AS value FROM duckdb_databases() WHERE database_name = current_database()",
                stdout, stderr, false, new EngineOptions { InMemoryMaxBytes = 1, TempDirectory = dir }
            })!;
            Assert.Equal(0, await task);
            Assert.Equal("value\nFalse\n", stdout.ToString().Replace("\r\n", "\n"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("--threads", "3", "threads", "3")]
    [InlineData("--temp-dir", "{temp}", "temp_directory", "{temp}")]
    [InlineData("--store", "file", "store", "False")]
    public async Task Query_EngineOptions_Arrive(string flag, string value, string setting, string expected)
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "input.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");
            value = value.Replace("{temp}", dir);
            expected = expected.Replace("{temp}", dir);
            var sql = setting == "store"
                ? "SELECT (path IS NULL OR path = '') AS value FROM duckdb_databases() WHERE database_name = current_database()"
                : $"SELECT current_setting('{setting}') AS value";
            var result = await Run("query", csv, sql, flag, value);
            Assert.Equal(0, result.Exit);
            Assert.Equal($"value\n{expected}\n", result.StdOut);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("import")]
    [InlineData("export")]
    [InlineData("query")]
    [InlineData("info")]
    public async Task Command_InvalidMemoryLimit_Arrives(string command)
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "input.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");
            var args = command switch
            {
                "import" => new[] { command, csv, "-o", Path.Combine(dir, "out.ball") },
                "export" => new[] { command, csv, Path.Combine(dir, "out.csv") },
                "query" => new[] { command, csv, "SELECT 1" },
                _ => new[] { command, csv }
            };
            var result = await Run(args.Concat(new[] { "--memory-limit", "invalid" }).ToArray());
            Assert.Equal(1, result.Exit);
            Assert.Contains("memory", result.StdErr, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ImportCsv_ThenQuery_PrintsTsv()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            var ball = Path.Combine(dir, "out.ball");
            File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");

            var imported = await Run("import", csv, "-o", ball);
            Assert.Equal(0, imported.Exit);

            var queried = await Run("query", ball, "SELECT Name, Age FROM data ORDER BY Name");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Name\tAge\nAlice\t30\nBob\t25\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Info_AfterImport_PrintsRowsAndColumns()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            var ball = Path.Combine(dir, "out.ball");
            File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");

            Assert.Equal(0, (await Run("import", csv, "-o", ball)).Exit);

            var info = await Run("info", ball);
            Assert.Equal(0, info.Exit);
            Assert.Contains("Format: Ball", info.StdOut);
            Assert.Contains("Rows: 2", info.StdOut);
            Assert.Contains("Name", info.StdOut);
            Assert.Contains("Age", info.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Bounce_ExtractsConstant_InfoShowsMetadata()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "meas.csv");
            var ball = Path.Combine(dir, "out.ball");
            File.WriteAllText(csv, "Site,Meas\nA,1\nA,2\n");

            Assert.Equal(0, (await Run("bounce", csv, ball)).Exit);

            var info = await Run("info", ball);
            Assert.Equal(0, info.Exit);
            Assert.Contains("Metadata:", info.StdOut);
            Assert.Contains("Site: A", info.StdOut);
            Assert.Contains("Columns:", info.StdOut);
            Assert.Contains("Meas", info.StdOut);

            var queried = await Run("query", ball, "SELECT Meas FROM data ORDER BY Meas");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Meas\n1\n2\n", queried.StdOut);
            Assert.DoesNotContain("Site", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Export_Parquet_RoundTripViaQuery()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            var parquet = Path.Combine(dir, "out.parquet");
            File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");

            Assert.Equal(0, (await Run("export", csv, parquet)).Exit);

            var queried = await Run("query", parquet, "SELECT Name FROM data ORDER BY Name");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Name\nAlice\nBob\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Help_ListsAllCommands()
    {
        var result = await Run("--help");
        Assert.Equal(0, result.Exit);
        Assert.Matches(@"(?m)^\s+import\b", result.StdOut);
        Assert.Matches(@"(?m)^\s+export\b", result.StdOut);
        Assert.Matches(@"(?m)^\s+bounce\b", result.StdOut);
        Assert.Matches(@"(?m)^\s+squish\b", result.StdOut);
        Assert.Matches(@"(?m)^\s+query\b", result.StdOut);
        Assert.Matches(@"(?m)^\s+info\b", result.StdOut);
    }

    [Fact]
    public async Task Import_Append_AddsRows()
    {
        var dir = TempDir();
        try
        {
            var first = Path.Combine(dir, "first.csv");
            var second = Path.Combine(dir, "second.csv");
            var ball = Path.Combine(dir, "out.ball");
            File.WriteAllText(first, "Name,Age\nAlice,30\nBob,25\n");
            File.WriteAllText(second, "Name,Age\nCarol,40\n");

            Assert.Equal(0, (await Run("import", first, "-o", ball)).Exit);
            Assert.Equal(0, (await Run("import", second, "-o", ball, "--append")).Exit);

            var queried = await Run("query", ball, "SELECT COUNT(*) AS c FROM data");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("c\n3\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Bounce_DefaultOutput_WritesSiblingBall()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");

            Assert.Equal(0, (await Run("bounce", csv)).Exit);
            var ball = Path.Combine(dir, "people.ball");
            Assert.True(File.Exists(ball), ball);

            var queried = await Run("query", ball, "SELECT Name FROM data ORDER BY Name");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Name\nAlice\nBob\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Squish_Partition_WritesHiveDirs()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            var hive = Path.Combine(dir, "hiveDir");
            File.WriteAllText(csv, "Site,Meas\nLab1,1\nLab1,2\nLab2,3\n");

            Assert.Equal(0, (await Run("squish", csv, hive, "--partition", "Site")).Exit);

            var lab1 = Path.Combine(hive, "Site=Lab1");
            var lab2 = Path.Combine(hive, "Site=Lab2");
            using (var imported = new DataBall())
            {
                await imported.ImportAsync(Directory.GetFiles(lab1, "*.parquet", SearchOption.AllDirectories)[0]);
                var rows = imported.Query("SELECT Site FROM data");
                Assert.NotEmpty(rows);
                Assert.All(rows, r => Assert.Equal("Lab1", r["Site"]?.ToString()));
            }

            using (var imported = new DataBall())
            {
                await imported.ImportAsync(Directory.GetFiles(lab2, "*.parquet", SearchOption.AllDirectories)[0]);
                var row = Assert.Single(imported.Query("SELECT Site FROM data"));
                Assert.Equal("Lab2", row["Site"]?.ToString());
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Import_HiveDir_ThenQuery()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            var hive = Path.Combine(dir, "hiveDir");
            var ball = Path.Combine(dir, "out.ball");
            File.WriteAllText(csv, "Site,Meas\nLab1,1\nLab1,2\nLab2,3\n");

            Assert.Equal(0, (await Run("squish", csv, hive, "--partition", "Site")).Exit);
            Assert.Equal(0, (await Run("import", hive, "-o", ball)).Exit);

            var queried = await Run("query", ball, "SELECT Site, Meas FROM data ORDER BY Meas");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Site\tMeas\nLab1\t1\nLab1\t2\nLab2\t3\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Import_WithConfig_AppliesColumnType()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            var config = Path.Combine(dir, "config.json");
            var ball = Path.Combine(dir, "out.ball");
            File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");
            File.WriteAllText(config, """
                {
                  "metadata": { "Operator": "Ada" },
                  "columns": { "Age": "int" }
                }
                """);

            Assert.Equal(0, (await Run("import", csv, "-c", config, "-o", ball)).Exit);

            var queried = await Run("query", ball, "SELECT Name, Age FROM data ORDER BY Name");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Name\tAge\nAlice\t30\nBob\t25\n", queried.StdOut);

            var info = await Run("info", ball);
            Assert.Equal(0, info.Exit);
            Assert.Contains("Operator", info.StdOut);
            Assert.Contains("Ada", info.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Export_Ball_ThenInfo()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            var ball = Path.Combine(dir, "out.ball");
            File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");

            Assert.Equal(0, (await Run("export", csv, ball)).Exit);

            var info = await Run("info", ball);
            Assert.Equal(0, info.Exit);
            Assert.Contains("Format: Ball", info.StdOut);
            Assert.Contains("Rows: 2", info.StdOut);
            Assert.Contains("Name", info.StdOut);
            Assert.Contains("Age", info.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Squish_WithoutPartition_WritesSiblingBall()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");

            Assert.Equal(0, (await Run("squish", csv)).Exit);
            var ball = Path.Combine(dir, "in.ball");
            Assert.True(File.Exists(ball), ball);

            var queried = await Run("query", ball, "SELECT Name FROM data ORDER BY Name");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Name\nAlice\nBob\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Query_ZeroRows_PrintsNothing()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");

            var queried = await Run("query", csv, "SELECT Name FROM data WHERE 1=0");
            Assert.Equal(0, queried.Exit);
            Assert.Equal(string.Empty, queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Query_NullCell_EmptyTsvField()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            File.WriteAllText(csv, "Name,Age,City\nAlice,30,NYC\nBob,,LA\n");

            var queried = await Run("query", csv, "SELECT Name, Age, City FROM data ORDER BY Name");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Name\tAge\tCity\nAlice\t30\tNYC\nBob\t\tLA\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Info_AfterExtractingAllConstants()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "meas.csv");
            var ball = Path.Combine(dir, "out.ball");
            File.WriteAllText(csv, "Site,Meas\nA,1\n");

            Assert.Equal(0, (await Run("bounce", csv, ball)).Exit);

            var info = await Run("info", ball);
            Assert.Equal(0, info.Exit);
            Assert.Contains("Rows: 0", info.StdOut);
            Assert.Contains("Metadata:", info.StdOut);
            Assert.Contains("Site: A", info.StdOut);
            Assert.Contains("Meas: 1", info.StdOut);
            Assert.Contains("Columns:\nMetadata:", info.StdOut);
            Assert.DoesNotContain("  _ (", info.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Export_FormatOption_Csv()
    {
        var dir = TempDir();
        try
        {
            var input = Path.Combine(dir, "people.csv");
            var output = Path.Combine(dir, "out.csv");
            File.WriteAllText(input, "Name,Age\nAlice,30\nBob,25\n");

            Assert.Equal(0, (await Run("export", input, output, "--format", "csv")).Exit);
            var text = File.ReadAllText(output).Replace("\r\n", "\n");
            Assert.StartsWith("Name", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Import_MemoryLimitOption_Applied()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "input.csv");
            var ball = Path.Combine(dir, "output.ball");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");
            var invalid = await Run("import", csv, "-o", ball, "--memory-limit", "invalid");
            Assert.Equal(1, invalid.Exit);
            Assert.Contains("memory", invalid.StdErr, StringComparison.OrdinalIgnoreCase);
            var valid = await Run("import", csv, "-o", ball, "--memory-limit", "1GB", "--threads", "2", "--temp-dir", dir);
            Assert.Equal(0, valid.Exit);
            Assert.True(File.Exists(ball));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static async Task<(int Exit, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CommandFactory.InvokeAsync(args, stdout, stderr);
        return (exit, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "databall-pr6", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
