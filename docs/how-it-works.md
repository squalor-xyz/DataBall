# How DataBall works

Diagrams of the shipped library (1.3.0 in tree). Each one shows a single idea. Plain-language labels come first and the method or type name follows in small text. See the [README](../README.md) for the full rules. Source paths are relative to `src/DataBall/`.

## 1. The pieces

The CLI is only a thin shell around the library, and lab formats plug in from outside it. All state sits in one DuckDB database per session.

```mermaid
flowchart TD
    CLI["databall CLI<br/><small>DataBall.Cli</small>"] --> API
    Host["Your app"] --> API
    Handlers["Lab format handlers<br/><small>DataBall.Handlers</small>"] -. "RegisterHandler" .-> API
    API["DataBall session<br/><small>DataBall class</small>"] --> Schema["Schema rules<br/><small>Config, SchemaResolver</small>"]
    API --> IO["File in / out<br/><small>ImportManager, ExportManager</small>"]
    API --> Store["Store<br/><small>DuckDbStore</small>"]
    IO --> Store
    Store --> DuckDB[("DuckDB<br/>in-memory or file")]
```

Every session holds two relations: **data** (the rows) and **meta** (key/value constants).

## 2. A session's life

```mermaid
flowchart LR
    Open["Open<br/><small>new DataBall / Open</small>"] --> Write["Add rows<br/><small>AddRows, CommitRow,<br/>ImportAsync</small>"]
    Write --> Compact["Compact<br/><small>Bounce</small>"]
    Compact --> Read["Read<br/><small>Query, Filter</small>"]
    Write --> Read
    Read --> Out["Save / export<br/><small>SaveAsync .ball,<br/>ExportAsync</small>"]
```

Compacting is optional. `ApplyFilter` limits what export and save write out.

## 3. Where the data lives

The working store and the file you share are two different things. By default DuckDB lives only in memory. A `.ball` is the portable copy.

```mermaid
flowchart LR
    subgraph store["Working store (DuckDB)"]
        Mem["In memory<br/><small>default; gone on Dispose</small>"]
        File["session.duckdb<br/><small>opt-in databasePath;<br/>kept after Dispose</small>"]
    end
    store -->|"SaveAsync"| Ball["session.ball<br/><small>zip of parquet + json</small>"]
    Ball -->|"Open / ImportAsync"| New["New session<br/><small>fresh store</small>"]
```

- **Share or move data as `.ball`.** Any tool can read it: unzip it and open the parquet. It is what hosts hand to each other.
- **A `.duckdb` file is the working engine, not an interchange format.** Reopen it with `new DataBall(databasePath: ...)`. A layout session needs the same `tables` config. Never point this at `catalog.duckdb`.

## 4. Imports are all-or-nothing

Every import runs inside one transaction. If it fails, the session is left exactly as it was, and that includes config and metadata. Source: `DataBall.cs` (`RunImport`).

```mermaid
flowchart TD
    A["ImportAsync(path)"] --> B["Pick format from extension<br/><small>csv / parquet / zip·tar / .ball</small>"]
    B --> C["Snapshot session state<br/><small>config, types, layout</small>"]
    C --> D["Begin transaction"]
    D --> E["Load rows"]
    E --> F["Split into tables<br/><small>only if config declares tables</small>"]
    F --> G{"OK?"}
    G -- yes --> H["Commit"]
    G -- no --> I["Roll back + restore snapshot"]
```

## 5. CSV headers become typed columns

A header such as `EVM(dB)` becomes a column named `EVM` of type double. Source: `HeaderParser.cs`, `SchemaResolver.cs`, `Config.cs`.

```mermaid
flowchart TD
    H["Header, e.g. EVM(dB)"] --> P["Split name + unit<br/><small>name(unit) → name_unit* → name</small>"]
    P --> T["Type<br/><small>config columns → parameters → unit table</small>"]
    P --> R["Role<br/><small>stimulus / classification / metadata / meas (default)</small>"]
    R --> M{"Metadata field<br/>and constant?"}
    M -- yes --> Meta["Move to meta"]
    M -- no --> Col["Stays a column"]
```

\* `name_unit` splits only when the suffix is a known unit, so `I_Total` stays whole.

Metadata fields (defaults `Lot`, `Tester`, `Program`) follow `metadataPolicy`. Under `requireConstant`, a field that varies throws. Under `first`, the first value always moves to meta.

## 6. Row builder: carry forward, reset on change

A test executive changes one setup value at a time. `InitializeRow` copies the previous row. When a **trigger** field changes, the row builder clears the fields that depend on it. Source: `DataBall.RowBuilder.cs`.

```mermaid
flowchart LR
    A["InitializeRow<br/><small>copy last row</small>"] --> B["ModifyField<br/><small>set values</small>"]
    B --> C{"Trigger field<br/>changed?"}
    C -- yes --> D["Clear dependents<br/><small>unless set this row</small>"]
    C -- no --> E["CommitRow"]
    D --> E
```

