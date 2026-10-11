# DataBall

<img src="assets/databall-512.png" alt="DataBall" width="128" />

Apache-2.0 .NET library and `databall` CLI for **test-executive / ATE measurement tables**. Capture sequential test rows, propagate slowly changing setup fields, reset dependents from config relationships, store true constants in metadata (Bounce), and import/export CSV, Parquet, zip/tar of CSV, and `.ball`.

## Engine (DuckDB)

The working store is DuckDB via `DuckDB.NET.Data.Full` 1.5.5. `Open` / `OpenAsync` choose **in-memory** for non-native input at or below `EngineOptions.InMemoryMaxBytes`, or a temporary DuckDB file above it. The default threshold is 25% of `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`. `StoreMode.Memory` / `File` force the choice; an empty constructor session starts in memory unless `File` is requested. Temporary stores use `TempDirectory` (OS temp by default) and clean up the database, WAL, and default spill directory on disposal or constructor failure; cleanup failures are logged. `EngineOptions` also sets DuckDB `MemoryLimit`, `Threads`, and spill `TempDirectory`; null uses DuckDB’s default. These settings are separate from `Config` and never saved in a `.ball`. Native files ignore store mode and threshold but apply engine settings. Opt-in file-backed: `new DataBall(databasePath: "session.duckdb")` (named argument; the first ctor string is still config JSON). Dispose does not delete the file. Do not open `catalog.duckdb` as a DataBall session. A `.ball` is also a native DuckDB file. `DataBall.Open("session.ball")` opens it directly, read-only by default; pass `writable: true` to modify it. The package ships **native RID binaries** (`libduckdb` / `duckdb.dll`) for win-x64, win-arm64, osx-x64, osx-arm64, linux-x64, and linux-arm64. This is **not** a pure-managed library.

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
| Parquet | `.parquet` or hive directory | `Open` attaches a view; `ImportAsync` copies | yes (hive partition via Bounce/Squish path+cols) |
| Archive of CSV | `.zip`, `.tar`, `.tar.gz`, `.tgz`, `.tar.xz`, `.txz` | yes | `.zip` / `.tar` / `.tar.gz` / `.tgz` only |
| Native DuckDB `.ball` | `.ball` | yes (replace or append) | yes (whole session) |

`Open` scores registered handlers and JSON file types before choosing these built-in fallback paths. `detect --explain` reports the scores and chosen route.

## Library usage

Pass machine-local settings to the constructor or open methods:

```csharp
using squalor.DataBall;

var engine = new EngineOptions { MemoryLimit = "1GB", Threads = 2, InMemoryMaxBytes = 64 * 1024 * 1024 };
using var session = await DataBall.OpenAsync("measurements.csv", engine: engine);
```

Registered lab handlers write parsed rows in batches of 10,000. A failed open disposes the partial session and removes its temporary store.

The CLI accepts `--memory-limit`, `--threads`, `--temp-dir`, and `--store auto|memory|file` on `import`, `export`, `query`, and `info`:

```sh
databall import measurements.csv -o session.ball --memory-limit 1GB --threads 2
```

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

`Metadata` is a snapshot; write with `SetMetadata`. `ImportAsync` detects format by extension (`Append` defaults to `false`). Every import is one transaction: a failing import (including a multi-CSV archive that fails part-way) leaves the session, its config, and its metadata as they were. `SaveAsync` and `ExportType.Ball` save the whole session as a compact DuckDB copy, even with a session filter. Saving to the live session path checkpoints it. Dispose with `using`; an uncommitted pending row is discarded.

`DataBall.Open(path, schemaPath = null, writable = false)` opens a native `.ball` as the session store. It reads embedded config and writes nothing by default. An overlay schema must declare the same `tables`. CSV and archive files create an imported session; they need no handler. For files, a registered handler takes precedence; a handler that delegates to generic import uses the generic path. A single unclaimed or delegated Parquet file, or a hive Parquet directory, opens as a `read_parquet` view, with projection and filter pushdown and no row copy. Configured `tables` copy and split at open. An append or edit copies the external view into a table in the write transaction; a replace drops the view without reading the source. Failure restores the view. `InitializeRow` performs that copy in its own transaction before starting a pending row; `SetMetadata` keeps the view. Store choice still uses source size at open. `ImportAsync` also reads a Squish **hive directory** of parquet (`hive_partitioning=false`; partition columns come from the files). Hive import is not `.ball` and does not restore Bounce metadata.

