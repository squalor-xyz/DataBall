using System.Globalization;
using squalor.DataBall;
using squalor.DataBall.Export;

namespace squalor.DataBall.Cli;

internal static class CliApp
{
    internal static Task<int> ImportAsync(
        string input,
        string output,
        bool append,
        string? configPath,
        TextWriter stderr)
    {
        return RunAsync(stderr, async () =>
        {
            using var db = new DataBall(configPath);
            if (append && File.Exists(output))
            {
                await db.ImportAsync(output);
                await db.ImportAsync(input, new ImportOptions { Append = true });
            }
            else
            {
                await db.ImportAsync(input);
            }

            await db.ExportAsync(output, CliFormat.DetectExportType(output));
            return 0;
        });
    }

    internal static Task<int> ExportAsync(
        string input,
        string output,
        string? format,
        TextWriter stderr)
    {
        return RunAsync(stderr, async () =>
        {
            using var db = new DataBall();
            await db.ImportAsync(input);
            var type = format is null
                ? CliFormat.DetectExportType(output)
                : CliFormat.ParseFormat(format);
            await db.ExportAsync(output, type);
            return 0;
        });
    }

    internal static Task<int> Bounce(
        string input,
        string? output,
        TextWriter stderr)
    {
        return RunAsync(stderr, async () =>
        {
            using var db = new DataBall();
            await db.ImportAsync(input);
            await db.Bounce();
            var dest = string.IsNullOrEmpty(output) ? CliFormat.DefaultBallPath(input) : output;
            RejectIfSamePath(input, dest);
            await db.ExportAsync(dest, CliFormat.DetectExportType(dest));
            return 0;
        });
    }

    internal static Task<int> Squish(
        string input,
        string? output,
        string? partition,
        TextWriter stderr)
    {
        return RunAsync(stderr, async () =>
        {
            using var db = new DataBall();
            await db.ImportAsync(input);
            if (partition is not null)
            {
                var cols = partition.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length == 0)
                    throw new DataBallException("Partition columns are required");
                var dir = string.IsNullOrEmpty(output) ? CliFormat.DefaultSquishDir(input) : output;
                RejectIfSamePath(input, dir);
                await db.Squish(dir, cols);
                return 0;
            }

            await db.Squish();
            var dest = string.IsNullOrEmpty(output) ? CliFormat.DefaultBallPath(input) : output;
            RejectIfSamePath(input, dest);
            await db.ExportAsync(dest, CliFormat.DetectExportType(dest));
            return 0;
        });
    }

    internal static Task<int> QueryAsync(
        string file,
        string sql,
        TextWriter stdout,
        TextWriter stderr)
    {
        return RunAsync(stderr, async () =>
        {
            using var db = new DataBall();
            await db.ImportAsync(file);
            WriteTsv(db.Query(sql), stdout);
            return 0;
        });
    }

    internal static Task<int> InfoAsync(
        string file,
        TextWriter stdout,
        TextWriter stderr)
    {
        return RunAsync(stderr, async () =>
        {
            using var db = new DataBall();
            await db.ImportAsync(file);
            WriteInfo(file, db, stdout);
            return 0;
        });
    }

    private static void RejectIfSamePath(string input, string dest)
    {
        if (CliFormat.SamePath(input, dest))
            throw new DataBallException("Output path is the input path. Pass -o explicitly.");
    }

    private static async Task<int> RunAsync(TextWriter stderr, Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (DataBallException ex)
        {
            stderr.WriteLine(ex.Message);
            if (ex.InnerException is not null)
                stderr.WriteLine(ex.InnerException.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void WriteTsv(IReadOnlyList<Dictionary<string, object?>> rows, TextWriter stdout)
    {
        if (rows.Count == 0)
            return;

        var keys = rows[0].Keys.ToArray();
        stdout.WriteLine(string.Join('\t', keys));
        foreach (var row in rows)
        {
            var cells = new string[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                row.TryGetValue(keys[i], out var value);
                cells[i] = FormatCell(value);
            }

            stdout.WriteLine(string.Join('\t', cells));
        }
    }

    private static string FormatCell(object? value)
    {
        if (value is null)
            return string.Empty;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }

    private static void WriteInfo(string path, DataBall db, TextWriter stdout)
    {
        stdout.WriteLine($"File: {path}");
        stdout.WriteLine($"Format: {CliFormat.DetectExportType(path)}");

        var exists = db.Query("""
            SELECT COUNT(*) AS c FROM information_schema.tables
            WHERE table_schema = 'main' AND table_name = 'data'
            """);
        long rows = 0;
        if (Convert.ToInt64(exists[0]["c"], CultureInfo.InvariantCulture) > 0)
        {
            var count = db.Query("SELECT COUNT(*) AS c FROM \"data\"");
            rows = Convert.ToInt64(count[0]["c"], CultureInfo.InvariantCulture);
        }

        stdout.WriteLine($"Rows: {rows}");
        stdout.WriteLine("Columns:");
        var columns = db.Query("""
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = 'main' AND table_name = 'data'
            ORDER BY ordinal_position
            """);
        foreach (var column in columns)
            stdout.WriteLine($"  {column["column_name"]} ({column["data_type"]})");

        stdout.WriteLine("Metadata:");
        foreach (var pair in db.Metadata.OrderBy(p => p.Key, StringComparer.Ordinal))
            stdout.WriteLine($"  {pair.Key}: {Convert.ToString(pair.Value, CultureInfo.InvariantCulture)}");
    }
}
