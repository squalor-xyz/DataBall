# Proposal for Parquet as Data Backing in DataBall

## Background
Parquet is a columnar file format for storage, not in-memory structure.

## Proposal
- **Usage**: For persistence/partitioning, not core backing.
- **Approach**: In bounce/export/save, write to (partitioned) Parquet, lazy load if needed for large data.
- **Performance**: Excellent compression/I/O for large datasets.
- **Pros**: Saves memory by disk spill; partitioning for query efficiency.
- **Cons**: I/O overhead for ops.
- **Recommendation**: Use for export/overflow in save operations; keep DataFrame in-memory. Implemented in ExportToParquet with partitioning, and Save using ZIP for .ball.