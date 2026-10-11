// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using squalor.DataBall.Import;

namespace squalor.DataBall
{
    public sealed partial class DataBall
    {
        /// <summary>How a source of a many-file open is read.</summary>
        private enum SourceRoute
        {
            Csv,
            Parquet,
            Hive,
            Archive,
            Handler,
        }

        /// <summary>
        /// Opens two or more sources into one session: every source is staged in its own table (TEMP in a memory store, a regular table in
        /// the temporary file store, which spills to disk), then one merge builds
        /// <c>"data"</c> and the <c>source</c> table, and one split follows. A failure disposes the session; nothing is left behind.
        /// </summary>
        private static async Task<DataBall> OpenManyAsync(OpenPlan plan, OpenOptions options, CancellationToken cancellationToken)
        {
            foreach (var entry in plan.Entries)
            {
                if (entry.Kind == OpenKind.Pointer)
                    throw new DataBallException($"'{entry.Path}' is a snapshot pointer; a snapshot cannot be opened together with other inputs yet, open it on its own");
                if (entry.Kind != OpenKind.Detected || entry.Report!.BuiltInPath is BuiltInPath.NativeBall or BuiltInPath.BallImport)
                    throw new DataBallException($"'{entry.Path}' is a native .ball; native sources cannot be opened together with other inputs yet, open it on its own");
            }
            var (effective, store, routes) = ResolveOpenConfig(plan.Entries, plan.Bytes, options);

            var db = new DataBall(effective, options.Engine, temporary: store == StoreMode.File);
            var extractions = new List<string>();
            try
            {
                var staged = await db.StageSourcesAsync(routes, extractions, cancellationToken).ConfigureAwait(false);
                db.RunImport(false, () => db.MergeStagedSources(staged));
                db.OpenReport = db.ReportSources(plan.Ignored, staged.ToDictionary(s => s.Key, s => s.Rows));
                return db;
            }
            catch (Exception ex)
            {
                db.Dispose();
                if (ex is DataBallException or OperationCanceledException)
                    throw;
                throw new DataBallException("Open failed", ex);
            }
            finally
            {
                foreach (var dir in extractions)
                    DeleteDirectory(dir);
            }
        }

