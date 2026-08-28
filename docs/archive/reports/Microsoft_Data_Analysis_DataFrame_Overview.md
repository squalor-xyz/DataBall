# Microsoft.Data.Analysis DataFrame Overview

## Description
A tabular data structure in .NET for analysis, built on Apache Arrow.

## Features
- **Mutable Operations**: Append/remove rows/columns, set values.
- **Interoperability**: Load from CSV (chunked for large), convert to/from Arrow.
- **Types**: Supports primitives, strings, nulls.

## Performance
- Leverages Arrow for efficiency.
- Suitable for in-memory datasets (GB-scale); integrates with ML.NET.

## Comparison to Pure Arrow
- DataFrame: Higher-level, mutable.
- Arrow: Low-level, immutable—better for static data.

## Usage in DataBall
Core storage; handles ops efficiently. Chunked for large imports.