# DataBall

Versatile data handling library for test executive applications. Supports import/export in multiple formats (CSV, Parquet, SQLite, Archives, .ball), data manipulation via row builder pattern and DataFrame operations, metadata storage, configuration-based relationships, and large dataset handling via chunking/partitioning.

## Features
- Import and export data in CSV, Parquet, SQLite, archives (ZIP/TAR.GZ/TAR.XZ), and native .ball format.
- Row builder pattern with relationship propagation for data manipulation.
- Bounce and Squish operations for data compaction and partitioning.
- Backend abstraction for in-memory or out-of-core (Parquet) processing.
- Configuration-loaded metadata, column types, and relationships.

## Installation
Install via NuGet:
```bash
dotnet add package squalor.DataBall --version 1.0.0
```

## Usage
```csharp
using squalor.DataBall;

var db = new DataBall("config.json");

// Add columns and rows
db.AddColumn<string>("Name", new[] { "Alice", "Bob" });
db.AddColumn<int>("Age", new[] { 30, 25 });

// Bounce for compaction
db.Bounce();

// Export to CSV
db.Roll(ExportType.Csv, "output.csv");

// Import from CSV
db.ImportFromCsv("input.csv", append: true);
```

## Quick Start Guide

### 1. Configuration with `config.json`
When creating a new `DataBall` instance, you can optionally specify a `config.json` file to define metadata, column types, and relationships. The configuration file is in JSON format and has the following structure:

```json
{
  "metadata": {
    "Version": "1.0",
    "Source": "TestData"
  },
  "columns": {
    "Name": "string",
    "Age": "int",
    "Date": "datetime"
  },
  "relationships": [
    {
      "trigger": "Name",
      "reset": ["Age", "Date"]
    }
  ]
}
```

- **metadata**: Key-value pairs for constants (e.g., version, source info).
- **columns**: Column names with types (`int`, `long`, `float`, `double`, `bool`, `datetime`, `string`).
- **relationships**: Defines fields that reset to null when a trigger field changes (used in row builder).

If no `config.json` is provided, `DataBall` starts with an empty `DataFrame` and no predefined types or relationships.

### 2. Building Datasets
`DataBall` supports two primary ways to build datasets:

#### a. Direct Column and Row Addition
Use `AddColumn<T>` and `AddRow` for direct data manipulation:
```csharp
var db = new DataBall();
db.AddColumn<string>("Name", new[] { "Alice", "Bob" });
db.AddColumn<int>("Age", new[] { 30, 25 });
db.AddRow(new object?[] { "Charlie", 40 });
```

#### b. Row Builder Pattern
For controlled data entry with relationship propagation:
```csharp
var db = new DataBall("config.json");
db.InitializeRow(new Dictionary<string, object?> { { "Name", "Alice" }, { "Age", 30 } });
db.ModifyField("Name", "Bob"); // Triggers relationship reset if configured
db.CommitRow(); // Adds row to DataFrame
```

The row builder ensures relationships (e.g., resetting dependent fields when a trigger changes) are applied before committing.