Lab translators live in `squalor.DataBall.Handlers` (not `DuckDbStore`). Primary dialect is **custom CSV** with unit headers (`EVM(dB)`). `LabHandlers.RegisterDefaults()` also registers STDF / Touchstone / production **slots** (Parse throws until a golden file exists). `RegisterDefaults()` is idempotent: it skips a handler type that is already registered. Duplicates registered by hand with `DataBall.RegisterHandler` still tie and are ambiguous. Register with `DataBall.RegisterHandler`; the highest `Score(FileProbe)` wins, with `Priority` breaking equal scores. A remaining tie throws with the scoring table. Legacy handlers keep `CanHandle` and receive their full stream at position zero; the default score maps its result to 1 or 0 with a reason. `CustomCsvHandler` scores 0.6 for a known unit header. `ClearHandlers` resets handlers, loaded types and lazy folder scanning. Do not put parsers in `DuckDbStore`.

Session filter is column predicates pushed to DuckDB (`Eq`, `In`, `Ge`, `Le`, inclusive `Range`) plus optional column projection. `Filter(spec)` does not mutate `"data"`. `ApplyFilter(spec)` applies to CSV, Parquet, and archive export; `ApplyFilter(null)` clears. `Query(sql)` is the unfiltered escape hatch.

`AddRows` inserts a batch with one DuckDB appender. It does not apply trigger/reset (`CommitRow` does). Empty input is a no-op.

### File types

`DataBall.Detect(path)` returns the scoring table and a `Match`, `Generic`, `Ambiguous` or `None` outcome without constructing a session. Native DuckDB `.ball` files are recognized before handlers; directories are never offered to them. When there is no scored candidate, the built-ins provide CSV/archive import or the Parquet/hive view. An unknown extension has no fallback. `Open` throws on ambiguity or no match with the table in its message.

JSON types load lazily, once per process, from `~/.databall/types/`, then `./databall/types/` under the working directory. `DataBall.RegisterFileType(path)` registers an explicit file last. A later origin replaces the same `id` and `version`; `DataBall.FileTypes` and detection entries report origins. Invalid files fail at load with their path. A winning type delegates to the existing generic import/view path and merges config in the order defaults, winning type's `config`, then the host's `schemaPath`, before store selection and session construction. Its `tables` therefore chooses the layout path.

```json
{
  "id": "lab-sweep",
  "version": 1,
  "priority": 0,
  "detect": {
    "minScore": 0.6,
    "conditions": [
      { "fileName": "SWEEP_*.csv", "weight": 1 },
      { "headerContainsAll": ["EVM(dB)", "I_Total(A)"], "weight": 3, "required": true }
    ]
  },
  "config": { "columns": { "EVM": "double" } }
}
```

Scores are matched weight divided by total weight; a missed required condition gates the score to zero. Each condition has exactly one condition key, optional numeric `weight` (default 1) and boolean `required` (default false). Supported keys are:

| Condition | JSON value |
|---|---|
| `fileName` | glob string, matched against the file name |
| `fileNameRegex` | regex string |
| `extension` | array of extensions including the dot |
| `headerContainsAll` | array of literal header names |
| `headerExact` | array of header names, in order |
| `lineMatches` | `{ "line": 0, "regex": "..." }` or `{ "firstLines": 5, "regex": "..." }` |
| `columnCount` | `{ "min": 2, "max": 5 }`, inclusive; either bound optional |
| `parquetColumns` | array of top-level column names |

A JSON type that scores exactly 0.6 ties with `CustomCsvHandler` and is ambiguous; give the type a full match or a higher priority.

Matches are case-insensitive; regexes have a one-second timeout. Aliases, preambles, captures as config sources and transforms are later slices. DataBall's own detection reads at most 64 KiB, plus Parquet's four-byte tail and a short-lived `DESCRIBE` query. Legacy `CanHandle` reads retain their previous unrestricted behavior.

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

