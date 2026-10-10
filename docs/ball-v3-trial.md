# db-05: `.ball` as a DuckDB file

Trial for the db-06 specification; recommendations below need the owner's decision.
No production `Open`, `SaveAsync`, export, or file-format behavior changes here.

## Method and scope

Measured on macOS 15.7.3, Arm64, using DuckDB.NET.Data.Full **1.5.5** and
`SELECT version()` = **v1.5.5**. The four tests are in
[`BallDuckDbTrialTests.cs`](../tests/DataBall.Tests/BallDuckDbTrialTests.cs).

The deterministic fixture contains 50,000 rows: 500 `groupId` values, each with
100 `sweepId` values, plus double-valued Pout, EVM, and current. The measurement
formulas repeat small sets of values; these are synthetic, compressible capture
rows, not a representative noise distribution or a GB-scale benchmark.

The same rows are written to a wide, file-backed `new DataBall(databasePath: …)`
session, today's ZIP `.ball` through `SaveAsync`, and Parquet through
`ExportAsync`. No compression options are supplied. Sizes are bytes on disk
after closing the DuckDB writer and finishing both exports, not allocated disk
blocks or peak working space. There is no user metadata in this fixture.

The child is a small test-only console project,
[`DataBall.TrialProbe`](../tests/DataBall.TrialProbe/Program.cs), included in the
solution and built/copied alongside the tests. It reuses the existing DataBall
project's DuckDB dependency; no packages were added. This avoids custom entry
arguments and startup behavior in the VSTest host. The existing CLI imports
files, so it cannot measure a direct native database open. The helper opens
the **same absolute path** with `ACCESS_MODE=READ_ONLY`, reports its PID, checks
the actual access mode on successful open, and counts `data`. A DuckDB open
error is logged as an outcome; a broken helper invocation fails the test.

## Observations

| Observation | Result |
| --- | --- |
| Wide file-backed DuckDB | 798,720 bytes |
| ZIP `.ball` via `SaveAsync` | 14,839 bytes |
| Parquet via `ExportAsync` | 28,451 bytes |
| DuckDB / ZIP `.ball` | 53.825729 |
| DuckDB / Parquet | 28.073530 |
| Reopen after writer disposal | All 50,000 rows and five columns equal to the generated source rows and the wide in-memory session, including column names and values |
| Storage tag from `duckdb_databases()` | `v1.0.0+` |
| Numeric storage header version | 64 |
| DuckDB library version | `v1.5.5` |

Every DuckDB main-file size measured here consists of whole 262,144-byte blocks
plus 12,288 header bytes: 798,720 = 12,288 + 3 × 262,144;
1,323,008 = 12,288 + 5 × 262,144; and 2,633,728 = 12,288 + 10 × 262,144.
At 50,000 rows, the size ratios are dominated by this block granularity; they
are not per-row or at-scale estimates. The at-scale ratio is unmeasured.

