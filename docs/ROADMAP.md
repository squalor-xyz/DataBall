# DataBall roadmap

Engineering leftovers from the DuckDB rebuild review, plus the suite-driven Open/handler track. **ATE spirit:** sequential test-executive tables, DuckDB as the only SQL engine, interchange via CSV / Parquet / `.ball` / zip-tar.gz. SQLite, DataFrame, and matrix Bounce are gone and stay gone.

DataBall is WIP and co-developed with `squalor-xyz/suite`. It is the cross-app utility: configurable schema, Open (detect + `IFormatHandler` → session), batch/row insert, query, session filter, and (from 1.3.0) a config-declared multi-table layout behind the `"data"` view.

License: **Apache-2.0** as of 1.1.0 (1.0.0 was MPL-2.0).

Do not nuget-push or git-push from this list unless the owner asks.

**Done (not on this list):** DuckDB store, net10 lib+CLI, row builder, Bounce/Squish, `.ball`, config types on CSV/archive import, public-API tests, drop SQLite.

---

## Suite-driven (do with squalor-xyz/suite)

| # | Item | Kind |
|---|---|---|
| S1 | Native default profile + overlay config: header patterns, unit→type, roles, metadataFields | Feature (done) |
| S2 | `IFormatHandler` + `RegisterHandler` + `Open(path, schema)` | Feature (done) |
| S3 | First lab handler project (not in `DuckDbStore`) | Feature (done: `DataBall.Handlers`, custom CSV primary; STDF/Touchstone/production slots) |
| S4 | Session filter API (column predicates; `Query` stays escape hatch) | Feature (done) |
| S5 | Batch insert path that does not row-loop | Feature (done) |
| S6 | Config-declared table layout (`tables`): master / dimension / rows / measurements behind the `"data"` view; `.ball` v2 | Feature — suite **S46** read path + `.ball` (done); **S47** row writes into tables; **S48** Bounce / column edits / MergeOrAppend; **S49** snowflake `parent` + perf guard |

Handlers: custom CSV is implemented. STDF / Touchstone / production are registered slots (`CanHandle` by extension; `Parse` throws until a golden file is named). No ALC in this slice.

---

## Order (hygiene)

| # | Item | Kind |
|---|---|---|
| 1 | Import API consistency | Fix (done) |
| 2 | Untyped CSV types (document + lock tests) | Fix / docs (done) |
| 3 | Hive-partitioned Parquet directory import | Feature (done, 1.2.0) |
| 4 | SharpCompress bump (and tar.xz export only if the library can write XZ) | Hygiene (done, 0.50.4; xz export still rejected) |
| 5 | File-backed DuckDB | Feature (done) |
| 6 | CI / native RID proof | Verify — suite **S37** |
| 7 | Local pack checklist | Docs only — suite **S38** |

---

## 1. Import API consistency

**Goal.** `ImportManager.ImportFromCsv` matches Parquet/archive/`.ball` and `ImportAsync`: explicit `append`, no fake `chunkSize`.

**Why.** CSV helper always `append: true` and ignores `chunkSize`. A second CSV into a live ATE session silently concatenates. `ImportAsync` already defaults replace (`ImportOptions.Append = false`). `chunkSize` is a DataFrame leftover; DuckDB streams.

**Contract**

- Replace:

```csharp
public static void ImportFromCsv(DataBall db, string path, bool append)
```

- Call `db.Store.ImportCsv(path, append, db.ExpectedColumnTypes)`.
- No default `append` (same as `ImportFromParquet`). Empty dest already treats append as replace.
- Delete `chunkSize`. Do not keep a no-op parameter.
- `ImportAsync` / CLI `--append` unchanged.

**Files**

- `src/DataBall/Import/ImportManager.cs`
- Call sites in `IntendedBehaviorTests.cs`, `DataBallTests.cs`, `ManagerTests.cs`, `ImportExportTests.cs`
- Optional: `DataBall.ImportAsync` CSV branch through `ImportManager` (changes CSV error string to `"Failed to import CSV file"` — only if you accept that)
- Do not edit `docs/archive/`

**Steps**

- Change the signature; drop `_ = chunkSize`.
- Update call sites: empty dest → `append: false`; `ImportCsv_IncomingColumnMatchesMetadata_DoesNotThrow` **must** pass `append: true`.
- Delete `ImportCsv_ChunkSizeOne_StillLoadsEveryRow` (it only proved ignore).

