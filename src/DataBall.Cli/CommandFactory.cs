using System.CommandLine;

namespace squalor.DataBall.Cli;

public static class CommandFactory
{
    public static RootCommand CreateRootCommand(TextWriter stdout, TextWriter stderr)
    {
        var root = new RootCommand("Import, export, compact, and query DataBall files");

        root.Subcommands.Add(CreateImportCommand(stderr));
        root.Subcommands.Add(CreateExportCommand(stderr));
        root.Subcommands.Add(CreateBounceCommand(stderr));
        root.Subcommands.Add(CreateSquishCommand(stderr));
        root.Subcommands.Add(CreateQueryCommand(stdout, stderr));
        root.Subcommands.Add(CreateInfoCommand(stdout, stderr));
        return root;
    }

    public static async Task<int> InvokeAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var root = CreateRootCommand(stdout, stderr);
        var parseResult = root.Parse(args);
        parseResult.InvocationConfiguration.Output = stdout;
        parseResult.InvocationConfiguration.Error = stderr;
        // Testhost has no process-control handles for SCL's SIGINT registration.
        parseResult.InvocationConfiguration.ProcessTerminationTimeout = null;
        return await parseResult.InvokeAsync();
    }

    private static Command CreateImportCommand(TextWriter stderr)
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
                stderr));
        return command;
    }

    private static Command CreateExportCommand(TextWriter stderr)
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
            Description = "csv, parquet, sqlite, ball, or archive",
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
                stderr));
        return command;
    }

    private static Command CreateBounceCommand(TextWriter stderr)
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
                stderr));
        return command;
    }

    private static Command CreateSquishCommand(TextWriter stderr)
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
                stderr));
        return command;
    }

    private static Command CreateQueryCommand(TextWriter stdout, TextWriter stderr)
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
                stderr));
        return command;
    }

    private static Command CreateInfoCommand(TextWriter stdout, TextWriter stderr)
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
                stderr));
        return command;
    }
}
