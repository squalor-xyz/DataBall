# DataBall

Apache-2.0 .NET library and `databall` CLI for **test-executive / ATE measurement tables**. Capture sequential test rows, propagate slowly changing setup fields, reset dependents from config relationships, store true constants in metadata (Bounce), and import/export CSV, Parquet, zip/tar of CSV, and `.ball`.

## Engine (DuckDB)

The working store is **in-memory DuckDB** via `DuckDB.NET.Data.Full` 1.5.5 (`Data Source=:memory:`). The package ships **native RID binaries** (`libduckdb` / `duckdb.dll`) for win-x64, win-arm64, osx-x64, osx-arm64, linux-x64, and linux-arm64. This is **not** a pure-managed library.

Callers do not need SQL. `Query(string sql)` is an escape hatch against table `"data"`.

## Install

Packages are not published to nuget.org from this repository. Use the package IDs below from a feed you control, or pack locally.

```bash
# Library
dotnet add package squalor.DataBall

# CLI
dotnet tool install --global squalor.DataBall.Cli
```

Local pack from this tree:

```bash
dotnet pack src/DataBall/DataBall.csproj -c Release -o artifacts
dotnet pack src/DataBall.Cli/DataBall.Cli.csproj -c Release -o artifacts
dotnet add package squalor.DataBall --source ./artifacts
dotnet tool install --global squalor.DataBall.Cli --add-source ./artifacts
```

Requires a released **.NET 10** SDK (`global.json` pins `10.0.400`, `rollForward: latestFeature`).

## Supported formats

| Format | Extensions | Import | Export |
|---|---|---|---|
| CSV | `.csv` | yes | yes |
| Parquet | `.parquet` | yes | yes (hive partition via Bounce/Squish path+cols) |
| Archive of CSV | `.zip`, `.tar`, `.tar.gz`, `.tgz`, `.tar.xz`, `.txz` | yes | `.zip` / `.tar` / `.tar.gz` / `.tgz` only |
| `.ball` | `.ball` | yes | yes |

## Library usage

```csharp
using squalor.DataBall;
using squalor.DataBall.Export;

using var db = new DataBall("config.json");

db.AddColumn("Name", new[] { "Alice", "Bob" });
db.AddColumn<int>("Age", new[] { 30, 25 });

db.SetMetadata("Source", "TestData");

await db.Bounce();
await db.ExportAsync("output.csv", ExportType.Csv);
// equivalent sync export:
db.Roll(ExportType.Csv, "output.csv");

await db.ImportAsync("input.csv", new ImportOptions { Append = true });
await db.SaveAsync("session.ball");
```

`Metadata` is a snapshot; write with `SetMetadata`. `ImportAsync` detects format by extension (`Append` defaults to `false`). `SaveAsync` writes `ExportType.Ball`. Dispose with `using`; an uncommitted pending row is discarded.

### Config

```json
{
  "metadata": { "Version": "1.0", "Source": "TestData" },
  "columns": {
    "Name": "string",
    "Age": "int",
    "Date": "datetime"
  },
  "relationships": [
    { "trigger": "Name", "reset": ["Age", "Date"] }
  ]
}
```

Column types: `int`, `long`, `float`, `double`, `bool`, `datetime`, `string`. Relationship JSON uses `"trigger"` / `"reset"` (Pascal `TriggerField` / `ResetFields` also binds). Logging is constructor-injected `ILogger`, default `NullLogger`.

A config file is an **overlay** on native defaults (`Config.CreateDefaults`). CSV headers are parsed in order `{name}({unit})`, `{name}_{unit}` (only if the suffix is a known unit), then `{name}` — so `EVM(dB)` and `I_Total(A)` become columns `EVM` and `I_Total` with types from the unit table (`dB`/`A` → `double`; `id`/`ID` → `long`). `{name}_{unit}` does not split `I_Total`. Roles: `parameters` or lists `stimulus` / `classification`; default role is `meas`. Names in `metadataFields` (defaults: Lot, Tester, Program) move to the metadata table when constant (`metadataPolicy`: `requireConstant`, `first`, or `bounce`). `columns` still overrides type per canonical name. Overlay JSON may also include `units` (with `aliases`) and `csv.headerPatterns`.

### Row builder (`CommitRow`)

`CommitRow` commits the pending row. `Roll` is export, not a row commit.

```csharp
using var db = new DataBall("config.json");
db.InitializeRow(new Dictionary<string, object?>
{
    ["Name"] = "Alice",
    ["Age"] = 30,
});
db.CommitRow();

db.InitializeRow();
db.ModifyField("Name", "Bob"); // trigger "Name" resets Age/Date unless modified
db.CommitRow();
```

`InitializeRow` copies the last committed row when one exists. Changing a trigger field resets dependents that were not explicitly modified.

### Bounce and Squish

Bounce moves **strict constants** (exactly one distinct non-null value **and no nulls**) into `Metadata`, drops those columns, then `DISTINCT` remaining rows. Mixed or all-null columns stay. Partition columns are not extracted. If the last remaining constant is extracted, table `"data"` is dropped (DuckDB cannot store a 0-column table).

Squish is the same call as Bounce. Hive-partitioned Parquet is written only when both a directory path and partition columns are supplied:

```csharp
await db.Squish("hive", new[] { "Site" });
```

This is not a matrix rewrite. DuckDB already dictionary-encodes repeats.

### Query

```csharp
var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");
```

## CLI

```
databall import  <input> -o <output> [--append] [--config]
databall export  <input> <output> [--format csv|parquet|ball|archive]
databall bounce  <input> [output]
databall squish  <input> [output] [--partition col,...]
databall query   <file> <sql>
databall info    <file>
```

```bash
databall import measurements.csv -o session.ball
databall import more.csv -o session.ball --append
databall export session.ball people.parquet
databall bounce session.ball compact.ball
databall squish session.ball hive --partition Site
databall query session.ball "SELECT * FROM data LIMIT 10"
databall info session.ball
```

`bounce` without an output path writes a sibling `.ball`. `query` prints TSV to stdout.

## .ball format

A `.ball` file is a ZIP of `data.parquet` + `metadata.json` (+ optional `config.json` when column types or relationships exist). Portable without DuckDB: unzip and read the parquet. DuckDB is the working engine, not the interchange file.

## Platforms

Windows (win-x64 required; win-arm64 included) and macOS (osx-x64 / osx-arm64 required) are supported targets. Linux (linux-x64, linux-arm64) is included and tested in CI.

## Troubleshooting

Debugging a process that uses DuckDB.NET can throw `AccessViolationException` because the debugger touches native memory; this is documented upstream (Giorgi/DuckDB.NET “Known Issues”) and is not a DataBall bug.

SharpCompress 0.40: stay on this version so tar.gz export keeps working. `.tar.xz` import works; `.tar.xz` export does not (XZ is decompress-only in 0.40).

## Historical docs

`docs/archive/` holds pre-rebuild proposals (Polars, Arrow, hierarchical TestData). They do not describe the shipped API.

## License

Apache License 2.0. See [LICENSE](LICENSE) for details. (1.0.0 was MPL-2.0; this tree is 1.1.0 Apache-2.0.)

## Repository

https://github.com/squalor-xyz/DataBall
