// SPDX-License-Identifier: Apache-2.0
using System.IO;
using squalor.DataBall.Cli;
using Xunit;

namespace squalor.DataBall.Cli.Tests;

/// <summary>db-24: <c>import</c> takes several inputs, a glob, <c>--recursive</c> and <c>--skip-bad</c>.</summary>
public class CliImportManyTests
{
    [Fact]
    public async Task Import_ManyInputs_Recursive_SkipBad()
    {
        var dir = TempDir();
        var outDir = TempDir();
        try
        {
            Write(dir, "a.csv", "id\n1\n");
            Write(dir, Path.Combine("sub", "b.csv"), "id\n2\n");
            Write(dir, "bad.parquet", "not a parquet file");
            Write(dir, Path.Combine("_temporary", "0", "stray.csv"), "id\n9\n");
            var ball = Path.Combine(outDir, "out.ball");

            var imported = await Run("import", dir, "--recursive", "--skip-bad", "-o", ball);

            Assert.Equal(0, imported.Exit);
            Assert.Contains("bad.parquet", imported.StdErr);
            var queried = await Run("query", ball, "SELECT id FROM data ORDER BY id");
            Assert.Equal("id\n1\n2\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public async Task Import_Glob_OpensEveryMatch()
    {
        var dir = TempDir();
        var outDir = TempDir();
        try
        {
            Write(dir, "a1.csv", "id\n1\n");
            Write(dir, "a2.csv", "id\n2\n");
            Write(dir, "other.txt", "not data");
            var ball = Path.Combine(outDir, "out.ball");

            var imported = await Run("import", Path.Combine(dir, "a*.csv"), "-o", ball);

            Assert.Equal(0, imported.Exit);
            var queried = await Run("query", ball, "SELECT id FROM data ORDER BY id");
            Assert.Equal("id\n1\n2\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public async Task Import_SingleInput_KeepsTheSamePath()
    {
        var dir = TempDir();
        try
        {
            var csv = Write(dir, "people.csv", "Name,Age\nAlice,30\n");
            var ball = Path.Combine(dir, "out.ball");

            var imported = await Run("import", csv, "-o", ball);

            Assert.Equal(0, imported.Exit);
            Assert.Equal(string.Empty, imported.StdErr);
            var queried = await Run("query", ball, "SELECT Name FROM data");
            Assert.Equal("Name\nAlice\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Import_SingleUnknownInput_KeepsTodaysError()
    {
        var dir = TempDir();
        try
        {
            var notes = Write(dir, "notes.txt", "not data");

            var imported = await Run("import", notes, "-o", Path.Combine(dir, "out.ball"));

            Assert.Equal(1, imported.Exit);
            Assert.Equal($"Unknown import format for '{notes}'\n", imported.StdErr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Import_SkipBad_WhenEveryInputIsBad_Fails()
    {
        var dir = TempDir();
        try
        {
            var bad = Write(dir, "bad.parquet", "not a parquet file");
            var ball = Path.Combine(dir, "out.ball");

            var imported = await Run("import", bad, "--skip-bad", "-o", ball);

            Assert.Equal(1, imported.Exit);
            Assert.Contains(bad, imported.StdErr);
            Assert.False(File.Exists(ball));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Every run that opens its inputs together, rather than one plain input, is refused with --append.</summary>
    [Theory]
    [InlineData("several")]
    [InlineData("recursive")]
    [InlineData("skip-bad")]
    [InlineData("glob")]
    public async Task Import_Append_WhenInputsOpenTogether_IsRejected(string shape)
    {
        var dir = TempDir();
        try
        {
            var a = Write(dir, "a.csv", "id\n1\n");
            var b = Write(dir, "b.csv", "id\n2\n");
            var ball = Path.Combine(dir, "out.ball");
            var args = shape switch
            {
                "several" => new[] { "import", a, b, "--append", "-o", ball },
                "recursive" => new[] { "import", dir, "--recursive", "--append", "-o", ball },
                "skip-bad" => new[] { "import", a, "--skip-bad", "--append", "-o", ball },
                _ => new[] { "import", Path.Combine(dir, "*.csv"), "--append", "-o", ball },
            };

            var imported = await Run(args);

            Assert.Equal(1, imported.Exit);
            Assert.Contains("--append takes one plain input", imported.StdErr);
            Assert.Contains("open together", imported.StdErr);
            Assert.DoesNotContain("db-16", imported.StdErr);
            Assert.False(File.Exists(ball));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Import_SoleNativeBall_WithSkipBad_Exports()
    {
        var dir = TempDir();
        var outDir = TempDir();
        try
        {
            var csv = Write(dir, "people.csv", "Name,Age\nAlice,30\n");
            var ball = Path.Combine(dir, "s.ball");
            Assert.Equal(0, (await Run("import", csv, "-o", ball)).Exit);
            var copy = Path.Combine(outDir, "t.ball");

            var imported = await Run("import", ball, "--skip-bad", "-o", copy);

            Assert.Equal(0, imported.Exit);
            Assert.Equal(string.Empty, imported.StdErr);
            var queried = await Run("query", copy, "SELECT Name FROM data");
            Assert.Equal("Name\nAlice\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public async Task Import_AbortOnABadFile_PrintsOneLineNamingIt()
    {
        var dir = TempDir();
        try
        {
            Write(dir, "a.csv", "id\n1\n");
            var bad = Write(dir, "bad.parquet", "not a parquet file");

            var imported = await Run("import", dir, "--recursive", "-o", Path.Combine(dir, "unused.ball"));

            Assert.Equal(1, imported.Exit);
            var line = Assert.Single(imported.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.StartsWith($"Cannot open '{bad}': ", line);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string Write(string dir, string name, string text)
    {
        var path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
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
        var dir = Path.Combine(Path.GetTempPath(), "databall-cli-many", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
