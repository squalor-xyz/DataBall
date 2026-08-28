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
    public async Task Query_MissingFile_NonZero()
    {
        var result = await Run("query", "/no/such.csv", "SELECT 1");
        Assert.Equal(1, result.Exit);
        Assert.True(
            result.StdErr.Contains("File not found") || result.StdErr.Contains("/no/such.csv"),
            result.StdErr);
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
