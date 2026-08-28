# Data Backing Proposal for DataBall

## Introduction
This document proposes the optimal internal data storage for the DataBall class, focusing on universality, memory efficiency, and performance.

## Proposed Solution
- **Backing Format**: Microsoft.Data.Analysis.DataFrame (Arrow-based).
- **Rationale**: Provides mutable, high-level API with columnar efficiency. Universal for imports/exports.
- **Benefits**:
  - Memory: Columnar; for large, use chunked imports/partitioned exports.
  - Performance: Vectorized; supports GB-scale in RAM.
  - Universal: Converts from various formats.
- **Alternatives**: DataTable (slower), pure Arrow (complex mutations).
- **Implementation**: Retained; added chunking for large imports, partitioning for exports.

## Risks and Mitigations
- Risk: OOM for 10s GB—Mitigate with chunked processing, disk spill via partitioned Parquet.
- Future: Lazy/out-of-core mode loading partitions on-demand.