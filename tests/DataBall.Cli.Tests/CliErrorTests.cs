// SPDX-License-Identifier: Apache-2.0
using System.IO;
using squalor.DataBall.Cli;
using Xunit;

namespace squalor.DataBall.Cli.Tests;

public class CliErrorTests
{
    [Fact]
    public async Task Import_MissingOutput_NonZero()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");

            var result = await Run("import", csv);
            Assert.NotEqual(0, result.Exit);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Import_MissingOutput_WritesUsageToStderr()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");

            var result = await Run("import", csv);
            Assert.NotEqual(0, result.Exit);
            Assert.True(
                result.StdErr.Contains("import", StringComparison.OrdinalIgnoreCase)
                || result.StdErr.Contains("Usage", StringComparison.OrdinalIgnoreCase)
                || result.StdErr.Contains("Required", StringComparison.OrdinalIgnoreCase),
                result.StdErr);
            Assert.DoesNotContain("Usage", result.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Query_MissingFile_NonZero()
    {
        var result = await Run("query", "/no/such.csv", "SELECT 1");
        Assert.Equal(1, result.Exit);
        Assert.Contains("File not found", result.StdErr);
    }

    [Fact]
    public async Task Import_MissingInputFile_NonZero()
    {
        var dir = TempDir();
        try
        {
            var missing = Path.Combine(dir, "no-such.csv");
            var output = Path.Combine(dir, "out.ball");

            var result = await Run("import", missing, "-o", output);
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("File not found", result.StdErr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Export_UnknownFormat_NonZero()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            var output = Path.Combine(dir, "out.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");

            var result = await Run("export", csv, output, "--format", "xyz");
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("Unknown format", result.StdErr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Export_UndetectableExtension_NonZero()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            var output = Path.Combine(dir, "out.dat");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");

            var result = await Run("export", csv, output);
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("Cannot detect format", result.StdErr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Squish_EmptyPartition_NonZero()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            var hive = Path.Combine(dir, "hive");
            File.WriteAllText(csv, "Site,Meas\nLab1,1\n");

            var result = await Run("squish", csv, hive, "--partition", "  ,  ");
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("Partition columns are required", result.StdErr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Query_BadSql_NonZero()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");

            var result = await Run("query", csv, "SELECT nope FROM data");
            Assert.Equal(1, result.Exit);
            Assert.False(string.IsNullOrWhiteSpace(result.StdErr));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Query_BadSql_Verbose_PrintsFullException()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");

            var quiet = await Run("query", csv, "SELECT nope FROM data");
            var verbose = await Run("--verbose", "query", csv, "SELECT nope FROM data");
            Assert.Equal(1, verbose.Exit);
            Assert.True(verbose.StdErr.Length > quiet.StdErr.Length, verbose.StdErr);
            Assert.Contains("at squalor.DataBall", verbose.StdErr, StringComparison.Ordinal);
            Assert.Contains("DuckDB", verbose.StdErr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Bounce_BallInput_NoOutput_DoesNotOverwriteInput()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            var ball = Path.Combine(dir, "people.ball");
            File.WriteAllText(csv, "Name,Age\nAlice,30\nBob,25\n");
            Assert.Equal(0, (await Run("bounce", csv, ball)).Exit);
            var before = File.ReadAllBytes(ball);

            var result = await Run("bounce", ball);
            Assert.Equal(0, result.Exit);
            Assert.True(before.SequenceEqual(File.ReadAllBytes(ball)));
            var sibling = Path.Combine(dir, "people.bounced.ball");
            Assert.True(File.Exists(sibling), sibling);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Squish_DirectoryInput_NoOutput_Refuses()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "in.csv");
            var hive = Path.Combine(dir, "hive");
            File.WriteAllText(csv, "Site,Meas\nLab1,1\nLab2,2\n");
            Assert.Equal(0, (await Run("squish", csv, hive, "--partition", "Site")).Exit);
            Assert.True(Directory.Exists(hive));

            var result = await Run("squish", hive, "--partition", "Site");
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("Pass -o explicitly", result.StdErr);
            Assert.True(Directory.Exists(hive));
            Assert.True(Directory.Exists(Path.Combine(hive, "Site=Lab1")));
            Assert.True(Directory.Exists(Path.Combine(hive, "Site=Lab2")));
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