The storage query is
`SELECT version(), tags['storage_version'] FROM duckdb_databases() WHERE path IS NOT NULL`.
After checking that bytes 8–11 are `DUCK`, the numeric version is also read
from bytes 12–19 of the closed file using the
[documented storage header](https://duckdb.org/docs/current/internals/storage.html#storage-header).

The read-only child failed while the writer held the file.
The orchestrator's normal `dotnet test` run reported this DuckDB error;
the test-output path, GUID, PID, and user are replaced with placeholders:

```text
DuckDBOpen failed: IO Error: Could not set lock on file "<test-output>/ball-trial-<guid>/capture.duckdb": Conflicting lock is held in /usr/local/share/dotnet/dotnet (PID <pid>) by user <user>. See also https://duckdb.org/docs/stable/connect/concurrency
```

After writer disposal, a second child opened successfully, reported
`accessMode=read_only`, and read 50,000 rows. The tests log both outcomes without
asserting success, failure, or an environment-specific error string.

### Dimension-growth append

A separate 50,000-row file uses a group dimension and a measurements table.
Its config also declares an initially absent `environment` dimension for
`Humidity`. After an initial `CHECKPOINT`, appending one row with `Humidity=40`
creates that dimension and exercises the db-02 rematerialize/re-split path.

| Stage | Main file bytes | WAL bytes |
| --- | ---: | ---: |
| Before append, after initial `CHECKPOINT` | 1,323,008 | — |
| After dimension-growth append, writer still open | 1,323,008 | 3,215,353 |
| After explicit `CHECKPOINT`, writer still open | 2,633,728 | 0 |
| After writer disposal | 2,633,728 | — |

The view then has 50,001 rows, and the new dimension has two rows.
The main file **did not shrink** at
the measured checkpoint. Measuring only the main file immediately after append
would omit 3,215,353 WAL bytes. This does not measure peak rewrite space,
repeated growth, arbitrary drops, `VACUUM`, or eventual block reclamation; those
remain unverified here.

## Documentation and compatibility

DuckDB documents a default numeric storage version of 64 for engines v1.0–v1.5,
and supports opting into newer storage versions. An explicit storage version
sets the minimum reader version. Newer engines support older storage starting
with the v0.10 compatibility work; older engines reading newer files is only
best effort. These are documentation statements, not cross-version results from
this trial. See
[storage compatibility and versions](https://duckdb.org/docs/current/internals/storage.html#compatibility),
[default storage version](https://duckdb.org/docs/current/internals/storage.html#default-storage-version), and
[explicit storage versions](https://duckdb.org/docs/current/internals/storage.html#explicit-storage-versions).

For native in-process access, DuckDB documents one process reading/writing, or
multiple read-only processes with no writer. See
[concurrency](https://duckdb.org/docs/current/connect/concurrency.html#single-process).
The measured writer/child lock result agrees with that model. The provider
documents the connection-string setting used by the child in
[connection string parameters](https://duckdb.net/docs/connection-string.html).

## Recommendations for db-06

| Question | Recommendation and basis |
| --- | --- |
| Read-only default on open | Default native parked-file reads to read-only, with explicit writable capture/reopen. The child successfully reads a closed file this way. Today's DataBall constructor initializes `meta` through a writable connection, so db-06 needs a dedicated read-only path; this slice has not implemented or tested that API. |
| Reader sees an in-progress capture | Have the writer publish a separate completed, closed snapshot generation; readers open that generation read-only. Do not promise direct cross-process reads of the active capture file: this trial gets a conflicting lock. Snapshot creation, publication, cadence, and concurrent capture consistency need db-06 design and tests; copying a live main file alone is not established as safe here. |
| Compatibility stance | Keep the initial default storage version (observed header 64), record the writer engine/storage versions, and require an explicit policy before opting into newer storage. State a tested reader floor only after testing those engines; report unsupported files with upgrade/export guidance. The `v1.0.0+` tag is not a measured guarantee that every older engine can read every DataBall file. |
| ZIP fallback | Retain import support for existing ZIP `.ball` files and offer explicit ZIP export for interchange/compact archives. Avoid automatic ZIP substitution for a native capture file. This small synthetic fixture gives a lower ZIP size, but the ZIP-fallback decision needs a measurement at realistic capture size; the at-scale ratio is unmeasured. The native file round-trips and supports the file-backed session. Whether the archive export keeps the `.ball` suffix or gains a distinct name remains the owner's db-06 decision. |

These recommendations are proposals, not shipped behavior. Storage and lock
results do not settle noisy-data sizes, Windows/Linux/network-filesystem locks,
multi-reader concurrency, older-engine compatibility, or a live-snapshot API.

## Reproduction and validation limits

Run `dotnet build DataBall.sln`, then
`dotnet test DataBall.sln --filter FullyQualifiedName~BallDuckDbTrialTests --logger "console;verbosity=detailed"`
and the full `dotnet test DataBall.sln`. All four trial outputs should appear.
The three logged tests have no measurement assertions; the round-trip asserts
the complete ordered row/column content against both the generated source rows
and the wide session. The storage probe also checks the header magic.

`dotnet test` (VSTest) on the trial filter passed **4/4 in Debug and in Release**
in the orchestrator and reviewer runs.
Validation limits: macOS Arm64 only, synthetic data.
