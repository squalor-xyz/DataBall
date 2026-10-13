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
        /// A conflict between two sources, found while staging the later one: the open aborts under either policy, so it is never a skipped
        /// source. It is a <see cref="DataBallException"/>, so callers see the same type as any other open failure.
        /// </summary>
        private sealed class SourceConflictException : DataBallException
        {
            public SourceConflictException(string message)
                : base(message)
            {
            }
        }

        /// <summary>
        /// Opens its sources into one session: every source, a lone one included, is staged in its own table (TEMP in a memory store, a regular table in
        /// the temporary file store, which spills to disk), then one merge builds <c>"data"</c> and the <c>source</c> table, and one split follows.
        /// A failure disposes the session; nothing is left behind. When a source that was skipped had brought a handler overlay that changed
        /// the config, the config is resolved again from the sources that remain and they are staged again, so the session is the one
        /// the remaining sources open on their own.
        /// </summary>
        private static async Task<DataBall> OpenManyAsync(OpenPlan plan, OpenOptions options, ResolvedOpen? resolved, CancellationToken cancellationToken)
        {
            foreach (var entry in plan.Entries)
            {
                if (entry.Kind != OpenKind.Detected)
                    throw NativeWithOtherInputs(entry);
                if (plan.Entries.Count > 1 && entry.Report!.BuiltInPath is BuiltInPath.NativeBall or BuiltInPath.BallImport)
                    throw new DataBallException($"'{entry.Path}' is a native .ball; native sources cannot be opened together with other inputs yet, open it on its own");
            }
            var (effective, store, routes) = resolved is null
                ? ResolveOpenConfig(plan.Entries, plan.Bytes, options)
                : (resolved.Config, resolved.Store, resolved.Routes);

            var db = new DataBall(effective, options.Engine, temporary: store == StoreMode.File);
            var extractions = new List<string>();
            var skipped = new List<SkippedFile>(plan.Skipped);
            // A lone file (not an archive, whose entries are sources of their own) fails under Skip as a single path always did.
            var sole = plan.Entries.Count == 1 && options.OnBadFile == BadFilePolicy.Skip && !IsArchive(plan.Entries[0]) ? plan.Entries[0] : null;
            try
            {
                var current = routes;
                var config = effective;
                List<StagedSource> staged;
                while (true)
                {
                    staged = await db.StageSourcesAsync(current, options.OnBadFile, skipped, extractions, cancellationToken).ConfigureAwait(false);
                    var gone = new HashSet<string>(skipped.Select(s => s.Path), StringComparer.Ordinal);
                    var survivors = current.Where(r => !gone.Contains(r.Entry.Path)).ToList();
                    if (survivors.Count == current.Count || survivors.Count == 0)
                        break;
                    var narrowed = ResolveConfig(survivors, options);
                    if (narrowed.ToJson() == config.ToJson())
                        break;
                    // A skipped source's handler overlay shaped the config the others were staged under: start over without it.
                    db.Dispose();
                    db = new DataBall(narrowed, options.Engine, temporary: store == StoreMode.File);
                    config = narrowed;
                    current = survivors;
                }
                if (staged.Count == 0)
                {
                    if (sole is not null)
                        throw SoleFailure(plan, sole.Path, skipped.Last(s => s.Path == sole.Path).Message, null);
                    throw new DataBallException($"No file to open: every input was skipped as bad: {OpenInputs.Every(skipped.Select(s => s.Path))}");
                }
                try
                {
                    db.RunImport(false, () => db.MergeStagedSources(staged));
                }
                catch (DataBallException ex) when (sole is not null)
                {
                    throw new DataBallException($"Cannot open '{sole.Path}': {FailureMessage(ex)}", ex);
                }
                // A lone handler file leaves its last parsed row for InitializeRow to copy, as every row written by hand does.
                if (plan.Entries.Count == 1 && staged[0].LastRow is { } last)
                    db.RememberRow(last);
                db.OpenReport = db.ReportSources(plan.Ignored, staged.ToDictionary(s => s.Key, s => s.Rows), skipped);
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
        /// A source that fails to stage is skipped under <see cref="BadFilePolicy.Skip"/> (listed in <paramref name="skipped"/>, its
        /// staging table dropped, its learned column types rolled back, and its key not used), or fails the open under <see cref="BadFilePolicy.Abort"/>.
        /// Under <c>requireConstant</c> a file source whose metadata field varies within it is checked as it is staged, so it is skipped (or aborts)
        /// before the next source learns from it: its learned types are rolled back, and the sources after it stage as if it were not there.
        /// </summary>
        private async Task<List<StagedSource>> StageSourcesAsync(
            IReadOnlyList<EntryRoute> routes,
            BadFilePolicy policy,
            List<SkippedFile> skipped,
            List<string> extractions,
            CancellationToken cancellationToken)
        {
            var excluded = new HashSet<string>(skipped.Select(s => s.Path), StringComparer.Ordinal);
            var staged = new List<StagedSource>();
            foreach (var route in routes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!excluded.Contains(route.Entry.Path))
                    await StageRouteAsync(route, policy, skipped, excluded, extractions, staged, cancellationToken).ConfigureAwait(false);
            }
            return staged;
        }

        /// <summary>Stages one route. An archive handles its own entries; any other source's failure is caught here.</summary>
        private async Task StageRouteAsync(
            EntryRoute route,
            BadFilePolicy policy,
            List<SkippedFile> skipped,
            HashSet<string> excluded,
            List<string> extractions,
            List<StagedSource> staged,
            CancellationToken cancellationToken)
        {
            var entry = route.Entry;
            var type = route.SourceType;
            if (route.Route == SourceRoute.Archive)
            {
                StageArchive(entry.OpenPath, type, policy, skipped, excluded, extractions, staged);
                return;
            }

            var learned = SnapshotLearned();
            try
            {
                // A .ball file that is not a native DuckDB file has no route: it is a bad file like any other.
                if (route.Route is null)
                    throw new DataBallException("No built-in import path for this file");
                switch (route.Route.Value)
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
                    case SourceRoute.Handler:
                        await StageHandlerAsync(route.Handler!, entry.OpenPath, type, staged, cancellationToken).ConfigureAwait(false);
                        break;
                }
                if (route.Route != SourceRoute.Handler)
                    RejectNonConstant(staged);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not SourceConflictException)
            {
                RestoreLearned(learned);
                _store.DropStaged(StagingTable(staged.Count + 1));
                if (policy == BadFilePolicy.Abort || ex is OutOfMemoryException)
                    throw new DataBallException($"Cannot open '{entry.Path}': {FailureMessage(ex)}", ex);
                skipped.Add(new SkippedFile(entry.Path, FailureMessage(ex), null, null, null));
                excluded.Add(entry.Path);
            }
        }

        /// <summary>
        /// The message of a failed source, on one line: a DataBall error with its cause names the cause too. A DuckDB message
        /// runs on with the SQL it failed on; that is left out.
        /// </summary>
        private static string FailureMessage(Exception ex)
        {
            var text = ex is DataBallException && ex.InnerException is { } inner ? $"{ex.Message}: {inner.Message}" : ex.Message;
            return text.Split('\n', 2)[0].TrimEnd('\r');
        }

        /// <summary>A copy of the learned column types: what staging a CSV source adds to the session, undone when the source is skipped.</summary>
        private (Dictionary<string, Type> Types, HashSet<string> Learned) SnapshotLearned() =>
            (new Dictionary<string, Type>(_expectedColumnTypes, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(_learnedColumnNames, StringComparer.OrdinalIgnoreCase));

        private void RestoreLearned((Dictionary<string, Type> Types, HashSet<string> Learned) state)
        {
            _expectedColumnTypes.Clear();
            foreach (var pair in state.Types)
                _expectedColumnTypes[pair.Key] = pair.Value;
            _learnedColumnNames.Clear();
            _learnedColumnNames.UnionWith(state.Learned);
        }

        /// <summary>
        /// Under <c>requireConstant</c>, a file source that has just been staged (the last of <paramref name="staged"/>) and whose metadata
        /// field varies within it is removed again and reported as a failed source, before the next source is staged. The check is the one
        /// the merge makes, applied to this source's own table; a handler source is not checked, as the merge does not check a field a handler holds.
        /// </summary>
        private void RejectNonConstant(List<StagedSource> staged)
        {
            var policy = MetadataPlanner.ParsePolicy(_config.MetadataPolicy);
            if (policy != MetadataPolicyKind.RequireConstant)
                return;
            var source = staged[^1];
            var columns = MetadataFieldsToReconcile().Select(f => HeldColumn(source.Table, f)).OfType<string>().ToList();
            if (columns.Count == 0)
                return;
            var profile = _store.ProfileColumns(source.Table, columns);
            var varying = columns.FirstOrDefault(c => MetadataPlanner.Rejects(policy, new FieldFacts(profile[c], source.Rows)));
            if (varying is null)
                return;
            staged.RemoveAt(staged.Count - 1);
            throw new DataBallException($"Metadata field '{varying}' is not constant within this source");
        }

        /// <summary>The spelling of the column of a staged table that holds <paramref name="field"/>, or null.</summary>
        private string? HeldColumn(string table, string field) =>
            _store.GetColumnsOf(table).Select(c => c.Name).FirstOrDefault(n => n.Equals(field, StringComparison.OrdinalIgnoreCase));

        private static string StagingTable(long key) => "_stg_" + key.ToString(CultureInfo.InvariantCulture);

        private void StageCsv(string path, SourceRecord record, List<StagedSource> staged)
        {
            var key = staged.Count + 1L;
            var table = StagingTable(key);
            // Sources after the first join rows already staged: learned types never narrow their values (the rule of an append).
            var merging = staged.Count > 0;
            _store.StageFile(table, "read_csv_auto", path, ", header=true", key);
            // What earlier sources taught the session, and the columns this file's own header types. A cast failure against the former is a
            // conflict between files; against the latter it is this file's own bad value.
            (Dictionary<string, Type> Types, HashSet<string> Learned)? earlier = merging ? SnapshotLearned() : null;
            var ownTyped = merging ? OwnHeaderTypedColumns(table) : null;
            try
            {
                _store.CoerceColumnsOf(table, _expectedColumnTypes, merging ? _learnedColumnNames : null);
                CanonicalizeCsvStaging(table, merging);
            }
            catch (Exception ex) when (earlier is not null && ex is not OperationCanceledException)
            {
                ThrowIfLearnedConflict(table, record, earlier!.Value, ownTyped!, staged);
                throw;
            }
            staged.Add(new StagedSource(table, record, key, _store.StagedRowCount(table), false));
        }

        /// <summary>
        /// Throws when a column of <paramref name="table"/> that an earlier source taught the session has a type that cannot merge with
        /// its type here. Called only after a failed cast, so the conflict is the cause of the failure: the merge would have refused it too.
        /// </summary>
        private void ThrowIfLearnedConflict(
            string table,
            SourceRecord record,
            (Dictionary<string, Type> Types, HashSet<string> Learned) earlier,
            HashSet<string> ownTyped,
            IReadOnlyList<StagedSource> staged)
        {
            foreach (var column in _store.GetColumnsOf(table))
            {
                if (ownTyped.Contains(column.Name) || !earlier.Learned.Contains(column.Name)
                    || !earlier.Types.TryGetValue(column.Name, out var learnedClr))
                    continue;
                var learned = DuckDbStore.ToDuckDbType(learnedClr);
                var decision = DuckDbStore.DecideColumnMerge(learned, column.DuckDbType,
                    () => _store.StagedColumnIsAllNull(table, column.Name), () => false);
                if (decision.Kind != DuckDbStore.ColumnMerge.Conflict)
                    continue;
                var first = staged.FirstOrDefault(s => _store.GetColumnsOf(s.Table).Any(c => c.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase)));
                var other = first?.Record.Path ?? "an earlier source";
                throw new SourceConflictException(
                    $"Cannot merge '{record.Path}' and '{other}': column '{column.Name}' has type {column.DuckDbType} in this source and {learned} in '{other}'");
            }
        }

        /// <summary>The columns of a freshly staged CSV table whose header gives them a type (the names the header canonicalizes to).</summary>
        private HashSet<string> OwnHeaderTypedColumns(string table)
        {
            var headers = _store.GetColumnsOf(table).Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals(Lineage.KeyColumn, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return SchemaResolver.ResolveAll(headers, _config)
                .Where(c => c.ClrType is not null)
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        /// <summary>
        /// Stages the CSV entries of an archive, each its own source recorded as <c>&lt;archive&gt;!&lt;entry&gt;</c>. An archive that cannot be read
        /// is one failed source; an entry that fails to stage is a failed source of its own. Under <see cref="BadFilePolicy.Skip"/> the failure
        /// is listed in <paramref name="skipped"/> and the rest of the archive still lands.
        /// </summary>
        private void StageArchive(
            string path,
            string type,
            BadFilePolicy policy,
            List<SkippedFile> skipped,
            HashSet<string> excluded,
            List<string> extractions,
            List<StagedSource> staged)
        {
            var archive = Path.GetFullPath(path);
            var dir = Path.Combine(Path.GetTempPath(), "databall-open-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            extractions.Add(dir);
            IReadOnlyList<ImportManager.ExtractedEntry> entries;
            try
            {
                entries = ImportManager.ExtractCsvEntries(path, dir);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DeleteDirectory(dir);
                extractions.Remove(dir);
                if (policy == BadFilePolicy.Abort)
                    throw new DataBallException($"Cannot open '{archive}': {FailureMessage(ex)}", ex);
                skipped.Add(new SkippedFile(archive, FailureMessage(ex), null, null, null));
                excluded.Add(archive);
                return;
            }

            foreach (var entry in entries)
            {
                var recorded = archive + "!" + entry.Key;
                if (excluded.Contains(recorded))
                    continue;
                var learned = SnapshotLearned();
                try
                {
                    StageCsv(entry.Path, SourceFile.DescribeEntry(entry.Path, recorded, type, entry.Modified), staged);
                    RejectNonConstant(staged);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not SourceConflictException)
                {
                    RestoreLearned(learned);
                    _store.DropStaged(StagingTable(staged.Count + 1));
                    if (policy == BadFilePolicy.Abort || ex is OutOfMemoryException)
                        throw new DataBallException($"Cannot open '{recorded}': {FailureMessage(ex)}", ex);
                    skipped.Add(new SkippedFile(recorded, FailureMessage(ex), null, null, null));
                    excluded.Add(recorded);
                }
            }
            // The entries are in staging (or skipped): their files are not needed any more.
            DeleteDirectory(dir);
            extractions.Remove(dir);
        }

        private async Task StageHandlerAsync(IFormatHandler handler, string path, string type, List<StagedSource> staged, CancellationToken cancellationToken)
        {
            var record = SourceFile.Describe(path, type);
            var key = staged.Count + 1L;
            var table = StagingTable(key);
            var batch = new List<IReadOnlyDictionary<string, object?>>(HandlerBatchSize);
            IReadOnlyDictionary<string, object?>? last = null;
            await foreach (var row in handler.Parse(path, Schema, cancellationToken).ConfigureAwait(false))
            {
                last = row;
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
            staged.Add(new StagedSource(table, record, key, _store.StagedRowCount(table), true) { LastRow = last });
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
        /// Gives each configured metadata field the lowest level its values need, across all staged sources, as <see cref="MetadataPlanner"/>
        /// decides: constant in every source and present in all of them (one value) goes to <c>Metadata</c>; constant within each source that has it
        /// but absent from some or different between them becomes a column of the <c>source</c> table; varying within any source stays a
        /// row column for every row, NULL where a source lacks it. Under <c>first</c> the first value found goes to <c>Metadata</c> and the
        /// column leaves every source; under <c>requireConstant</c> a field that varies within a source throws; under <c>bounce</c> a source with
        /// empty cells is not constant. A declared table key and a field a handler wrote stay row columns. A name the <c>source</c> table's built-in
        /// columns use (<c>path</c>, <c>type</c>, …) may still reach Metadata; only the source-column level is refused for it, and it falls back to a row column.
        /// </summary>
        private IReadOnlyList<SourceMetadataColumn> ReconcileSourceMetadata(IReadOnlyList<StagedSource> staged)
        {
            var result = new List<SourceMetadataColumn>();
            if (_config.MetadataFields.Count == 0)
                return result;
            var policy = MetadataPlanner.ParsePolicy(_config.MetadataPolicy);

            var fields = MetadataFieldsToReconcile();

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
                var name = held[holders[0]][field];
                var facts = holders
                    .Select(i => new OpenHolder(i, staged[i].FromHandler,
                        staged[i].FromHandler ? default : new FieldFacts(profiles[i][held[i][field]], staged[i].Rows)))
                    .ToList();
                var decision = MetadataPlanner.PlanOpen(policy, ClassifyField(field), name, facts, staged.Count);

                switch (decision.Level)
                {
                    case MetadataLevel.Metadata:
                        if (decision.FirstNonNull)
                        {
                            foreach (var i in holders)
                            {
                                var value = _store.FirstNonNull(staged[i].Table, held[i][field]);
                                if (value is null)
                                    continue;
                                _store.SetMetadata(name, value);
                                break;
                            }
                        }
                        else
                        {
                            _store.SetMetadata(name, decision.Value);
                        }
                        DropField(staged, held, holders, field, name);
                        break;
                    case MetadataLevel.SourceColumn:
                        result.Add(SourceColumnOf(staged, types, name, field, decision.Values!));
                        DropField(staged, held, holders, field, name);
                        break;
                }
            }
            return result;
        }

        /// <summary>The <c>source</c> table column for a per-source field: the merged type of its sources' columns and a value per source.</summary>
        private static SourceMetadataColumn SourceColumnOf(
            IReadOnlyList<StagedSource> staged,
            List<Dictionary<string, string>> types,
            string name,
            string field,
            IReadOnlyList<(int Index, object? Value)> constants)
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
            return new SourceMetadataColumn(name, type!, values);
        }

        /// <summary>
        /// The configured metadata fields the reconciliation handles: distinct, and neither lineage nor a declared layout key. A field named like a
        /// built-in column of the <c>source</c> table is among them: it may reach Metadata, but never becomes a column of <c>source</c>.
        /// </summary>
        private List<string> MetadataFieldsToReconcile()
        {
            var fields = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in _config.MetadataFields)
            {
                if (seen.Add(field) && ClassifyField(field) is FieldHome.Fresh or FieldHome.BuiltInSourceName)
                    fields.Add(field);
            }
            return fields;
        }

        private void DropField(IReadOnlyList<StagedSource> staged, List<Dictionary<string, string>> held, List<int> holders, string field, string name)
        {
            foreach (var i in holders)
                _store.DropStagedColumn(staged[i].Table, held[i][field]);
            RemoveExpectedType(name);
        }
    }
}