        private static void DeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The OS temp folder collects what a failed delete leaves.
            }
        }

        /// <summary>
        /// Reads every entry into staging, in order. Source keys count up from 1 in staging order (an archive's entries in key order),
        /// so a staging table, its record and its rows' <c>_source_key</c> agree before anything is written to <c>"data"</c>.
        /// </summary>
        private async Task<List<StagedSource>> StageSourcesAsync(
            IReadOnlyList<EntryRoute> routes,
            List<string> extractions,
            CancellationToken cancellationToken)
        {
            var staged = new List<StagedSource>();
            foreach (var route in routes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = route.Entry;
                var type = route.SourceType;
                switch (route.Route)
                {
                    case SourceRoute.Csv:
                        StageCsv(entry.OpenPath, SourceFile.Describe(entry.OpenPath, type), staged);
                        break;
                    case SourceRoute.Parquet:
                        StageParquet(entry.OpenPath, SourceFile.Describe(entry.OpenPath, type), null, staged);
                        break;
                    case SourceRoute.Hive:
                        StageParquet(entry.OpenPath, SourceFile.Describe(entry.OpenPath, type, entry.HiveFiles), entry.HiveFiles, staged);
                        break;
                    case SourceRoute.Archive:
                        StageArchive(entry.OpenPath, type, extractions, staged);
                        break;
                    case SourceRoute.Handler:
                        await StageHandlerAsync(route.Handler!, entry.OpenPath, type, staged, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new DataBallException($"No built-in import path for '{entry.Path}'");
                }
            }
            return staged;
        }

        private static string StagingTable(long key) => "_stg_" + key.ToString(CultureInfo.InvariantCulture);

        private void StageCsv(string path, SourceRecord record, List<StagedSource> staged)
        {
            var key = staged.Count + 1L;
            var table = StagingTable(key);
            // Sources after the first join rows already staged: learned types never narrow their values (the rule of an append).
            var merging = staged.Count > 0;
            _store.StageFile(table, "read_csv_auto", path, ", header=true", key);
            _store.CoerceColumnsOf(table, _expectedColumnTypes, merging ? _learnedColumnNames : null);
            CanonicalizeCsvStaging(table, merging);
            staged.Add(new StagedSource(table, record, key, _store.StagedRowCount(table), false));
        }

        private void StageParquet(string path, SourceRecord record, IReadOnlyList<string>? hiveFiles, List<StagedSource> staged)
        {
            var key = staged.Count + 1L;
            var table = StagingTable(key);
            if (hiveFiles is not null)
                _store.StageHiveDirectory(table, hiveFiles, key);
            else
                _store.StageFile(table, "read_parquet", path, "", key);
            staged.Add(new StagedSource(table, record, key, _store.StagedRowCount(table), false));
        }

        private void StageArchive(string path, string type, List<string> extractions, List<StagedSource> staged)
        {
            var archive = Path.GetFullPath(path);
            var dir = Path.Combine(Path.GetTempPath(), "databall-open-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            extractions.Add(dir);
            // Each CSV entry is its own source, recorded as <archive>!<entry>.
            foreach (var entry in ImportManager.ExtractCsvEntries(path, dir))
                StageCsv(entry.Path, SourceFile.DescribeEntry(entry.Path, archive + "!" + entry.Key, type, entry.Modified), staged);
            // The entries are in staging: their files are not needed any more.
            DeleteDirectory(dir);
            extractions.Remove(dir);
        }

        private async Task StageHandlerAsync(IFormatHandler handler, string path, string type, List<StagedSource> staged, CancellationToken cancellationToken)
        {
            var record = SourceFile.Describe(path, type);
            var key = staged.Count + 1L;
            var table = StagingTable(key);
            var batch = new List<IReadOnlyDictionary<string, object?>>(HandlerBatchSize);
            await foreach (var row in handler.Parse(path, Schema, cancellationToken).ConfigureAwait(false))
            {
                batch.Add(KeyHandlerRow(row, key));
                if (batch.Count == HandlerBatchSize)
                {
                    _store.StageRows(table, batch, _expectedColumnTypes);
                    AfterHandlerBatchForTests?.Invoke(this, batch.Count);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                _store.StageRows(table, batch, _expectedColumnTypes);
                AfterHandlerBatchForTests?.Invoke(this, batch.Count);
            }
            if (!_store.TableExists(table))
                _store.StageNoRows(table);
            staged.Add(new StagedSource(table, record, key, _store.StagedRowCount(table), true));
        }

        /// <summary>
        /// The one merge of a many-file open, inside one transaction: per-source metadata is reconciled across the staged sources,
        /// <c>"data"</c> is built from what is left, and the <c>source</c> table is created with its per-source columns. The caller's
        /// <see cref="RunImport"/> splits once afterwards.
        /// </summary>
        internal void MergeStagedSources(IReadOnlyList<StagedSource> staged)
        {
            var sourceColumns = ReconcileSourceMetadata(staged);
            _store.BuildDataFromStaged(staged, DeclaredMergeTypes());
            _store.CreateSourceTable(staged, sourceColumns);
        }

        /// <summary>
        /// Gives each configured metadata field the lowest level its values need, across all staged sources:
        /// constant in every source and present in all of them (one value) goes to <c>Metadata</c>; constant within each source that has it
        /// but absent from some or different between them becomes a column of the <c>source</c> table; varying within any source stays a
        /// row column for every row, NULL where a source lacks it. Under <c>first</c> the first value found goes to <c>Metadata</c> and the
        /// column leaves every source; under <c>requireConstant</c> a field that varies within a source throws; under <c>bounce</c> a source with
        /// empty cells is not constant. A declared table key, a field a handler wrote, and a name the <c>source</c> table already uses stay row columns.
        /// </summary>
        private IReadOnlyList<SourceMetadataColumn> ReconcileSourceMetadata(IReadOnlyList<StagedSource> staged)
        {
            var result = new List<SourceMetadataColumn>();
            if (_config.MetadataFields.Count == 0)
                return result;
            var policy = _config.MetadataPolicy ?? "liftConstant";
            var first = policy.Equals("first", StringComparison.OrdinalIgnoreCase);
            var bounce = policy.Equals("bounce", StringComparison.OrdinalIgnoreCase);
            var require = policy.Equals("requireConstant", StringComparison.OrdinalIgnoreCase);

            var fields = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in _config.MetadataFields)
            {
                if (!seen.Add(field)
                    || field.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase)
                    || IsLayoutKeyColumn(field)
                    || DuckDbStore.IsBuiltInSourceColumn(field))
                    continue;
                fields.Add(field);
            }

            // Per source: the field columns it holds (by their spelling there), profiled in one pass.
            var held = new List<Dictionary<string, string>>(staged.Count);
            var profiles = new List<IReadOnlyDictionary<string, ColumnProfile>>(staged.Count);
            var types = new List<Dictionary<string, string>>(staged.Count);
            foreach (var s in staged)
            {
                var columns = _store.GetColumnsOf(s.Table);
                var mine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var mineTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in fields)
                {
                    var match = columns.FirstOrDefault(c => c.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                    if (match.Name is null)
                        continue;
                    mine[field] = match.Name;
                    mineTypes[field] = match.DuckDbType;
                }
                held.Add(mine);
                types.Add(mineTypes);
                profiles.Add(s.FromHandler ? new Dictionary<string, ColumnProfile>() : _store.ProfileColumns(s.Table, mine.Values.ToList()));
            }

            foreach (var field in fields)
            {
                var holders = Enumerable.Range(0, staged.Count).Where(i => held[i].ContainsKey(field)).ToList();
                if (holders.Count == 0)
                    continue;
                // Handler rows are written as they are: a field a handler brought stays a row column.
                if (holders.Any(i => staged[i].FromHandler))
                    continue;
                var name = held[holders[0]][field];

                if (first)
                {
                    foreach (var i in holders)
                    {
                        var value = _store.FirstNonNull(staged[i].Table, held[i][field]);
                        if (value is null)
                            continue;
                        _store.SetMetadata(name, value);
                        break;
                    }
                    DropField(staged, held, holders, field, name);
                    continue;
                }

                var constants = new List<(int Index, object? Value)>();
                var varying = false;
                foreach (var i in holders)
                {
                    var p = profiles[i][held[i][field]];
                    if (p.Distinct > 1)
                    {
                        if (require)
                            throw new DataBallException($"Metadata field '{name}' is not constant");
                        varying = true;
                        break;
                    }
                    if (p.Distinct == 0)
                        continue; // all empty: no value
                    if (bounce && p.NonNull < staged[i].Rows)
                        varying = true; // bounce lifts only a column without empty cells
                    else
                        constants.Add((i, p.Value));
                }
                if (varying || constants.Count == 0)
                    continue;

                if (constants.Count == staged.Count && constants.All(c => SameValue(c.Value, constants[0].Value)))
                {
                    _store.SetMetadata(name, constants[0].Value);
                }
                else
                {
                    var values = new object?[staged.Count];
                    string? type = null;
                    var typeFrom = constants[0].Index;
                    foreach (var (index, value) in constants)
                    {
                        values[index] = value;
                        var t = types[index][field];
                        if (type is null)
                            type = t;
                        else if (DuckDbStore.MergedColumnType(type, t) is { } merged)
                            type = merged;
                        else
                        {
                            // A data column with types that cannot merge fails the open; so does a per-source column.
                            throw new DataBallException(
                                $"Cannot merge '{staged[index].Record.Path}': per-source column '{name}' has type {type} in '{staged[typeFrom].Record.Path}' and {t} in this one");
                        }
                    }
                    result.Add(new SourceMetadataColumn(name, type!, values));
                }
                DropField(staged, held, holders, field, name);
            }
            return result;
        }

        private void DropField(IReadOnlyList<StagedSource> staged, List<Dictionary<string, string>> held, List<int> holders, string field, string name)
        {
            foreach (var i in holders)
                _store.DropStagedColumn(staged[i].Table, held[i][field]);
            RemoveExpectedType(name);
        }

        /// <summary>Equality of two values read from staging, numerically for numbers of different widths.</summary>
        private static bool SameValue(object? a, object? b)
        {
            if (a is null || b is null)
                return a is null && b is null;
            static bool Integral(object v) => v is sbyte or byte or short or ushort or int or uint or long;
            static bool Number(object v) => Integral(v) || v is float or double or decimal;
            if (Number(a) && Number(b))
            {
                return Integral(a) && Integral(b)
                    ? Convert.ToInt64(a, CultureInfo.InvariantCulture) == Convert.ToInt64(b, CultureInfo.InvariantCulture)
                    : Convert.ToDouble(a, CultureInfo.InvariantCulture) == Convert.ToDouble(b, CultureInfo.InvariantCulture);
            }
            return a.Equals(b);
        }
    }
}