**Tests**

- Add `ImportFromCsv_DefaultIsReplace` and `ImportFromCsv_AppendTrueConcatenates` (mirror the `ImportAsync` facts).
- Keep config-int CSV tests; only add the `append:` argument.

**Risks**

- Public breaking change (unpublished 1.0.0). Old two-arg `ImportFromCsv` concatenated; after this, omit `append: true` and you **replace**.
- Do not add pending-row checks on `ImportManager` in this item.

**Out of scope.** Real CSV chunking; CLI `--append`; Parquet `ExpectedColumnTypes`.

---

## 2. Untyped CSV column types

**Goal.** Lock the rule: types come from **config** or **`AddColumn`**. Untyped CSV integers stay DuckDB BIGINT / `long`. Do **not** infer INTEGER from value range.

**Why.** `read_csv_auto` infers BIGINT. Config CAST already makes `"Age": "int"` → `int` (`CsvImport_WithConfigAgeInt_ValuesAreInt32`). Silent “fits in int32” narrowing would break serials/counters and config `"Age": "long"`.

**Contract**

| How the column appears | Query CLR type |
|---|---|
| `AddColumn<int>` | `int` |
| Config `"Age": "int"` then CSV/archive | `int` |
| Config `"Age": "long"` | `long` |
| Untyped CSV `Age` | `long` |
| Append into existing INTEGER `Age` | CAST to dest (`int`) |

**Files**

- `README.md` — one paragraph under Config
- XML remarks on `ImportAsync` / `ImportFromCsv`
- `DuckDbStore.CoerceStagingColumns` — **no code change** except maybe a comment
- `tests/DataBall.Tests/IntendedBehaviorTests.cs` — new lock tests

**Steps**

- Document; do not add BIGINT→INTEGER sniffing.
- Leave `Convert.ToInt32` in older tests (valid for both types).
- Never weaken `CsvImport_WithConfigAgeInt_ValuesAreInt32`.

**Tests to add**

- `CsvImport_WithoutConfig_AgeValuesAreInt64`
- `ImportAsync_CsvWithoutConfig_AgeValuesAreInt64`
- `CsvImport_WithConfigAgeLong_SmallValuesStayInt64`
- `CsvImport_PartialConfig_UntypedAgeStaysInt64`
- Optional: config `int` + value `2147483648` throws (strict CAST)

**Risks.** Callers using `is int` on untyped CSV are already wrong. New tests fail if someone later “helpfully” narrows.

**Out of scope.** Inferring float/bool; Parquet expected types; changing `Query` to coerce `long`→`int`.

---

## 3. Hive-partitioned Parquet directory import

**Goal.** `ImportAsync(hiveDir)` of a Squish/Bounce hive tree rebuilds `"data"` including partition columns.

**Why.** Export already `COPY ... PARTITION_BY ... WRITE_PARTITION_COLUMNS true`. Import is single-file `read_parquet`. `ImportAsync` requires `File.Exists`. Only a single partition *file* is tested today.

**Contract**

- Directory from hive export → all rows, all columns, parquet schema types (not VARCHAR keys).
- `ImportOptions.Append` same as single parquet.
- Single `.parquet` file unchanged.
- Hive is **not** `.ball`: Bounce metadata is not on disk in the hive tree.
- Empty dir / no `*.parquet` → `DataBallException`.
- CLI `databall import <hiveDir> -o out.ball` works via `ImportAsync`.

Use `read_parquet('<abs-dir>/**/*.parquet', hive_partitioning=false)` so path keys do not duplicate/widen in-file columns. Probe DuckDB 1.5.5 before coding. Do not `GetFullPath` a glob string; quote the directory, then append `/**/*.parquet`.

**Files**

- `src/DataBall/DuckDbStore.cs` — `ImportParquet` directory branch
- `src/DataBall/Import/ImportManager.cs` — `DetectImportFormat` for hive dirs
- `src/DataBall/DataBall.cs` — existence check for directories
- `src/DataBall.Cli/CliApp.cs` / `CliFormat.cs` — `info` format on a directory
- `README.md` — parquet row + example

**Steps**

1. Probe auto vs `hive_partitioning=false` on a DataBall hive tree.
2. Implement directory glob; throw if no parquet files.
3. Detect format + `ImportAsync` existence.
4. Fix CLI `info` if `DetectExportType` throws on a dir.
5. README.

