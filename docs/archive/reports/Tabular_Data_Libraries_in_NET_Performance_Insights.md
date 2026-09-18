# Tabular Data Libraries in .NET: Performance Insights

## Overview
Summary of .NET libraries for tabular data, based on comparisons.

## Key Libraries
- **DataTable**: Simple but slower for large comparisons/loops.
- **DataFrame (Microsoft.Data.Analysis)**: Arrow-backed, performant for analytics; chunked for large.
- **Others**: TabNet/XGBoost for ML on tabular; not general storage.

## Benchmarks
- Arrow-based libs outperform row-oriented.
- For data access: Dapper faster than EF, but not tabular.
- Irregular data: GBDTs > NNs.

## Recommendations for DataBall
Favor Arrow-integrated solutions; use partitioning for large data.