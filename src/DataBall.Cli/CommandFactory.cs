// SPDX-License-Identifier: Apache-2.0
using System.CommandLine;

namespace squalor.DataBall.Cli;

public static class CommandFactory
{
    public static RootCommand CreateRootCommand(TextWriter stdout, TextWriter stderr)
    {
        var root = new RootCommand("Import, export, compact, and query DataBall files");
        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Print full exception details",
            Recursive = true,
        };
        root.Options.Add(verboseOption);

        root.Subcommands.Add(CreateImportCommand(stderr, verboseOption));
        root.Subcommands.Add(CreateExportCommand(stderr, verboseOption));
        root.Subcommands.Add(CreateBounceCommand(stderr, verboseOption));
        root.Subcommands.Add(CreateSquishCommand(stderr, verboseOption));
        root.Subcommands.Add(CreateQueryCommand(stdout, stderr, verboseOption));
        root.Subcommands.Add(CreateInfoCommand(stdout, stderr, verboseOption));
        return root;
    }

    public static async Task<int> InvokeAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var root = CreateRootCommand(stdout, stderr);
        var parseResult = root.Parse(args);
        parseResult.InvocationConfiguration.Error = stderr;
        parseResult.InvocationConfiguration.ProcessTerminationTimeout = null;
        parseResult.InvocationConfiguration.Output = parseResult.Errors.Count > 0 ? stderr : stdout;
        return await parseResult.InvokeAsync();
    }

    private static Command CreateImportCommand(TextWriter stderr, Option<bool> verboseOption)
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "Source file",
        };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Destination path",
            Required = true,
        };
        var appendOption = new Option<bool>("--append")
        {
            Description = "Append to an existing output file",
        };
        var configOption = new Option<string?>("--config", "-c")
        {
            Description = "Path to configuration JSON",
        };

        var command = new Command("import", "Import a file into an output DataBall")
        {
            inputArg,
            outputOption,
            appendOption,
            configOption,
        };
        command.SetAction(async (parseResult, _) =>
            await CliApp.ImportAsync(
                parseResult.GetValue(inputArg)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(appendOption),
                parseResult.GetValue(configOption),
                stderr,
                parseResult.GetValue(verboseOption)));
        return command;
    }

    private static Command CreateExportCommand(TextWriter stderr, Option<bool> verboseOption)
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "Source file",
        };
        var outputArg = new Argument<string>("output")
        {
            Description = "Destination path",
        };
        var formatOption = new Option<string?>("--format")
        {
            Description = "csv, parquet, ball, or archive",
        };

        var command = new Command("export", "Export a file to another format")
        {
            inputArg,
            outputArg,
            formatOption,
        };
        command.SetAction(async (parseResult, _) =>
            await CliApp.ExportAsync(
                parseResult.GetValue(inputArg)!,
                parseResult.GetValue(outputArg)!,
                parseResult.GetValue(formatOption),
                stderr,
                parseResult.GetValue(verboseOption)));
        return command;
    }

    private static Command CreateBounceCommand(TextWriter stderr, Option<bool> verboseOption)
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "Source file",
        };
        var outputArg = new Argument<string?>("output")
        {
            Description = "Destination path (defaults to a sibling .ball)",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("bounce", "Extract constants into metadata and write a compacted file")
        {
            inputArg,
            outputArg,
        };
        command.SetAction(async (parseResult, _) =>
            await CliApp.Bounce(
                parseResult.GetValue(inputArg)!,
                parseResult.GetValue(outputArg),
                stderr,
                parseResult.GetValue(verboseOption)));
        return command;
    }

    private static Command CreateSquishCommand(TextWriter stderr, Option<bool> verboseOption)
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "Source file",
        };
        var outputArg = new Argument<string?>("output")
        {
            Description = "Destination path or hive directory",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var partitionOption = new Option<string?>("--partition")
        {
            Description = "Comma-separated columns for hive-partitioned Parquet",
        };

        var command = new Command("squish", "Compact like bounce, optionally writing hive-partitioned Parquet")
        {
            inputArg,
            outputArg,
            partitionOption,
        };
        command.SetAction(async (parseResult, _) =>
            await CliApp.Squish(
                parseResult.GetValue(inputArg)!,
                parseResult.GetValue(outputArg),
                parseResult.GetValue(partitionOption),
                stderr,
                parseResult.GetValue(verboseOption)));
        return command;
    }

    private static Command CreateQueryCommand(TextWriter stdout, TextWriter stderr, Option<bool> verboseOption)
    {
        var fileArg = new Argument<string>("file")
        {
            Description = "File to query",
        };
        var sqlArg = new Argument<string>("sql")
        {
            Description = "SQL to run",
        };

        var command = new Command("query", "Run SQL and print TSV")
        {
            fileArg,
            sqlArg,
        };
        command.SetAction(async (parseResult, _) =>
            await CliApp.QueryAsync(
                parseResult.GetValue(fileArg)!,
                parseResult.GetValue(sqlArg)!,
                stdout,
                stderr,
                parseResult.GetValue(verboseOption)));
        return command;
    }

    private static Command CreateInfoCommand(TextWriter stdout, TextWriter stderr, Option<bool> verboseOption)
    {
        var fileArg = new Argument<string>("file")
        {
            Description = "File to inspect",
        };

        var command = new Command("info", "Print file format, rows, columns, and metadata")
        {
            fileArg,
        };
        command.SetAction(async (parseResult, _) =>
            await CliApp.InfoAsync(
                parseResult.GetValue(fileArg)!,
                stdout,
                stderr,
                parseResult.GetValue(verboseOption)));
        return command;
    }
}