A config file is an **overlay** on native defaults (`Config.CreateDefaults`). CSV headers are parsed in order `{name}({unit})`, `{name}_{unit}` (only if the suffix is a known unit), then `{name}` — so `EVM(dB)` and `I_Total(A)` become columns `EVM` and `I_Total` with types from the unit table (`dB`/`A` → `double`; `id`/`ID` → `long`). `{name}_{unit}` does not split `I_Total`. Roles: `parameters` or lists `stimulus` / `classification`; default role is `meas`. For CSV and Parquet (files and hive directories), names in `metadataFields` (defaults: Lot, Tester, Program) move to the metadata table under the default `metadataPolicy: liftConstant` when they have one distinct non-null value (nulls are allowed); varying fields stay columns. `requireConstant` throws for varying fields, while `first` and `bounce` remain explicit opt-ins. Parquet keeps its source names and types, with DATE promoted to TIMESTAMP; CSV header canonicalization and type coercion do not apply. A Parquet view reads the configured metadata aggregates and excludes extracted columns without copying rows. Append reconciliation keeps equal metadata values and restores differing values as a column; `MergeOrAppend` copies without configured metadata extraction. `columns` still overrides CSV type per canonical name. Overlay JSON may also include `units` (with `aliases`) and `csv.headerPatterns`.

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

Kinds: `master` (at most one; the root dimension), `dimension` (a normalized repeating group stored once per distinct key), `rows` (the spine, one per layout; implied as `rows` when absent), `measurements` (a column group; rows where every group column is NULL are not stored, so sparse column sets do not produce wide null rows). Each table binds columns by explicit `columns` first, then by `roles` (`identity`, `stimulus`, `meas`, `classification`) for columns nobody claimed; unclaimed columns land on the spine. A dimension's `key` defaults to all its columns; the surrogate key is a deterministic hash of the key columns, and a key that does not determine the other columns throws. Names `data`, `meta`, `source`, and `_*` are reserved; `parent` (snowflake) is parsed but not supported yet. Tables that match no column are omitted. Under the default `liftConstant`, constant `metadataFields` go to `Metadata`; varying fields stay columns in the declared layout.