Relationships come from config, for example `{ "trigger": "Name", "reset": ["Age"] }`. `AddRow` / `AddRows` skip this logic and store rows exactly as given.

## 7. Bounce: move constants out, drop duplicates

Source: `DataBall.cs` (`Bounce`). `Squish` is the same call.

```mermaid
flowchart TD
    A["Each column"] --> B{"One value,<br/>no nulls?"}
    B -- yes --> C["Move to meta,<br/>drop column"]
    B -- no --> D["Keep"]
    C --> E["DISTINCT remaining rows"]
    D --> E
    E --> F["Optional: write hive parquet<br/><small>path + partition columns</small>"]
```

Partition columns and declared table `key` columns are never moved out. Row order is not kept.

## 8. Multi-table layout

With a `tables` section in config, the rows are stored split across several tables. **data** becomes a view that joins them back into the wide shape, so readers never notice. The example below uses the config from the README.

```mermaid
erDiagram
    device ||--o{ sweep : "device_key"
    setup  ||--o{ sweep : "setup_key"
    sweep  ||--o| rf    : "_row"
    sweep  ||--o| dc    : "_row"
    device {
        key device_key
        col SN
    }
    setup {
        key setup_key
        col Teststand
        col Temp
        col Vcc
    }
    sweep {
        key _row
        key device_key
        key setup_key
        col Frequency
        col Date
    }
    rf {
        key _row
        col Pout
        col Pin
        col Gain
        col EVM
    }
    dc {
        key _row
        col I_Total
    }
```

| Kind | Stores | Example |
|---|---|---|
| `master` / `dimension` | each distinct setup once, keyed by a hash of its key columns | `device`, `setup` |
| `rows` (spine) | one row per measurement point: row key `_row` plus dimension keys | `sweep` |
| `measurements` | a column group. Rows where the whole group is NULL are skipped | `rf`, `dc` |

## 9. Writing into a layout

Every write path (import, `AddRows`, `CommitRow`, merge) funnels through one routine. Source: `DuckDbStore.Layout.cs` (`AppendStagingIntoLayout`).

```mermaid
flowchart TD
    A["Incoming rows<br/><small>staged in a temp table</small>"] --> B["Reconcile metadata<br/><small>a value that now varies becomes a column</small>"]
    B --> C["Add new columns<br/><small>spine / measurement groups</small>"]
    C --> D["Cast to stored types"]
    D --> E["Dimensions: reuse row with same hash key"]
    E --> F["Spine, then measurement groups"]
```

An append that would add a column to a dimension, add a new dimension, or turn varying metadata into a dimension or group column re-splits the session: the layout is materialized wide in row order, the new columns and rows are added, and the result is split again in the same transaction. `_row` is renumbered and dimension keys may change, so neither is stable (a declared-`key` dimension keeps its hash-of-key surrogate; an undeclared-key dimension gets new keys when a column is added). Existing rows get NULL in a new dimension column, so an appended row (including a `CommitRow` copy of the last row) that reuses an existing declared key with a non-NULL value there throws, because the key no longer determines the row; the session is left as it was. The re-split rewrites the whole session, so its cost grows with session size and adding a dimension column mid-capture is expensive; later appends of the same shape take the in-place path.

## 10. Whole-table edits on a layout

`AddColumn`, `RemoveColumn`, and `Bounce` all work on the wide shape. On a layout session they unsplit, apply the edit, and re-split, all inside one transaction. Source: `DataBall.cs` (`OnWideData`).

```mermaid
flowchart LR
    A["Tables"] -->|"unsplit<br/><small>in row order</small>"| B["One wide table"]
    B -->|"AddColumn / RemoveColumn / Bounce"| C["Edited wide table"]
    C -->|"re-split"| D["Tables"]
```

`_row` gets renumbered, so do not treat it as a stable key.

## 11. The .ball file

A `.ball` is a ZIP archive. v2 (layout sessions) adds per-table parquet files but keeps the wide `data.parquet`, so 1.2.0 readers still work. Source: `Export/ExportManager.cs`, `Import/ImportManager.cs`.

```mermaid
flowchart LR
    subgraph ball["session.ball (zip)"]
        meta["metadata.json"]
        data["data.parquet<br/><small>wide rows</small>"]
        cfg["config.json<br/><small>optional</small>"]
        man["manifest.json<br/><small>v2</small>"]
        tbl["tables/*.parquet<br/><small>v2</small>"]
    end
```

```mermaid
flowchart TD
    A["Open .ball"] --> B["Apply config.json"]
    B --> C{"v2 manifest matches<br/>session tables?"}
    C -- yes --> D["Load tables/*.parquet"]
    C -- no --> E["Load data.parquet<br/>and re-split if needed"]
    D --> F["Apply metadata.json"]
    E --> F
```