**Tests** (`ImportExportTests` + CLI)

- Round-trip Squish `Site`+`Meas` via `ImportAsync(dir)`
- Nested two partition columns
- Only-partition-column hive
- Int partition stays `int`
- Append true/false
- Existing single-file hive test still passes
- Empty dir throws
- CLI import hive → query

**Risks.** Glob + `QuotePath`; hive is not metadata; `info` without a CLI change will throw.

**Out of scope.** Spark hive without written partition columns; S3; changing export COPY options.

---

## 4. SharpCompress bump (tar.xz export only if XZ write exists)

**Goal.** Leave 0.40 only if a bump breaks tar.gz. Prefer **0.50.4** (NU1902 GHSA-6c8g-7p36-r338 is ≤0.47.4). Re-verify tar.gz export and tar.xz import. **Do not add a second compressor.** SharpCompress 0.50 docs still say XZ is decompress-only — expect xz **export to stay rejected**, with the exception text no longer saying “0.40”.

**Why.** Pin comment in `DataBall.csproj`. We do not call `WriteToDirectory` (the vulnerable API). NuGet still warns. 0.50 tar APIs changed (`ArchiveFactory` may not open tar.gz; `ReaderFactory` fallback already exists). Unlisted SharpCompress **1.0.0** — do not use.

**Contract**

- One compressor: SharpCompress.
- `dotnet list package --vulnerable` does not list SharpCompress.
- `Archive_TarGz_RoundTrip` and `Archive_TarXz_Import_FromHandBuiltFile` pass.
- XZ export: keep `DataBallException` unless `TarWriter` actually compresses XZ; then add a SharpCompress-only round-trip and drop the throw.
- Keep `ZipSlipSafePath` and `IsXzArchivePath` → ReaderFactory.

**Files**

- `src/DataBall/DataBall.csproj` — version + comment
- `src/DataBall/Export/ExportManager.cs` — `EnsureArchiveExportSupported` / `GetArchiveFormat`
- `src/DataBall/Import/ImportManager.cs` — only if APIs moved
- `README.md`, `ExportType` archive xml-doc

**Steps**

1. Bump to 0.50.4 (fallback 0.49.1 / 0.48.1). Never 1.0.0.
2. Confirm XZ still decompress-only in that version’s docs.
3. Fix WriterFactory if renamed; if tar.gz export cannot be fixed inside SharpCompress, **revert the bump**.
4. Run archive tests; `dotnet list package --vulnerable`.
5. Update exception text / README.

**Tests.** Existing archive facts are the gate. CI needs `xz` on PATH for the import fixture (Windows: skip fixture if `xz` missing — see item 6).

**Risks.** 0.50 tar.gz writer regression; LeaveStreamOpen default; do not shell out to `xz` for export.

**Out of scope.** XZ.NET, GrindCore, `.tar.bz2`, using `WriteToDirectory`.

---

## 5. File-backed DuckDB

**Goal.** Default stays `:memory:`. Opt-in `databasePath` so one session can spill to a caller-owned `.duckdb`. Interchange remains `.ball`.

**Why.** Working table `"data"` is RAM-only. CSV already streams; the store does not. After items 1–2 (API hygiene).

**Contract**

```csharp
public DataBall(string? configPath = null, ILogger? logger = null, string? databasePath = null)
```

- Named-only file path: `new DataBall(databasePath: path)`. First `string` stays config JSON.
- `null`/empty → memory. Whitespace → `DataBallException`. `":memory:"` allowed.
- No `DataBallOptions`, memory_limit, threads, MotherDuck, auto-temp-file.
- `DuckDBConnectionStringBuilder`; `DataSource = Path.GetFullPath(path)`.
- `CREATE TABLE IF NOT EXISTS "meta"`; hydrate `_metadata` on reopen. `"data"` persists. Types/relationships still only from `configPath` / `.ball` config.
- Dispose **does not delete** the file; it must release the native lock. Caller owns delete.
- Second open while first is alive → `DataBallException` (DuckDB exclusive lock).
- Missing parent directory → fail; do not create it.
- **No CLI flag** in this slice (`CliApp` stays `new DataBall()`).

**Files**

