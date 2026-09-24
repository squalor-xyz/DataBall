# DataBall

Apache-2.0 .NET library and `databall` CLI for **test-executive / ATE measurement tables**. Capture sequential test rows, propagate slowly changing setup fields, reset dependents from config relationships, store true constants in metadata (Bounce), and import/export CSV, Parquet, zip/tar of CSV, and `.ball`.

## Engine (DuckDB)

The working store is DuckDB via `DuckDB.NET.Data.Full` 1.5.5. Default is **in-memory** (`Data Source=:memory:`). Opt-in file-backed: `new DataBall(databasePath: "session.duckdb")` (named argument; the first ctor string is still config JSON). Dispose does not delete the file. Do not open `catalog.duckdb` as a DataBall session. Interchange remains `.ball`. The package ships **native RID binaries** (`libduckdb` / `duckdb.dll`) for win-x64, win-arm64, osx-x64, osx-arm64, linux-x64, and linux-arm64. This is **not** a pure-managed library.

Callers do not need SQL. `Query(string sql)` is an escape hatch against relation `"data"` (a table, or a view when the config declares `tables`; see [Tables](#tables)).

## Install

Packages are **not on nuget.org**. Do **not** `dotnet nuget push`. There are no API keys in this tree. Pack locally:

```bash
dotnet pack src/DataBall/DataBall.csproj -c Release -o artifacts
dotnet pack src/DataBall.Handlers/DataBall.Handlers.csproj -c Release -o artifacts
dotnet pack src/DataBall.Cli/DataBall.Cli.csproj -c Release -o artifacts
```

Inspect the library nupkg: `lib/net10.0/DataBall.dll`, README, **no `runtimes/`** (DuckDB natives come from `DuckDB.NET.Data.Full` at consumer restore). Inspect the tool nupkg: `tools/net10.0/any/` includes `DataBall.dll` and `runtimes/{linux-x64,linux-arm64,osx,win-x64,win-arm64}/native/`.

Consume from `./artifacts`. Restore still needs nuget.org (or a mirror) for DuckDB.NET, SharpCompress, and logging abstractions:

```bash
dotnet add package squalor.DataBall --source ./artifacts
dotnet tool uninstall -g squalor.DataBall.Cli   # if an older version is already installed
dotnet tool install --global squalor.DataBall.Cli --add-source ./artifacts
databall --help
```

Package IDs: `squalor.DataBall` (library), `squalor.DataBall.Handlers` (lab CSV/translators), `squalor.DataBall.Cli` (global tool `databall`).

A `v*` tag that is an ancestor of `main` packs those three and attaches the nupkgs to a GitHub Release. nuget.org push runs only when the `NUGET_API_KEY` secret is set on the DataBall repo; until then the packages are **not** on nuget.org. Do not claim a nuget.org listing until a tagged release has pushed.

Requires a released **.NET 10** SDK (`global.json` pins `10.0.400`, `rollForward: latestFeature`).

## Supported formats

| Format | Extensions | Import | Export |
|---|---|---|---|
| CSV | `.csv` | yes | yes |
| Parquet | `.parquet` or hive directory | yes (file or Squish hive dir) | yes (hive partition via Bounce/Squish path+cols) |
| Archive of CSV | `.zip`, `.tar`, `.tar.gz`, `.tgz`, `.tar.xz`, `.txz` | yes | `.zip` / `.tar` / `.tar.gz` / `.tgz` only |
| `.ball` | `.ball` | yes | yes |

## Library usage

```csharp
using squalor.DataBall;
using squalor.DataBall.Export;

using var db = new DataBall("config.json");

db.AddColumn("Name", new[] { "Alice", "Bob" });
db.AddColumn<int>("Age", new[] { 30, 25 });
db.AddRows(new[]
{
    new Dictionary<string, object?> { ["Name"] = "Carol", ["Age"] = 40 },
});

db.SetMetadata("Source", "TestData");

await db.Bounce();
await db.ExportAsync("output.csv", ExportType.Csv);
// equivalent sync export:
db.Roll(ExportType.Csv, "output.csv");

await db.ImportAsync("input.csv", new ImportOptions { Append = true });
await db.SaveAsync("session.ball");

using var opened = DataBall.Open("input.csv"); // optional schema overlay: Open(path, "config.json")
```

`Metadata` is a snapshot; write with `SetMetadata`. `ImportAsync` detects format by extension (`Append` defaults to `false`). `SaveAsync` writes `ExportType.Ball`. Dispose with `using`; an uncommitted pending row is discarded.

`DataBall.Open(path, schemaPath?)` creates a new session from a **file**. Generic CSV / Parquet / archive / `.ball` need no handler (`ImportAsync` path). `ImportAsync` also reads a Squish **hive directory** of parquet (`hive_partitioning=false`; partition columns come from the files). Hive import is not `.ball` and does not restore Bounce metadata.

Lab translators live in `squalor.DataBall.Handlers` (not `DuckDbStore`). Primary dialect is **custom CSV** with unit headers (`EVM(dB)`). `LabHandlers.RegisterDefaults()` also registers STDF / Touchstone / production **slots** (Parse throws until a golden file exists). Register with `DataBall.RegisterHandler`; first `CanHandle` match wins. `ClearHandlers` resets the process registry. Do not put parsers in `DuckDbStore`.

Session filter is column predicates pushed to DuckDB (`Eq`, `In`, `Ge`, `Le`, inclusive `Range`) plus optional column projection. `Filter(spec)` does not mutate `"data"`. `ApplyFilter(spec)` is honored by export/save; `ApplyFilter(null)` clears. `Query(sql)` is the unfiltered escape hatch.

`AddRows` inserts a batch with one DuckDB appender. It does not apply trigger/reset (`CommitRow` does). Empty input is a no-op.

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

Column types: `int`, `long`, `float`, `double`, `bool`, `datetime`, `string`. An optional `tables` section declares a multi-table layout ([Tables](#tables)). Types come from this config or from `AddColumn`. Untyped CSV integers stay DuckDB BIGINT / `long` — they are not narrowed from value range. Relationship JSON uses `"trigger"` / `"reset"` (Pascal `TriggerField` / `ResetFields` also binds). Logging is constructor-injected `ILogger`, default `NullLogger`.

A config file is an **overlay** on native defaults (`Config.CreateDefaults`). CSV headers are parsed in order `{name}({unit})`, `{name}_{unit}` (only if the suffix is a known unit), then `{name}` — so `EVM(dB)` and `I_Total(A)` become columns `EVM` and `I_Total` with types from the unit table (`dB`/`A` → `double`; `id`/`ID` → `long`). `{name}_{unit}` does not split `I_Total`. Roles: `parameters` or lists `stimulus` / `classification`; default role is `meas`. Names in `metadataFields` (defaults: Lot, Tester, Program) move to the metadata table when constant (`metadataPolicy`: `requireConstant`, `first`, or `bounce`). `columns` still overrides type per canonical name. Overlay JSON may also include `units` (with `aliases`) and `csv.headerPatterns`.

### Tables

`tables` splits the session into several DuckDB tables while `"data"` stays available as a **view** that joins them back wide. Without `tables` the session is one wide `"data"` table exactly as before.

```json
{
  "stimulus": ["Frequency", "Temp", "Vcc"],
  "parameters": { "stimulusGrp": { "role": "identity" }, "sweep": { "role": "identity" } },
  "tables": {
    "device": { "kind": "master",       "columns": ["SN"] },
    "setup":  { "kind": "dimension",    "columns": ["Teststand", "Temp", "Vcc"] },
    "sweep":  { "kind": "rows",         "roles":   ["identity", "stimulus"], "columns": ["Date"] },
    "rf":     { "kind": "measurements", "columns": ["Pout", "Pin", "Gain", "EVM"] },
    "dc":     { "kind": "measurements", "columns": ["I_Total"] }
  }
}
```

Kinds: `master` (at most one; the root dimension), `dimension` (a normalized repeating group stored once per distinct key), `rows` (the spine, one per layout; implied as `rows` when absent), `measurements` (a column group; rows where every group column is NULL are not stored, so sparse column sets do not produce wide null rows). Each table binds columns by explicit `columns` first, then by `roles` (`identity`, `stimulus`, `meas`, `classification`) for columns nobody claimed; unclaimed columns land on the spine. A dimension's `key` defaults to all its columns; the surrogate key is a deterministic hash of the key columns, and a key that does not determine the other columns throws. Names `data`, `meta`, and `_*` are reserved; `parent` (snowflake) is parsed but not supported yet. Tables that match no column are omitted. `metadataFields` still go to `Metadata`, not to a table.

The view lists the wide columns in their original order and no key columns. `Query`, `Filter` / `Count` / `ApplyFilter`, CSV / Parquet / archive export, hive Squish output, `information_schema` on `'data'`, and the CLI work through the view. Row order of `SELECT * FROM data` is not guaranteed; `ORDER BY`. Import replaces the layout atomically: a failed import leaves the previous tables and view in place.

Shipped in this slice: `ImportAsync` (replace) of every format and the first `AddRows` / `AddRow` / `CommitRow` batch into an empty layout session build the tables. **Not yet** on a layout session (throws `... is not supported on a multi-table session yet`): `AddColumn`, `RemoveColumn`, `MergeOrAppend`, `Bounce` / `Squish`, `ImportAsync` with `Append = true`, further `AddRow` / `AddRows` / `InitializeRow` / `CommitRow` once the view exists, and `Open` with a handler that does not delegate to generic import. Reopening a file-backed layout session requires a config that declares the same `tables`.

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

This is not a matrix rewrite. DuckDB already dictionary-encodes repeats. Bounce is not yet supported on a multi-table session (see [Tables](#tables)).

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
databall import hive -o session.ball
databall query session.ball "SELECT * FROM data LIMIT 10"
databall info session.ball
```

`bounce` without an output path writes a sibling `.ball`. `query` prints TSV to stdout. It is a local escape hatch and must not be exposed at a service boundary.

## .ball format

A `.ball` file is a ZIP of `data.parquet` + `metadata.json` (+ optional `config.json` when column types, relationships, or `tables` exist). Portable without DuckDB: unzip and read the parquet. DuckDB is the working engine, not the interchange file.

A multi-table session writes a **v2** ball: the same wide `data.parquet` (so older readers keep working) plus `manifest.json` (`ballVersion`, wide `columns`, `tables`) and one lossless `tables/<name>.parquet` per physical table, including surrogate keys and row keys. `config.json` carries `tables`, so a reader with no config rebuilds the layout; a reader whose own config declares different `tables` takes the ball's (config.json is an overlay, like `columns` and `relationships`). When `tables/` or the manifest is missing or does not fit, the reader falls back to `data.parquet` and re-splits. A save with `ApplyFilter` set writes only the filtered wide `data.parquet`.

## Platforms

CI is `windows-latest` = win-x64, `macos-latest` = osx-arm64 (portable `osx` RID), and `ubuntu-latest` = linux-x64. win-arm64 is in the nupkg, not CI-tested. linux-arm64 is in the nupkg, not CI-tested.

## Troubleshooting

Debugging a process that uses DuckDB.NET can throw `AccessViolationException` because the debugger touches native memory; this is documented upstream (Giorgi/DuckDB.NET “Known Issues”) and is not a DataBall bug.

SharpCompress **0.50.4**. `.tar.xz` import works; `.tar.xz` export does not (XZ is decompress-only). Do not use SharpCompress 1.0.0.

## Historical docs

`docs/archive/` holds pre-rebuild proposals and the DataFrame/SQLite-era class notes (Polars, Arrow, hierarchical TestData). They do not describe the shipped API. This README and `docs/ROADMAP.md` are the current docs.

## License

Apache License 2.0. See [LICENSE](LICENSE) for details. Versions ≤ 1.0.0 were distributed under MPL-2.0; those releases remain available under those terms. 1.1.0+ is Apache-2.0. Git tags: `v1.2.0` (last release; this tree is 1.3.0, unreleased), `v1.0.0-mpl` (last MPL-2.0 commit).

## Repository

https://github.com/squalor-xyz/DataBall