The view lists the wide columns in their original order and no surrogate-key columns; the one extra column is `_source_key`, which sits on the spine and points at the `source` table (see [Source lineage](#source-lineage)). `Query`, `Filter` / `Count` / `ApplyFilter`, CSV / Parquet / archive export, hive Squish output, `information_schema` on `'data'`, and the CLI work through the view. Row order of `SELECT * FROM data` is not guaranteed; `ORDER BY`. Import replaces the layout atomically: a failed import leaves the previous tables and view in place.

Writes route into the tables: `ImportAsync` (replace or `Append = true`) of every format, `AddRow` / `AddRows`, `InitializeRow` / `CommitRow` (copies the row with the highest row key), and `MergeOrAppend`. Incoming values are cast to the physical column types before hashing, so a dimension row is reused when its key columns match (`25` after `25.0` is the same setup). Columns absent from a batch are NULL; a new unclaimed column is added to the spine, and a new column claimed by a `measurements` table that had no columns yet creates that table. An append whose rows would add a column to a dimension, or a new dimension, re-splits the session (materialize wide in row-key order, append, split again in one transaction). `_row` is renumbered and dimension keys may change, so do not treat either as stable (a declared-`key` dimension keeps its hash-of-key surrogate; an undeclared-key dimension gets new keys when a column is added). Existing rows get NULL in a new dimension column, so an appended row, including a `CommitRow` copy of the last row, that reuses an existing declared key with a non-NULL value in it throws (the key no longer determines the row) and leaves the session as it was. The re-split renames its materialized wide table and consumes it directly during splitting, avoiding extra `_unsplit` and `_wide` copies. The `_cast` staging table is still a full wide TEMP copy held in memory, and dropped tables remain until commit; the early drop does not lower the peak inside the transaction. It still rewrites the whole session, so its cost grows with session size and adding a dimension column mid-capture is expensive; later appends of the same shape take the in-place path. Metadata columns arriving on an append are reconciled: equal to the stored value → ignored; different → the key leaves `Metadata` and becomes a column of the table the config assigns it to, filled with the old value for earlier rows (a spine column directly; a dimension or group column via the re-split). With `metadataPolicy: first` the stored value wins and the column is ignored. Every write is one transaction; a failure leaves tables, view, config, and metadata as they were. A `.ball` whose stored config declares a *different* `tables` layout can replace a layout session but cannot be appended to it (throws). `AddColumn`, `RemoveColumn`, and `Bounce` / `Squish` work on a layout session: the layout is materialized back into one wide table in row-key order, the wide operation runs, and the result is re-split in one transaction (a failure leaves the session as it was). The internal row key `_row` is regenerated by those operations; it is not a public key. `RemoveColumn` refuses a column in a table's declared `key`. Reopening a file-backed layout session binds the stored `tables` config automatically.

Both wide and layout sessions demote a conflicting metadata append under the default `liftConstant`. Differences from a wide session, on purpose: with explicit `requireConstant`, a layout demotes a conflict while a wide CSV append throws; physical column types widen on append under the rule in *Column types on append*, and incoming values are cast otherwise (the wide session re-casts existing columns to grown config types); metadata reconciliation compares canonical names (the wide session compares raw headers).

**Column types on append.** An append merges columns by name, case-insensitively; a column the append lacks is NULL. An incoming column with no non-null value adopts the existing type, and an empty existing column (any type) that is not declared adopts the incoming type. Otherwise two integer types widen to the wider integer; an integer or `FLOAT` meeting `FLOAT` or `DOUBLE` widens to `DOUBLE`; `DATE` meeting `TIMESTAMP`, with or without time zone, widens to `TIMESTAMP`. A wide session alters the column in place, and so does a layout session, which alters the table that owns the column. A dimension key column widened to `DOUBLE` or `TIMESTAMP` re-splits the session, because `hash(25)` and `hash(25.0)` differ, as do `hash(DATE)` and `hash(TIMESTAMP)` and `hash(FLOAT)` and `hash(DOUBLE)`; a dimension with no declared `key` has every column as a key column. A key widened to a wider integer keeps its keys. `_source_key` is never widened. Any other type conflict, such as `VARCHAR` into `DOUBLE`, or `UBIGINT` or `DECIMAL` into an integer, throws a `DataBallException` that names the column, both types and the incoming source, before any change, so the session is left as it was; `MergeOrAppend` names the source as the merged session. **Declared** types are the config's column and parameter types and the types set by `AddColumn<T>`: a declared column always casts to its type and never widens. **Learned** types are the ones DataBall takes from `InitializeRow`/`CommitRow` and from the CSV schema pass: they widen by the normal rules and are not saved as declared. Parquet, native `.ball` and `MergeOrAppend` imports do not coerce incoming values, so their declared columns cast through the merge. A learned type never narrows an incoming value on a CSV append that merges into a live session: where the incoming column is wider, the value is kept and the merge widens the column, so a learned `INTEGER` takes `2.7` as `2.7` in a `DOUBLE` column. First imports and replaces still cast to learned types, as before.

### Source lineage

A session opened or imported from a file records where its rows came from. The built-in `source` table has one row per imported file: `source_key` (BIGINT, assigned 1, 2, … in import order and stable across appends), `path`, `sha256`, `type`, `bytes`, `modified_utc` and `added_utc`. Each row of `"data"` carries one `_source_key` that points at its `source` row, in wide and layout sessions alike (on a layout it is a spine column, and `source` is not one of the layout tables). Read the sources with `db.Sources` (`IReadOnlyList<SourceInfo>` with `Key`, `Path`, `Sha256`, `Type`, `Bytes`, `ModifiedUtc`, `AddedUtc`) or with `db.Query("SELECT * FROM source")`. `_source_key` is a BIGINT, so `ReadColumns` reads it as `long`.

```csharp
using var db = DataBall.Open("run-07.csv");
await db.ImportAsync("run-08.csv", new ImportOptions { Append = true });
foreach (var source in db.Sources)
    Console.WriteLine($"{source.Key} {source.Path} {source.Sha256}");
var rows = db.Query("SELECT d.*, s.path FROM data d LEFT JOIN source s ON d._source_key = s.source_key");
await db.ExportAsync("all.csv", ExportType.Csv); // ends with _source_file, _source_type
```

- `path` is the full path of the file. Each CSV entry of an archive is its own source, `<archive>!<entry>`. `sha256` is the full lowercase hex SHA-256 of the file, computed when the file is imported (about 0.84 s per GiB on an Apple M2, so a 50 GB input adds about 40 s). `Open` and `ImportAsync` hash it before their first write; the `ImportManager` static imports and every archive entry after the first hash inside the import's transaction, which a failure rolls back. A hive Parquet directory is one source: `bytes` is the sum of its Parquet files and `sha256` is the SHA-256 of the lines `<relative path>\t<file SHA-256>\n` for those files, sorted by relative path (ordinal, `/` separators); each file is read once. `SourceInfo.Sha256` is a `string?` only because the `source` table's column is nullable: DataBall always writes it, and it is null only for a row something else wrote. `modified_utc` is the file's last-write time; for an archive entry it is the time stored in the archive.
- `type` is what detection chose: the handler name (a JSON type's id) or the built-in path, `CsvImport`, `ParquetView` or `ArchiveImport`. `ImportAsync` records the built-in path of the file's format.
- A replacing import starts the `source` table over at key 1; an append adds sources and keeps the earlier keys. A failed import leaves the sources as they were. `MergeOrAppend` and native `.ball` import bring the other session's sources in with their keys moved after this session's; the `.ball` file itself is not a source. A `.ball` saved before lineage has no `source` table, and its rows arrive with a NULL `_source_key`.
- Rows from `AddRow`, `AddRows` and the row builder came from no file: their `_source_key` is NULL, and `InitializeRow` does not copy the last row's key. A session that never imported a file has no `source` table and no `_source_key` column. The `source` table is created by the first import; until then `db.Sources` is empty.
- Bounce never moves `_source_key` into `Metadata`, and its `DISTINCT` ignores it: rows equal in every other column collapse to one row from whichever files they came from, and the lowest `_source_key` survives (a row with a NULL key takes the key of an equal file row). A table that has only `_source_key` left holds no data and is dropped, as an empty one was before.
- CSV, Parquet, archive and Squish hive exports resolve the key: the exported file has `_source_file` (the path) and `_source_type` in place of `_source_key`, empty for a NULL key, so it stands alone. This holds under `ApplyFilter`. A filter whose `Columns` leaves out `_source_key` exports only those columns. A session whose rows have `_source_key` but no `source` table (a pre-lineage or capture-only `.ball` imported into a fresh session) exports the key as nothing and both columns as NULL, never as the raw key. `SaveAsync` and `ExportType.Ball` keep the `source` table and `_source_key` unchanged, and the session itself is not changed by exporting.
- Names starting with `_source` (any case) are reserved. `source` and `_source*` cannot be table names, and a `tables` entry may not list a `_source*` column in `columns` or `key`; config validation rejects them. `AddRow`, `AddRows`, `InitializeRow` (its initial values), `ModifyField`, `AddColumn` and `RemoveColumn` throw `DataBallException` for such a column name before anything changes; the handler drain and the merge write `_source_key` themselves. In an imported file (CSV, Parquet, a handler's rows, a native `.ball`'s data) `_source_file` and `_source_type` are renamed to `origin_source_file` and `origin_source_type`, so re-importing a DataBall export keeps its earlier provenance beside the new `_source_key`; any other `_source*` column makes the import fail and names the column; `_source_key` fails too, except in a native `.ball`, whose keys are remapped as above. An input that already has an `origin_source_file` or `origin_source_type` column, and also brings `_source_file` or `_source_type`, fails rather than overwrite it. Partitioning a Squish by `_source_key` fails: the output carries `_source_file` and `_source_type` instead.

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

`InitializeRow` copies the last committed row when one exists, except its `_source_key` (see [Source lineage](#source-lineage)). Changing a trigger field resets dependents that were not explicitly modified.

### Bounce and Squish

Bounce moves **strict constants** (exactly one distinct non-null value **and no nulls**) into `Metadata`, drops those columns, then `DISTINCT` remaining rows. Mixed or all-null columns stay. Partition columns are not extracted. If the last remaining constant is extracted, table `"data"` is dropped (DuckDB cannot store a 0-column table). `_source_key` is never extracted, and a table left with only that column counts as empty.

Squish is the same call as Bounce. Hive-partitioned Parquet is written only when both a directory path and partition columns are supplied; its files carry `_source_file` and `_source_type` (see [Source lineage](#source-lineage)):

```csharp
await db.Squish("hive", new[] { "Site" });
```

This is not a matrix rewrite. DuckDB already dictionary-encodes repeats. On a multi-table session Bounce runs on the materialized wide rows and re-splits; a table whose columns all became metadata is dropped, and a column in a table's declared `key` is never extracted (see [Tables](#tables)). `DISTINCT` does not keep row order, so after Bounce (wide or layout) the stored row order is not write order: a row builder with no remembered last row (for example after a reopen) copies an arbitrary row.

### Query

```csharp
var rows = db.Query("SELECT Name, Age FROM data ORDER BY Name");

var filter = new SessionFilter { Columns = ["Name", "Age"] };
ColumnSet columns = db.ReadColumns(filter);
var ages = (ColumnData<int>)columns["Age"];
for (var i = 0; i < ages.Values.Length; i++)
    if (ages.Nulls is null || !ages.Nulls[i])
        Console.WriteLine(ages.Values[i]);

await foreach (var row in db.ReadRowsAsync(filter, cancellationToken))
    Console.WriteLine(row["Name"]);
```

`ReadColumns` returns arrays in projection order. The read-side map is INTEGER/SMALLINT/TINYINT/UTINYINT/USMALLINT → `int`, BIGINT/UINTEGER → `long`, FLOAT → `float`, DOUBLE/DECIMAL/UBIGINT/HUGEINT → `double`, BOOLEAN → `bool`, DATE/TIMESTAMP* → `DateTime`, and UHUGEINT and every other unlisted type → `string` using DuckDB's text form (`CAST(... AS VARCHAR)`). This map is separate from import typing. Column lookup is case-insensitive. Null slots hold `default(T)`; `Nulls[i]` is true for those slots, and `Nulls` is null when a column has no nulls. Arrays are sized using one matching-row count before the reader fills them.

`ReadRowsAsync` uses DuckDB streaming mode and yields a separate caller-owned dictionary for each row. Both reads push projection and predicates to DuckDB and read through the wide `data` view for table layouts. They do not apply `CurrentFilter` implicitly or guarantee row order. Cancellation is checked before execution and between rows. Finish or dispose the enumerator (including an `await foreach` early break) before any call that touches the store; any call that touches the store while a row stream is open throws `DataBallException`. In-memory access to `Metadata`, `Schema` and `CurrentFilter`, and clearing the filter with `ApplyFilter(null)`, still works. Disposing the session ends the stream: its next move throws. `ReadColumns` and `ReadRowsAsync` execute synchronously on the caller's thread; UI hosts should use `Task.Run` to keep that work off the UI thread. Sessions are owned by one caller and are not thread-safe.

## CLI

```
databall detect  <path> [--explain] [--types <dir>]
databall import  <input> -o <output> [--append] [--config]
databall export  <input> <output> [--format csv|parquet|ball|archive]
databall bounce  <input> [output]
databall squish  <input> [output] [--partition col,...]
databall query   <file> <sql>
databall info    <file>
```

`import`, `export`, `query`, and `info` accept `--memory-limit <limit>`, `--threads <count>`, `--temp-dir <directory>`, and `--store auto|memory|file`. The CLI uses the same input-size rule as `OpenAsync`: `auto` sums file bytes (recursively for directories), uses memory at or below the threshold (default 25% of GC available memory), and a temporary file above it. `memory` and `file` force the store choice. For append import, both the existing output and incoming input count. `--temp-dir` sets the temporary store location and DuckDB spill directory.

```bash
databall import measurements.csv -o session.ball
databall import more.csv -o session.ball --append
databall export session.ball people.parquet
databall bounce session.ball compact.ball
databall squish session.ball hive --partition Site
databall import hive -o session.ball
databall query session.ball "SELECT * FROM data LIMIT 10"
databall query session.ball "SELECT * FROM source"   # the imported files
databall info session.ball
```

`detect` prints the outcome; `--explain` includes scores, thresholds, priorities, reasons, origins and the built-in fallback. `--types` registers every JSON type in that directory. It exits 0 for `Match` or `Generic`, and 1 for `Ambiguous` or `None`.

`export` to CSV, Parquet or an archive ends each row with `_source_file` and `_source_type` (see [Source lineage](#source-lineage)). `bounce` without an output path writes a sibling `.ball`. `query` prints TSV to stdout. It is a local escape hatch and must not be exposed at a service boundary.

## .ball format

A `.ball` v3 is a DuckDB database file, with `DUCK` at bytes 8–11. It contains `data` (a table, layout view, or opt-in external Parquet view), layout tables when configured, `meta` for metadata, the `source` table once a file has been imported (with `_source_key` in `data`; see [Source lineage](#source-lineage)), and the reserved `_databall` config history table. Each config version records `version`, `ball_format` (3), JSON `config`, `duckdb_version`, `storage_version`, and `written_at`. On the latest row, `duckdb_version`, `storage_version`, and `written_at` describe the last save, not when that config version was created. The highest version is current; unchanged merged config does not add a row. Config cannot declare tables named `_databall`, `meta` or `source`, or any name starting with `_source`.

Save attaches a temporary destination, runs `COPY FROM DATABASE`, embeds an external Parquet view as a table in the saved copy by default, detaches it, then replaces the destination. When saving to another path, the live session keeps its view. A default save by a writable session to its own live path embeds the Parquet in place and checkpoints the resulting table. `await session.SaveAsync("linked.ball", new SaveOptions { Link = true })` preserves the view and absolute source paths instead; that file requires its Parquet sources, and opening it with a missing source throws `DataBallException` naming the path. Native import attaches the source read-only and copies rows, metadata and `source` rows (keys moved after the session's own) through staging and merges the schema config subset in one transaction; the source is detached before the call returns. ZIP `.ball` v1/v2 is unsupported. DuckDB's default storage version is retained, with no older-engine reader floor claimed.

The same-path in-process gate applies to read-only and writable sessions. Cross-process live-reader snapshot publication is outside this release's native-file change.

Native saves stamp the latest `_databall` row with the running engine's `version()`, the destination's actual `duckdb_databases()` storage tag, and `written_at` (time of that save). The engine and storage versions are read after the database copy and before detach. Saving a writable session to its own file stamps that row before checkpointing. Config history grows only when config changes, including a read-only overlay saved to a copy. A read-only session cannot save to its own path (it throws); save to another path, or open with `writable: true`.

`Open` uses the full stored config; an overlay merges on top and must keep the same table layout (table order does not matter). Stored `meta` values are authoritative on reopen; only overlay metadata overrides them, in memory for a read-only session. Native `ImportAsync` merges only columns, relationships, tables, and config metadata. The receiving session keeps its CSV settings, metadata fields, metadata policy, and other settings.

## Platforms

CI is `windows-latest` = win-x64, `macos-latest` = osx-arm64 (portable `osx` RID), and `ubuntu-latest` = linux-x64. win-arm64 is in the nupkg, not CI-tested. linux-arm64 is in the nupkg, not CI-tested.

## Troubleshooting

Debugging a process that uses DuckDB.NET can throw `AccessViolationException` because the debugger touches native memory; this is documented upstream (Giorgi/DuckDB.NET “Known Issues”) and is not a DataBall bug.

SharpCompress **0.50.4**. `.tar.xz` import works; `.tar.xz` export does not (XZ is decompress-only). Do not use SharpCompress 1.0.0.

## Diagrams

[`docs/how-it-works.md`](docs/how-it-works.md): Mermaid diagrams of the session lifecycle, where the data lives (memory, `.duckdb`, `.ball`), atomic import, header → schema, row builder, Bounce, multi-table layout, and the `.ball` format.

## Historical docs

`docs/archive/` holds pre-rebuild proposals and the DataFrame/SQLite-era class notes (Polars, Arrow, hierarchical TestData). They do not describe the shipped API. This README and the roadmap in `.slicer/` (see `docs/ROADMAP.md`) are the current docs.

## License

Apache License 2.0. See [LICENSE](LICENSE) for details. Versions ≤ 1.0.0 were distributed under MPL-2.0; those releases remain available under those terms. 1.1.0+ is Apache-2.0. Git tags: `v1.3.0` (last release), `v1.0.0-mpl` (last MPL-2.0 commit).

## Repository

https://github.com/squalor-xyz/DataBall