- `src/DataBall/DuckDbStore.cs` — ctor, open wrap, IF NOT EXISTS, hydrate
- `src/DataBall/DataBall.cs` — third ctor param
- `README.md` — Engine + example
- `tests/DataBall.Tests/FileBackedStoreTests.cs` — new

**Steps**

1. File ctor on the store; dispose connection on open failure.
2. Wire `DataBall`.
3. Temp-dir tests; unique paths for parallel xunit.
4. README. No GB-scale fixture.

**Tests (must-haves)**

- File created; query works; dispose does not delete; `File.Delete` after dispose
- Reopen rows + metadata
- Overlapping second ctor throws
- Missing parent dir throws
- `new DataBall()` still memory
- File-backed Bounce / CSV import / `SaveAsync` `.ball`
- `configPath` + `databasePath` together

**Risks.** DuckDB.NET process-wide open (`File is already open`); Windows `.wal` blocking `Directory.Delete`; passing `.duckdb` as the first ctor arg is still “config JSON”.

**Out of scope.** CLI `--database`; public `DatabasePath`; persist relationships into the file; `.duckdb` as an export type.

---

## 6. CI / native RID proof

**Goal.** The existing `.github/workflows/ci.yml` matrix (`windows-latest`, `macos-latest`, `ubuntu-latest`) actually loads DuckDB natives. No new job types. No csproj `RuntimeIdentifier` unless testhost cannot copy natives.

**Why.** Bindings ship `linux-x64`, `linux-arm64`, `osx` (portable dylib), `win-x64`, `win-arm64`. Only linux-x64 is proven in this workspace. README Platforms currently over-claims.

**Contract**

- Keep restore → build Release → test Release. `fail-fast: false`.
- Library stays RID-agnostic; consumers get natives from `DuckDB.NET.Data.Full`.
- After green runs: README states **which** OS/RID actually ran (`windows-latest`=win-x64, `macos-latest`=osx-arm64 via `osx`, `ubuntu-latest`=linux-x64). win-arm64 / linux-arm64 = “in nupkg,” not “CI tested.”
- Windows likely lacks `xz`: skip `Archive_TarXz_Import_FromHandBuiltFile` when `xz` is not on PATH (xunit 2.5.3 has no `Assert.Skip` — early-return / omit fixture). Export-xz-throws must still run. Do not `choco install xz` unless you explicitly want that coverage.

**Files.** Workflow: verify first, edit only if required. `ImportExportTests.cs` if Windows `xz`. README Platforms after evidence.

**Steps.** Watch a three-OS run; fix native load or temp-delete-after-dispose in product, not with extra jobs; then README.

**Tests.** Every `new DataBall()` is a native-load test. After item 5, `FileBackedStoreTests` is the Windows lock canary.

**Out of scope.** Pack job; ARM extra matrix; self-contained publish.

---

## 7. Local pack checklist (no publish)

**Goal.** README Install is an honest local-pack procedure. No `dotnet nuget push`, no `git push`, no nuget.org.

**Why.** IDs `squalor.DataBall` / `squalor.DataBall.Cli` (`databall` tool) are packable; not on nuget.org. `dotnet add package squalor.DataBall` without `--source` 404s.

**Contract**

- Lead with “not on nuget.org.”
- Pack both projects `-c Release -o artifacts`.
- Inspect library nupkg: `lib/net10.0/DataBall.dll`, README, **no** `runtimes/` (natives from DuckDB.NET at consumer restore).
- Inspect tool nupkg: `tools/net10.0/any/` includes `DataBall.dll` + `runtimes/{linux-x64,linux-arm64,osx,win-x64,win-arm64}/native/`.
- Consume with `--source ./artifacts`; restore still needs nuget.org (or a mirror) for DuckDB.NET, SharpCompress, logging abstractions.
- Tool: uninstall first if 1.0.0 already installed; `databall --help`.
- Bold: no push, no API keys.

**Files.** `README.md` only (expand Install). csproj metadata already has license + readme.

**Tests.** None. Do not add a CI pack job.

**Risks.** Tool nupkg missing `runtimes/` → native load failure; version clash on global tool.

---

## Explicitly not on this roadmap

Hierarchical `DataFile`/`DataGroup`/`DataSweep` as an **object model** (the storage layout may be multi-table via config `tables`; the API stays rows + `"data"`), streaming `Query`, Polars, DataFrame, SQLite, MotherDuck, Spectre.Console, Coverlet, GB-scale fixtures, nuget.org listing.
