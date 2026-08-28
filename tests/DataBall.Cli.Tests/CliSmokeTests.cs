using System.IO;
using squalor.DataBall.Cli;
using Xunit;

namespace squalor.DataBall.Cli.Tests;

public class CliSmokeTests
{
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
            Assert.Contains("Site", info.StdOut);
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
        Assert.Contains("import", result.StdOut);
        Assert.Contains("export", result.StdOut);
        Assert.Contains("bounce", result.StdOut);
        Assert.Contains("squish", result.StdOut);
        Assert.Contains("query", result.StdOut);
        Assert.Contains("info", result.StdOut);
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
            Assert.True(Directory.Exists(lab1), lab1);
            Assert.True(Directory.Exists(lab2), lab2);
            Assert.NotEmpty(Directory.GetFiles(lab1, "*.parquet", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.GetFiles(lab2, "*.parquet", SearchOption.AllDirectories));
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
                  "columns": { "Age": "int" }
                }
                """);

            Assert.Equal(0, (await Run("import", csv, "-c", config, "-o", ball)).Exit);

            var queried = await Run("query", ball, "SELECT Name, Age FROM data ORDER BY Name");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Name\tAge\nAlice\t30\nBob\t25\n", queried.StdOut);

            var types = await Run("query", ball, "SELECT typeof(Age) AS t FROM data LIMIT 1");
            Assert.Equal(0, types.Exit);
            Assert.Contains("INT", types.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Export_Sqlite_RoundTripViaQuery()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            var sqlite = Path.Combine(dir, "out.sqlite");
            File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");

            Assert.Equal(0, (await Run("export", csv, sqlite)).Exit);

            var queried = await Run("query", sqlite, "SELECT Name FROM data ORDER BY Name");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Name\nAlice\nBob\n", queried.StdOut);
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
            Assert.Contains("Metadata:", info.StdOut);
            Assert.Contains("Site", info.StdOut);
            Assert.Contains("Meas", info.StdOut);
            Assert.Contains("Rows: 0", info.StdOut);
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
