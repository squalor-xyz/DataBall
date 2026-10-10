// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Expected an absolute path to an existing DuckDB file");
    return 2;
}

var builder = new DuckDBConnectionStringBuilder { DataSource = args[0] };
builder["ACCESS_MODE"] = "READ_ONLY";
var readOnlyRequested = string.Equals(Convert.ToString(builder["ACCESS_MODE"], CultureInfo.InvariantCulture), "READ_ONLY", StringComparison.OrdinalIgnoreCase);
if (!readOnlyRequested)
{
    Console.Error.WriteLine("Trial helper must request a read-only open");
    return 2;
}

using var connection = new DuckDBConnection(builder.ConnectionString);
try
{
    connection.Open();
}
catch (DuckDBException ex)
{
    // A DuckDB open failure is a measured outcome, not a harness failure.
    Console.WriteLine(JsonSerializer.Serialize(new ProbeResult(Environment.ProcessId, readOnlyRequested, false, null, null, ex.Message)));
    return 0;
}

using var command = connection.CreateCommand();
command.CommandText = "SELECT current_setting('access_mode')";
var mode = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
if (!string.Equals(mode, "read_only", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Trial helper opened in unexpected mode: {mode}");
    return 2;
}
command.CommandText = "SELECT COUNT(*) FROM data";
var rows = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
Console.WriteLine(JsonSerializer.Serialize(new ProbeResult(Environment.ProcessId, readOnlyRequested, true, mode, rows, null)));
return 0;

internal sealed record ProbeResult(int ProcessId, bool ReadOnlyRequested, bool Success, string? AccessMode, long? Rows, string? Error);