### 3. DataFrame Operations
`DataBall` uses `Microsoft.Data.Analysis.DataFrame` for in-memory storage, supporting standard operations like filtering, grouping, joining, and sorting transparently. See the [Microsoft.Data.Analysis documentation](https://docs.microsoft.com/en-us/dotnet/api/microsoft.data.analysis.dataframe) for details.

Example:
```csharp
var filtered = db.Filter(row => (int?)row["Age"] > 25);
var grouped = db.GroupBy(new[] { "Name" });
var sorted = db.Sort(new[] { "Age" });
```

### 4. Order of Operations
To optimize data processing, follow this order:
1. **Load Configuration**: Use `new DataBall("config.json")` to set up metadata, types, and relationships.
2. **Import Data**: Use `ImportFrom*` methods (e.g., `ImportFromCsv`) to load data.
3. **Manipulate Data**: Use `AddColumn`, `AddRow`, or row builder (`InitializeRow`, `ModifyField`, `CommitRow`).
4. **Bounce**: Call `Bounce()` to compact data (extract constants, deduplicate rows, limited chunk deduplication).
5. **Export/Save**: Use `Roll` or `Save` to export to desired format.
6. **Squish (Optional)**: For large datasets, use `Squish` to partition and deduplicate for disk storage.

### 5. Export Operations
Export data using the `Roll` method with supported formats (`ExportType`):
```csharp
db.Roll(ExportType.Csv, "output.csv");
db.Roll(ExportType.Parquet, "output.parquet", new[] { "Name" }); // Partitioned
db.Roll(ExportType.Sqlite, "output.db");
db.Roll(ExportType.Archive, "output.tar.gz");
db.Roll(ExportType.DataBall, "output.ball"); // Native .ball format
```

- **CSV**: Simple tabular export with chunking support.
- **Parquet**: Efficient columnar storage, supports partitioning.
- **SQLite**: Relational database export.
- **Archive**: ZIP/TAR.GZ/TAR.XZ with CSVs, supports partitioning.
- **DataBall (.ball)**: ZIP with Parquet data and JSON metadata.

### 6. Import Operations
Import data from various formats:
```csharp
db.ImportFromCsv("input.csv", append: true, chunkSize: 1000); // Chunked import
db.ImportFromParquet("input.parquet", append: true);
db.ImportFromSqlite("input.db");
db.ImportFromArchive("input.tar.gz");
db.ImportFromDataBall("input.ball");
```

- Set `append: true` to add to existing data; `false` to replace.
- `chunkSize` enables streaming for large CSVs.

### 7. Bounce
The `Bounce` operation optimizes in-memory data:
- Extracts constant columns to metadata.
- Deduplicates rows.
- Performs limited chunk deduplication (max 5 tables, 4x4 to 1000x1000 matrices).
- Optionally exports to partitioned Parquet.

Use before `Roll` or `Save` for smaller memory footprint:
```csharp
db.Bounce("output_dir", new[] { "Name" });
```

### 8. Squish and Save
`Squish` is designed for large datasets, performing full deduplication and partitioning for disk storage:
```csharp
db.Squish("output_dir", new[] { "Name" });
```

`Save` exports to the native .ball format (ZIP with Parquet and metadata):
```csharp
db.Save("output.ball", new[] { "Name" });
```

Use `Squish` when minimizing disk usage; `Save` for portable .ball files.

### 9. Adding Rows with Row Builder
The row builder pattern ensures controlled data entry:
```csharp
db.InitializeRow(new Dictionary<string, object?> { { "Name", "Alice" } });
db.ModifyField("Age", 30);
db.CommitRow();
```

- `InitializeRow`: Starts a new row, optionally copying the last row or setting initial values.
- `ModifyField`: Updates fields, marking them as modified.
- `CommitRow`: Applies relationships and adds the row to the `DataFrame`.

### 10. Other Prominent Features
- **Backend Switching**: Switch to `ParquetBackend` for large datasets:
  ```csharp
  db.SwitchBackend(new ParquetBackend("data.parquet"));
  ```
- **Metadata**: Store and retrieve constants or configuration data:
  ```csharp
  db.Metadata["Source"] = "TestData";
  ```
- **Type Coercion**: Automatically coerces data to expected types during import or row commits.
- **Logging**: Uses NLog for detailed logging (see `NLog.config`).

## FAQ

**Q: What if I don't have a `config.json`?**
A: You can create a `DataBall` instance without a config file. Columns and types are inferred dynamically, but you lose predefined relationships and metadata.

**Q: When should I use `Bounce` vs `Squish`?**
A: Use `Bounce` for in-memory optimization before export. Use `Squish` for large datasets requiring full deduplication and partitioning for disk storage.

**Q: Can I use advanced DataFrame operations?**
A: Yes, `DataBall.Data` exposes the full `DataFrame` API. See Microsoft.Data.Analysis docs for advanced operations.

**Q: How does partitioning work?**
A: For Parquet and .ball exports, specify `partitionColumns` to group data by unique values, creating subdirectories (e.g., `Name=Alice/part-0.parquet`).

**Q: What is the .ball format?**
A: A ZIP file containing Parquet data (partitioned or single) and a `metadata.json` file with constants and configuration.

## License
Mozilla Public License Version 2.0 (MPL-2.0). See [LICENSE](LICENSE) for details.

## Repository
https://github.com/squalor-xyz/DataBall
