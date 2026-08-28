# Bounce Operation Details

The "bounce" operation in DataBall compacts and optimizes the in-memory data for better storage and performance.

### Method
- **Bounce(string partitionedParquetPath = null, string[] partitionColumns = null)**: Performs optimizations.
  - Identifies constant columns (all non-null values equal), extracts value to metadata, removes column.
  - Drops duplicate rows.
  - Optionally exports to partitioned Parquet for large data persistence.

### Optimizations Implemented
- Constant Removal: Moves constants to metadata to reduce DataFrame size.
- Deduplication: Removes identical rows.
- Metadata Handling in Merge: During append, checks if new data matches metadata; if yes, removes column; if no, promotes to main column with values filled.

### Benefits
- Memory savings by eliminating redundancy.
- Faster ops on slimmer DataFrame.
- Prepares for large data by disk spill via partitioned Parquet.

### Future Enhancements
- Normalization: For repeating/low-cardinality columns, create metadata DF with unique combos, link via ID.
- Partitioning: Auto-select columns (e.g., categorical) if not provided.
- Compression: Apply Arrow compression.

### Example
using System;

db.Bounce("partitioned_data", new[] { "Category" }); // Compact and partition export