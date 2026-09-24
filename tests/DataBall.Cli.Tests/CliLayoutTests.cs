// SPDX-License-Identifier: Apache-2.0
using System.IO;
using squalor.DataBall.Cli;
using Xunit;

namespace squalor.DataBall.Cli.Tests;

public class CliLayoutTests
{
    [Fact]
    public async Task Info_LayoutBall_ListsTables_AndQueryReadsView()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "meas.csv");
            var config = Path.Combine(dir, "tables.json");
            var ball = Path.Combine(dir, "out.ball");
            File.WriteAllText(csv, "Site,Temp,Meas\nA,25,1\nA,25,2\nB,85,3\n");
            File.WriteAllText(config, """
                { "tables": { "setup": { "kind": "dimension", "columns": ["Site", "Temp"] }, "m": { "kind": "measurements", "columns": ["Meas"] } } }
                """);

            var imported = await Run("import", csv, "-o", ball, "--config", config);
            Assert.Equal(0, imported.Exit);

            var info = await Run("info", ball);
            Assert.Equal(0, info.Exit);
            Assert.Contains("Rows: 3", info.StdOut);
            Assert.Contains("Tables:", info.StdOut);
            Assert.Contains("setup (2 rows)", info.StdOut);
            Assert.Contains("rows (3 rows)", info.StdOut);
            Assert.Contains("m (3 rows)", info.StdOut);

            var queried = await Run("query", ball, "SELECT Site, Temp, Meas FROM data ORDER BY Meas");
            Assert.Equal(0, queried.Exit);
            Assert.Equal("Site\tTemp\tMeas\nA\t25\t1\nA\t25\t2\nB\t85\t3\n", queried.StdOut);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Info_WideBall_HasNoTablesSection()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, "people.csv");
            var ball = Path.Combine(dir, "out.ball");
            File.WriteAllText(csv, "Name,Age\nAlice,30\n");
            Assert.Equal(0, (await Run("import", csv, "-o", ball)).Exit);
            var info = await Run("info", ball);
            Assert.Equal(0, info.Exit);
            Assert.DoesNotContain("Tables:", info.StdOut);
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
        var dir = Path.Combine(Path.GetTempPath(), "databall-cli-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
