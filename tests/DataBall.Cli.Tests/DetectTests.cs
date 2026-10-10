// SPDX-License-Identifier: Apache-2.0
using System.Runtime.CompilerServices;
using squalor.DataBall;
using Xunit;

namespace squalor.DataBall.Cli.Tests;

public class DetectTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "detect-cli-" + Guid.NewGuid().ToString("N"));

    public DetectTests()
    {
        Directory.CreateDirectory(_root);
        DataBall.ClearHandlers();
        SetRoots(_root, _root);
    }

    private static void SetRoots(string? home, string? local)
        => DataBall.SetFileTypeRootsForTests(home, local);

    [ModuleInitializer]
    internal static void UseEmptyRoots()
    {
        var empty = Path.Combine(AppContext.BaseDirectory, "no-file-types");
        SetRoots(empty, empty);
    }

    public void Dispose()
    {
        DataBall.ClearHandlers();
        UseEmptyRoots();
        Directory.Delete(_root, true);
    }

    private async Task<(int Exit, string Output, string Error)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CommandFactory.InvokeAsync(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private string Csv()
    {
        var path = Path.Combine(_root, "input.csv");
        File.WriteAllText(path, "Id\n1\n");
        return path;
    }

    private string Types(params string[] ids)
    {
        var dir = Path.Combine(_root, "types");
        Directory.CreateDirectory(dir);
        foreach (var id in ids)
            File.WriteAllText(Path.Combine(dir, id + ".json"), """{"id":"$id","version":1,"detect":{"conditions":[{"extension":[".csv"]}]}}""".Replace("$id", id));
        return dir;
    }

    [Fact]
    public async Task Detect_DelegatingUnknownExtension_ExitCode1()
    {
        var input = Path.Combine(_root, "feed.txt");
        File.WriteAllText(input, "Id\n1\n");
        var types = Types("lab");
        File.WriteAllText(Path.Combine(types, "lab.json"), """{"id":"lab","version":1,"detect":{"conditions":[{"extension":[".txt"]}]}}""");
        Assert.Equal(1, (await Run("detect", input, "--types", types)).Exit);
    }

    [Fact]
    public async Task Detect_MissingTypesDirectory_ContextualError()
    {
        var missing = Path.Combine(_root, "missing");
        Assert.Contains($"File type directory not found: '{missing}'", (await Run("detect", Csv(), "--types", missing)).Error);
    }

    [Fact]
    public async Task Detect_Explain_PrintsScoresAndReasons()
    {
        var result = await Run("detect", Csv(), "--types", Types("lab"), "--explain");
        Assert.Equal(0, result.Exit);
        Assert.Contains("Match", result.Output);
        Assert.Contains("lab", result.Output);
        Assert.Contains("score=1", result.Output);
        Assert.Contains("minScore=0.6", result.Output);
        Assert.Contains("priority=0", result.Output);
        Assert.Contains("extension", result.Output);
        Assert.Contains("Generic", result.Output);
    }

    [Fact]
    public async Task Detect_Ambiguous_ExitCode1()
    {
        var result = await Run("detect", Csv(), "--types", Types("first", "second"), "--explain");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Ambiguous", result.Output);
        Assert.Contains("first", result.Output);
        Assert.Contains("second", result.Output);
    }

    [Fact]
    public async Task Detect_Generic_ExitCode0()
    {
        var result = await Run("detect", Csv(), "--explain");
        Assert.Equal(0, result.Exit);
        Assert.Contains("Generic", result.Output);
        Assert.Contains("CSV", result.Output);
    }
}
