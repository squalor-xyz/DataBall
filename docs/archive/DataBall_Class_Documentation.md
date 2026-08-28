# DataBall Class Documentation

## Overview
The `DataBall` class is a .NET Core data handling utility designed for test executive applications. It manages data in an efficient in-memory structure using `Microsoft.Data.Analysis.DataFrame`, supports various import/export formats, data manipulation, custom metadata, and configuration-driven behaviors like field relationships. It's particularly suited for test and measurement scenarios where data needs to be created, modified, and persisted flexibly. For large datasets (10s of GB), use chunked imports and partitioned exports to manage memory.

## Key Features
### Data Storage
- **Core Data**: Stored in a `DataFrame` for tabular data (in-memory; for large data, export to partitioned Parquet).
- **Metadata**: A `Dictionary<string, object>` for custom key-value pairs (global constants).

### Import Capabilities
- **Formats Supported**:
  - CSV files (with optional chunkSize for large files).
  - Parquet files (single or partitioned directory).
  - SQLite databases (from a specified table, default "data").
  - Archives: ZIP, TAR.GZ, TAR.XZ containing CSV files (imports and appends CSVs inside).
  - Another DataBall: A ZIP file with "data.parquet" or partitioned parquets and "metadata.json" for metadata.
- **Behavior**: Can append/merge to existing data. For append, aligns schemas by adding missing columns with nulls. Checks metadata consistency: If new data matches existing metadata values, removes column; if differs, moves to main data columns. For large CSV, chunked loading to avoid OOM. For partitioned Parquet, reads all files recursively.

### Export Capabilities
- **Formats Supported**:
  - CSV.
  - Parquet (optional partitioning by columns for large data; writes to subdirs like col=val/data.parquet).
  - SQLite (to a specified table, default "data").
  - Archive: ZIP with a single "data.csv" (can specify compression; extendable for TAR.GZ/TAR.XZ).
  - DataBall: ZIP with "data.parquet" or partitioned parquets and "metadata.json" (.ball extension).

### Data Manipulation
- **Columns**: Add (with values), Remove.
- **Rows**: Add (direct), Remove by index, Set value by row/column.
- **Row Builder Pattern**:
  - `InitializeRow(optional Dictionary<string, object> initialValues)`: Starts a new row, propagating values from the last row if exists.
  - `ModifyField(string field, object value)`: Updates or adds a field in the pending row.
  - `Roll()`: Commits the row to the DataFrame.
    - Adds new columns if needed (infers type or uses expected from config).
    - Applies relationships: If a trigger field changed, resets dependent fields to null unless explicitly modified.
- **Bounce Operation**: `Bounce(optional string partitionedParquetPath, optional string[] partitionColumns)`: Compacts data by identifying and removing constant columns to metadata, drops duplicate rows. Optionally exports to partitioned Parquet.
- **Save Operation**: `Save(optional string filePath, optional string[] partitionColumns)`: Performs Bounce, then saves to .ball (ZIP bundle with metadata.json and Parquet data, partitioned if specified). If filePath null, uses current or throws.

### Configuration
- **Loading**: Optional JSON config file on instantiation.
  - **Metadata**: Key-value pairs to preload (assumed constants).
  - **Columns**: Dictionary of column names to types (e.g., "int", "string").
  - **Relationships**: List of objects with "trigger" (field name) and "reset" (list of dependent fields).
- **Expected Types**: Used for new columns; defaults to string if unknown.
- **Initialization**: If no data, creates empty columns based on config.

## Implementation Details
- **Namespace**: squalor.DataBall
- **Dependencies**: Microsoft.Data.Analysis, Parquet.Net, SharpCompress, System.Text.Json, Microsoft.Data.Sqlite.
- **Relationships Logic**: Tracks original vs. pending values and modifications. On change in trigger, sets dependents to null if not modified.
- **Merge/Append**: Clones and rebuilds DataFrame for schema alignment; handles metadata consistency.
- **Type Handling**: Supports primitives (int, long, float, double, bool, DateTime) and string; uses nullables for missing values.
- **Exceptions**: Throws if modifying without initialized row or save without path.
- **Unit Tests**: Comprehensive tests cover constructor, row builder (propagation, relationships), manipulations, imports/exports, merging, save.
- **Optimizations in Bounce**: Constant column extraction to metadata; deduplication. Future: Normalization for repeating fields, auto-partition.
- **Large Data Support**: Chunked CSV import; partitioned Parquet export/import to handle data > RAM by processing groups/files. For 10s GB, ensure sufficient RAM for groups; future lazy loading.

## Future Implementation Plans
- **Enhancements**:
  - Full support for TAR.GZ/TAR.XZ in exports (currently ZIP-focused).
  - Advanced merging: Handle type conflicts with coercion or errors.
  - Performance optimizations for large datasets (e.g., lazy loading from partitioned Parquet, chunked processing for all imports).
  - Querying: Add methods for filtering, grouping using DataFrame APIs.
  - Bounce: Add normalization for low-cardinality columns to metadata DF, automatic partition column selection.
  - Apache Arrow: Direct methods for Arrow file support.
- **Additional Features**:
  - Multi-table support in SQLite.
  - Timestamping and versioning for rows/metadata.
  - Integration with test tools: e.g., automatic unit columns, validation rules.
  - Export config/relationships in DataBall format.
  - Error handling: More robust validation on imports, type mismatches.
  - Polars: Optional interop if performance critical.
  - Disk-backed mode for out-of-core operations.
- **Discussed Iterations**:
  - Iterative development: Start basic import/export, add row builder, config and relationships, namespace update, tests, data backing proposals, Polars/Parquet proposals, bounce operation, partitioned Parquet on save, large data support.
  - Focus on efficiency and usability for test/measurement apps.
  - Explored Polars: No native .NET; not integrated.
  - Parquet: For persistence/partitioning in export/bounce/save (.ball as ZIP bundle).
  - No full code dumps in dumps; recreate via instructions.

## Usage Example
using System.Collections.Generic;

var db = new DataBall("config.json");
db.ImportFromCsv("large.csv", chunkSize: 100000); // Chunk for large
db.InitializeRow(new Dictionary<string, object> { {"Col1", 1} });
db.ModifyField("Col2", "value");
db.Roll();
db.Bounce("partitioned_data", new[] { "Category" }); // Compact and partition export
db.Save("mydata", new[] { "Category" }); // Bounce and save to mydata.ball as partitioned ZIP
// Then import/export as needed.

This document captures the state as of October 08, 2025.