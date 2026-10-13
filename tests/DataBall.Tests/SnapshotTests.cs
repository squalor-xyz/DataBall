// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace squalor.DataBall.Tests;

public sealed class SnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "snapshot-test-" + Guid.NewGuid().ToString("N"));

    private string Dir(string name = "snaps") => Path.Combine(_root, name);

    private static Dictionary<string, object?> Row(long id) => new() { ["sample"] = id, ["value"] = id * 0.5 };

    private static long Count(DataBall db) => Convert.ToInt64(db.Query("SELECT count(*) AS n FROM data")[0]["n"]);

    private static long[] Samples(DataBall db) => db.Query("SELECT sample FROM data ORDER BY sample").Select(r => Convert.ToInt64(r["sample"])).ToArray();

    private static string[] Files(string directory) =>
        Directory.GetFiles(directory).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static DataBall Writer(params long[] ids)
    {
        var db = new DataBall();
        db.AddRows(ids.Select(Row).ToArray());
        return db;
    }

    private static SnapshotOptions Options(string directory, string name = "run", int keep = 2, bool embed = false) =>
        new() { Directory = directory, Name = name, Keep = keep, EmbedViews = embed };

    public void Dispose()
    {
        DuckDbStore.AfterCopyForTests = null;
        SnapshotFiles.BeforeDeleteForTests = null;
        SnapshotFiles.GenerationMissingForTests = null;
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Blocks a copy that targets <c>target</c> after its data is copied, so a test can act meanwhile.</summary>
    private sealed class CopyPause : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();

        public CopyPause(string target)
        {
            DuckDbStore.AfterCopyForTests = copying =>
            {
                if (!copying.Equals(target, StringComparison.Ordinal))
                    return;
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
            };
        }

        public void WaitUntilPaused() => Assert.True(_entered.Wait(TimeSpan.FromSeconds(10)), "the copy never started");

        public void Release() => _release.Set();

        public void Dispose()
        {
            Release();
            DuckDbStore.AfterCopyForTests = null;
        }
    }

    [Fact]
    public async Task Publish_FirstAndSecond_GenerationsAndPointer()
    {
        var dir = Dir();
        using var db = Writer(1, 2);

        var first = await db.PublishSnapshotAsync(Options(dir));

        Assert.Equal(1, first.Generation);
        Assert.Equal(Path.Combine(dir, "run.1.ball"), first.Path);
        Assert.Equal(Path.Combine(dir, "run.latest"), first.PointerPath);
        Assert.True(first.Bytes > 0);
        Assert.Equal(new FileInfo(first.Path).Length, first.Bytes);
        Assert.True(first.Duration > TimeSpan.Zero);
        Assert.Equal(new[] { "run.1.ball", "run.latest" }, Files(dir));
        Assert.Equal("DATABALL-SNAPSHOT 1\nrun.1.ball\n", File.ReadAllText(first.PointerPath));

        db.AddRow(Row(3));
        var second = await db.PublishSnapshotAsync(Options(dir));

        Assert.Equal(2, second.Generation);
        Assert.Equal(Path.Combine(dir, "run.2.ball"), second.Path);
        Assert.Equal(new[] { "run.1.ball", "run.2.ball", "run.latest" }, Files(dir));
        Assert.Equal("DATABALL-SNAPSHOT 1\nrun.2.ball\n", File.ReadAllText(second.PointerPath));
        using (var one = DataBall.Open(first.Path))
            Assert.Equal(new long[] { 1, 2 }, Samples(one));
        using (var two = DataBall.Open(second.Path))
            Assert.Equal(new long[] { 1, 2, 3 }, Samples(two));
    }

    [Fact]
    public async Task Publish_ReaderOnOldGeneration_StaysValid()
    {
        var dir = Dir();
        using var db = Writer(1, 2, 3);
        var first = await db.PublishSnapshotAsync(Options(dir));
        using var reader = DataBall.Open(Path.Combine(dir, "run.latest"));
        Assert.Equal(new long[] { 1, 2, 3 }, Samples(reader));

        db.AddRows(new[] { Row(4), Row(5) });
        var second = await db.PublishSnapshotAsync(Options(dir));

        Assert.Equal(2, second.Generation);
        Assert.Equal(new long[] { 1, 2, 3 }, Samples(reader));
        Assert.Equal(first.Path, reader.OpenedSnapshot!.Path);
        reader.Dispose();
        using var later = DataBall.Open(Path.Combine(dir, "run.latest"));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, Samples(later));
    }

    [Fact]
    public async Task Publish_Retention_KeepsLastN_SkipsHeldFile()
    {
        using var db = Writer(1);

        // Keep = 2: the third publish deletes generation 1.
        var plain = Dir("plain");
        for (var i = 0; i < 3; i++)
            await db.PublishSnapshotAsync(Options(plain));
        Assert.Equal(new[] { "run.2.ball", "run.3.ball", "run.latest" }, Files(plain));

        // A generation that cannot be deleted is skipped without failing, and goes at a later publish.
        var held = Dir("held");
        await db.PublishSnapshotAsync(Options(held));
        await db.PublishSnapshotAsync(Options(held));
        SnapshotFiles.BeforeDeleteForTests = path =>
        {
            if (Path.GetFileName(path) == "run.1.ball")
                throw new IOException("held by a reader");
        };
        var third = await db.PublishSnapshotAsync(Options(held));
        Assert.Equal(3, third.Generation);
        Assert.Equal(new[] { "run.1.ball", "run.2.ball", "run.3.ball", "run.latest" }, Files(held));
        SnapshotFiles.BeforeDeleteForTests = null;
        var fourth = await db.PublishSnapshotAsync(Options(held));
        Assert.Equal(4, fourth.Generation);
        Assert.Equal(new[] { "run.3.ball", "run.4.ball", "run.latest" }, Files(held));
        Assert.Equal("DATABALL-SNAPSHOT 1\nrun.4.ball\n", File.ReadAllText(fourth.PointerPath));

        // The generation the pointer names is never deleted, even when it is not among the newest N.
        var named = Dir("named");
        for (var i = 0; i < 3; i++)
            await db.PublishSnapshotAsync(Options(named, keep: 3));
        File.WriteAllText(Path.Combine(named, "run.latest"), "DATABALL-SNAPSHOT 1\nrun.1.ball\n");
        SnapshotFiles.Prune(named, "run", 1, NullLogger.Instance);
        Assert.Equal(new[] { "run.1.ball", "run.3.ball", "run.latest" }, Files(named));

        // Without a pointer nothing is deleted: not when it is missing, not when it is some other file.
        var unnamed = Dir("unnamed");
        for (var i = 0; i < 3; i++)
            await db.PublishSnapshotAsync(Options(unnamed, keep: 3));
        File.Delete(Path.Combine(unnamed, "run.latest"));
        SnapshotFiles.Prune(unnamed, "run", 1, NullLogger.Instance);
        Assert.Equal(new[] { "run.1.ball", "run.2.ball", "run.3.ball" }, Files(unnamed));
        File.WriteAllText(Path.Combine(unnamed, "run.latest"), "just some other file\n");
        SnapshotFiles.Prune(unnamed, "run", 1, NullLogger.Instance);
        Assert.Equal(new[] { "run.1.ball", "run.2.ball", "run.3.ball", "run.latest" }, Files(unnamed));
    }

    [Fact]
    public async Task Publish_ExcludesPendingRow()
    {
        var dir = Dir();
        using var db = Writer(1, 2);
        db.InitializeRow(Row(3));

        await db.PublishSnapshotAsync(Options(dir));

        using var snapshot = DataBall.Open(Path.Combine(dir, "run.latest"));
        Assert.Equal(new long[] { 1, 2 }, Samples(snapshot));
        db.CommitRow();
        Assert.Equal(new long[] { 1, 2, 3 }, Samples(db));
    }

    [Fact]
    public async Task OpenPointer_OpensLatestReadOnly_ExposesGeneration()
    {
        var dir = Dir();
        var pointer = Path.Combine(dir, "run.latest");
        using var db = Writer(1, 2);
        await db.PublishSnapshotAsync(Options(dir));
        db.AddRow(Row(3));
        var second = await db.PublishSnapshotAsync(Options(dir));

        using (var reader = DataBall.Open(pointer))
        {
            Assert.Equal(new long[] { 1, 2, 3 }, Samples(reader));
            var opened = reader.OpenedSnapshot!;
            Assert.Equal(2, opened.Generation);
            Assert.Equal(Path.Combine(dir, "run.2.ball"), opened.Path);
            Assert.Equal(pointer, opened.PointerPath);
            Assert.Equal(second.Bytes, opened.Bytes);
            var error = Assert.Throws<DataBallException>(() => reader.AddRow(Row(9)));
            Assert.Equal("Session is read-only; open with writable: true to change it", error.Message);
        }

        var writable = Assert.Throws<DataBallException>(() => DataBall.Open(pointer, writable: true));
        Assert.Equal($"A snapshot is read-only; {pointer} cannot be opened with writable: true", writable.Message);

        db.AddRow(Row(4));
        await db.PublishSnapshotAsync(Options(dir));
        using (var newer = DataBall.Open(pointer))
        {
            Assert.Equal(3, newer.OpenedSnapshot!.Generation);
            Assert.Equal(new long[] { 1, 2, 3, 4 }, Samples(newer));
        }

        using (var plain = DataBall.Open(Path.Combine(dir, "run.3.ball")))
            Assert.Null(plain.OpenedSnapshot);

        // A pointer written with CRLF line endings is read the same way.
        File.WriteAllText(pointer, "DATABALL-SNAPSHOT 1\r\nrun.2.ball\r\n");
        using (var crlf = DataBall.Open(pointer))
            Assert.Equal(2, crlf.OpenedSnapshot!.Generation);

        // A read-only session may publish: the snapshot is a copy.
        using var viewer = DataBall.Open(Path.Combine(dir, "run.3.ball"));
        var copied = await viewer.PublishSnapshotAsync(Options(Dir("copy")));
        Assert.Equal(1, copied.Generation);
        using var copy = DataBall.Open(copied.PointerPath);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, Samples(copy));
    }

    [Fact]
    public async Task OpenPointer_Malformed_Throws()
    {
        var dir = Dir();
        var pointer = Path.Combine(dir, "run.latest");
        using var db = Writer(1);
        await db.PublishSnapshotAsync(Options(dir));
        File.WriteAllText(Path.Combine(dir, "run.9.ball"), "not a database file at all");
        var cases = new (string Text, string Message)[]
        {
            ("DATABALL-SNAPSHOT 1\n", " is not a valid snapshot pointer: line 2 must name a generation file in the same directory"),
            ("DATABALL-SNAPSHOT 1\n../run.1.ball\n", " is not a valid snapshot pointer: line 2 must name a generation file in the same directory"),
            ("DATABALL-SNAPSHOT 1\nsub/run.1.ball\n", " is not a valid snapshot pointer: line 2 must name a generation file in the same directory"),
            ("DATABALL-SNAPSHOT 1\nrun.ball\n", " is not a valid snapshot pointer: 'run.ball' is not a generation file name"),
        };
        foreach (var (text, message) in cases)
        {
            File.WriteAllText(pointer, text);
            var error = Assert.Throws<DataBallException>(() => DataBall.Open(pointer));
            Assert.Equal(pointer + message, error.Message);
        }

        // A generation that is not a native .ball is refused, and a file with another first line is not a pointer.
        File.WriteAllText(pointer, "DATABALL-SNAPSHOT 1\nrun.9.ball\n");
        var notNative = Assert.Throws<DataBallException>(() => DataBall.Open(pointer));
        Assert.Equal($"Snapshot generation {Path.Combine(dir, "run.9.ball")} is not a native .ball", notNative.Message);
        File.WriteAllText(pointer, "DATABALL-SNAPSHOT 2\nrun.1.ball\n");
        var other = Assert.Throws<DataBallException>(() => DataBall.Open(pointer));
        Assert.DoesNotContain("snapshot pointer", other.Message);
    }

    [Fact]
    public async Task OpenPointer_GenerationVanished_RetriesThenClearError()
    {
        var dir = Dir();
        var pointer = Path.Combine(dir, "run.latest");
        using var db = Writer(1, 2);
        await db.PublishSnapshotAsync(Options(dir, keep: 5));
        db.AddRow(Row(3));
        await db.PublishSnapshotAsync(Options(dir, keep: 5));
        File.Delete(Path.Combine(dir, "run.1.ball"));

        // The pointer names a generation that retention just deleted; by the second read it names generation 2.
        File.WriteAllText(pointer, "DATABALL-SNAPSHOT 1\nrun.1.ball\n");
        SnapshotFiles.GenerationMissingForTests = p => File.WriteAllText(p, "DATABALL-SNAPSHOT 1\nrun.2.ball\n");
        using (var retried = DataBall.Open(pointer))
        {
            Assert.Equal(2, retried.OpenedSnapshot!.Generation);
            Assert.Equal(new long[] { 1, 2, 3 }, Samples(retried));
        }

        // The pointer still names the missing generation on the second read.
        File.WriteAllText(pointer, "DATABALL-SNAPSHOT 1\nrun.1.ball\n");
        SnapshotFiles.GenerationMissingForTests = null;
        var error = Assert.Throws<DataBallException>(() => DataBall.Open(pointer));
        Assert.Equal($"Snapshot pointer {pointer} names generation run.1.ball, which does not exist", error.Message);

        // The pointer itself vanishes before the second read: a clear error, not a raw FileNotFoundException.
        File.WriteAllText(pointer, "DATABALL-SNAPSHOT 1\nrun.1.ball\n");
        SnapshotFiles.GenerationMissingForTests = File.Delete;
        var gone = Assert.Throws<DataBallException>(() => DataBall.Open(pointer));
        Assert.Equal($"Snapshot pointer {pointer} cannot be read", gone.Message);
    }

    [Fact]
    public async Task ReadSnapshotGeneration_ReadsThePointerOnly()
    {
        var dir = Dir();
        var pointer = Path.Combine(dir, "run.latest");
        using var db = Writer(1);
        Assert.Null(DataBall.ReadSnapshotGeneration(pointer));

        await db.PublishSnapshotAsync(Options(dir));
        Assert.Equal(1, DataBall.ReadSnapshotGeneration(pointer));
        await db.PublishSnapshotAsync(Options(dir));
        Assert.Equal(2, DataBall.ReadSnapshotGeneration(pointer));

        // Polling does not open the generation: a reader holding it is not in the way, and the pointer alone decides.
        using var reader = DataBall.Open(pointer);
        Assert.Equal(2, reader.OpenedSnapshot!.Generation);
        File.WriteAllText(pointer, "DATABALL-SNAPSHOT 1\nrun.7.ball\n");
        Assert.Equal(7, DataBall.ReadSnapshotGeneration(pointer));

        var notPointer = Path.Combine(dir, "notes.latest");
        File.WriteAllText(notPointer, "just some notes\n");
        var error = Assert.Throws<DataBallException>(() => DataBall.ReadSnapshotGeneration(notPointer));
        Assert.Equal($"{notPointer} is not a snapshot pointer", error.Message);
        File.WriteAllText(notPointer, "DATABALL-SNAPSHOT 1\nrun.ball\n");
        var malformed = Assert.Throws<DataBallException>(() => DataBall.ReadSnapshotGeneration(notPointer));
        Assert.Equal($"{notPointer} is not a valid snapshot pointer: 'run.ball' is not a generation file name", malformed.Message);
        Assert.Throws<DataBallException>(() => DataBall.ReadSnapshotGeneration(" "));
        var unreadable = Path.Combine(dir, "dir.latest");
        Directory.CreateDirectory(unreadable);
        var cannot = Assert.Throws<DataBallException>(() => DataBall.ReadSnapshotGeneration(unreadable));
        Assert.Equal($"Snapshot pointer {unreadable} cannot be read", cannot.Message);
    }

    [Fact]
    public async Task Publish_ExistingNonPointerLatest_Refused_NothingWritten()
    {
        var dir = Dir();
        Directory.CreateDirectory(dir);
        var pointer = Path.Combine(dir, "run.latest");
        var mine = new byte[] { 0xEF, 0xBB, 0xBF, 0x6D, 0x79, 0x20, 0x6E, 0x6F, 0x74, 0x65, 0x73, 0x0D, 0x0A };
        File.WriteAllBytes(pointer, mine);
        using var db = Writer(1);

        var error = await Assert.ThrowsAsync<DataBallException>(() => db.PublishSnapshotAsync(Options(dir)));

        Assert.Equal($"{pointer} exists and is not a snapshot pointer", error.Message);
        Assert.Equal(mine, File.ReadAllBytes(pointer));
        Assert.Equal(new[] { "run.latest" }, Files(dir));
    }

    [Fact]
    public async Task Publish_ViewSession_LinksByDefault_EmbedsOnRequest()
    {
        using var f = new ParquetFixture();
        using (var writer = new DataBall())
            writer.Query($"COPY (SELECT i AS Id, md5(i::VARCHAR) AS Payload FROM range(20000) t(i)) TO {DuckDbStore.QuoteString(f.Source)} (FORMAT PARQUET)");
        var dir = f.PathFor("snaps");
        using var db = DataBall.Open(f.Source);

        var linked = await db.PublishSnapshotAsync(Options(dir, "view"));
        var embedded = await db.PublishSnapshotAsync(Options(dir, "view", embed: true));

        Assert.Equal(new[] { 1, 2 }, new[] { linked.Generation, embedded.Generation });
        Assert.True(linked.Bytes < embedded.Bytes);
        ParquetViewTests.AssertView(db);
        using (var l = DataBall.Open(linked.Path))
        {
            ParquetViewTests.AssertView(l);
            Assert.Equal(20000L, l.Count(new SessionFilter()));
        }
        using (var e = DataBall.Open(embedded.Path))
        {
            ParquetViewTests.AssertTable(e);
            Assert.Equal(20000L, e.Count(new SessionFilter()));
        }
        File.Delete(f.Source);
        var missing = Assert.Throws<DataBallException>(() => DataBall.Open(linked.Path));
        // Linked paths are stored with '/' separators, so DuckDB names that form on Windows.
        Assert.Contains(f.Source.Replace('\\', '/'), missing.Message);
        using var stillThere = DataBall.Open(embedded.Path);
        Assert.Equal(20000L, stillThere.Count(new SessionFilter()));
    }

    [Fact]
    public async Task Publish_GateReleasedDuringCopy()
    {
        var dir = Dir();
        var other = Path.Combine(_root, "other.ball");
        var otherCopy = Path.Combine(_root, "other-copy.ball");
        Directory.CreateDirectory(_root);
        using (var seed = new DataBall(databasePath: other))
            seed.AddRows(new[] { Row(7), Row(8) });
        using var writer = Writer(1, 2);
        using var pause = new CopyPause(Path.Combine(dir, "run.1.ball"));

        var publishing = Task.Run(() => writer.PublishSnapshotAsync(Options(dir)));
        pause.WaitUntilPaused();

        // Open, save and dispose a different file while the publish sits inside its copy.
        var elsewhere = Task.Run(() =>
        {
            using var session = DataBall.Open(other);
            session.SaveAsync(otherCopy).GetAwaiter().GetResult();
        });
        var finished = await Task.WhenAny(elsewhere, Task.Delay(TimeSpan.FromSeconds(10))) == elsewhere;
        pause.Release();
        Assert.True(finished, "another session's open and save waited for the publish copy");
        await elsewhere;
        var info = await publishing;

        Assert.Equal(1, info.Generation);
        using var copied = DataBall.Open(otherCopy);
        Assert.Equal(new long[] { 7, 8 }, Samples(copied));
        using var snapshot = DataBall.Open(info.Path);
        Assert.Equal(new long[] { 1, 2 }, Samples(snapshot));
    }

    [Fact]
    public async Task Publish_GenerationNumbering_SurvivesRestart()
    {
        // Generations 1 and 2 exist; a new writer session publishes 3.
        var dir = Dir();
        using (var first = Writer(1))
        {
            await first.PublishSnapshotAsync(Options(dir, keep: 5));
            await first.PublishSnapshotAsync(Options(dir, keep: 5));
        }
        File.WriteAllText(Path.Combine(dir, "other.9.ball"), "not ours");
        File.WriteAllText(Path.Combine(dir, "run.x.ball"), "not ours");
        File.WriteAllText(Path.Combine(dir, "run.1.5.ball"), "not ours");
        using (var restarted = Writer(1, 2))
        {
            var info = await restarted.PublishSnapshotAsync(Options(dir, keep: 5));
            Assert.Equal(3, info.Generation);
            Assert.Equal("DATABALL-SNAPSHOT 1\nrun.3.ball\n", File.ReadAllText(info.PointerPath));
        }

        // Pruned generations leave gaps: the next number is one more than the highest, not the count.
        var gaps = Dir("gaps");
        using (var before = Writer(1))
            for (var i = 0; i < 4; i++)
                await before.PublishSnapshotAsync(Options(gaps));
        Assert.Equal(new[] { "run.3.ball", "run.4.ball", "run.latest" }, Files(gaps));
        using var after = Writer(1, 2);
        var next = await after.PublishSnapshotAsync(Options(gaps));
        Assert.Equal(5, next.Generation);
        Assert.Equal(new[] { "run.4.ball", "run.5.ball", "run.latest" }, Files(gaps));
    }

    [Fact]
    public async Task Publish_DuringOpenStream_Throws()
    {
        var dir = Dir();
        using var db = Writer(1, 2);
        await using (var rows = db.ReadRowsAsync(new SessionFilter()).GetAsyncEnumerator())
        {
            Assert.True(await rows.MoveNextAsync());
            var error = await Assert.ThrowsAsync<DataBallException>(() => db.PublishSnapshotAsync(Options(dir)));
            Assert.Equal("A row stream is open; finish or dispose it first", error.Message);
        }
        Assert.False(Directory.Exists(dir));

        var after = await db.PublishSnapshotAsync(Options(dir));
        Assert.Equal(1, after.Generation);

        var other = Dir("disposed");
        db.Dispose();
        var disposed = await Assert.ThrowsAsync<ObjectDisposedException>(() => db.PublishSnapshotAsync(Options(other)));
        Assert.Equal("squalor.DataBall.DataBall", disposed.ObjectName);
        Assert.False(Directory.Exists(other));
    }

    [Fact]
    public async Task Publish_InvalidOptions_Throw_WriteNothing()
    {
        var dir = Dir();
        using var db = Writer(1);
        var cases = new (SnapshotOptions Options, string Message)[]
        {
            (Options(dir, keep: 0), "Snapshot Keep must be at least 1"),
            (Options(dir, "a/b"), "Snapshot name must be a bare file name: 'a/b'"),
            (Options(dir, ".."), "Snapshot name must be a bare file name: '..'"),
            (Options(dir, " "), "Snapshot name must be a bare file name: ' '"),
            (Options(" "), "Snapshot directory is required"),
        };
        foreach (var (options, message) in cases)
        {
            var error = await Assert.ThrowsAsync<DataBallException>(() => db.PublishSnapshotAsync(options));
            Assert.Equal(message, error.Message);
        }
        Assert.False(Directory.Exists(dir));
        await Assert.ThrowsAsync<ArgumentNullException>(() => db.PublishSnapshotAsync(null!));
    }

    [Fact]
    public async Task Publish_PointerBlocked_LeavesNoTempFiles()
    {
        var dir = Dir();
        using var db = Writer(1);
        Directory.CreateDirectory(Path.Combine(dir, "run.latest"));

        await Assert.ThrowsAsync<DataBallException>(() => db.PublishSnapshotAsync(Options(dir)));

        // The finished generation stays; no temp file does.
        Assert.Equal(new[] { "run.1.ball" }, Files(dir));
        Directory.Delete(Path.Combine(dir, "run.latest"));
        var next = await db.PublishSnapshotAsync(Options(dir));
        Assert.Equal(2, next.Generation);
        Assert.Equal(new[] { "run.1.ball", "run.2.ball", "run.latest" }, Files(dir));
    }

    [Fact]
    public async Task Publish_TargetAppearsDuringCopy_NotOverwritten()
    {
        var dir = Dir();
        var target = Path.Combine(dir, "run.1.ball");
        using var db = Writer(1);
        DuckDbStore.AfterCopyForTests = _ => File.WriteAllText(target, "another writer's file");

        await Assert.ThrowsAsync<DataBallException>(() => db.PublishSnapshotAsync(Options(dir)));

        Assert.Equal("another writer's file", File.ReadAllText(target));
        Assert.Equal(new[] { "run.1.ball" }, Files(dir));
    }

    [Fact]
    public async Task SaveAsync_Unchanged_WithNarrowedGate()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "saved.ball");
        using var db = Writer(1, 2);

        // A save while the target is open in another session still throws, and writes nothing.
        await db.SaveAsync(target);
        using (var holder = DataBall.Open(target))
        {
            var error = await Assert.ThrowsAsync<DataBallException>(() => db.SaveAsync(target));
            Assert.Equal("Save target is already open in another session", error.Message);
        }
        Assert.Equal(new[] { "saved.ball" }, Files(_root));

        // A failed save releases its target and cleans up, so the next save to it works.
        var failing = Path.Combine(_root, "failing.ball");
        DuckDbStore.AfterCopyForTests = _ => throw new IOException("disk full");
        await Assert.ThrowsAsync<DataBallException>(() => db.SaveAsync(failing));
        DuckDbStore.AfterCopyForTests = null;
        Assert.Equal(new[] { "saved.ball" }, Files(_root));
        await db.SaveAsync(failing);
        using (var reopened = DataBall.Open(failing))
            Assert.Equal(new long[] { 1, 2 }, Samples(reopened));

        // While a save sits inside its copy, its target is still reserved; other files are not blocked.
        var slow = Path.Combine(_root, "slow.ball");
        using (var pause = new CopyPause(slow))
        {
            var saving = Task.Run(() => db.SaveAsync(slow));
            pause.WaitUntilPaused();

            var attempts = Task.Run(async () =>
            {
                using var second = Writer(5);
                var save = await Assert.ThrowsAsync<DataBallException>(() => second.SaveAsync(slow));
                using var unrelated = DataBall.Open(failing);
                // Opening a file while another session saves to it throws at once too.
                var open = Assert.Throws<DataBallException>(() => new DataBall(databasePath: slow));
                Assert.Equal($"Database file is already open: {slow}", open.Message);
                return save.Message;
            });
            var finished = await Task.WhenAny(attempts, Task.Delay(TimeSpan.FromSeconds(10))) == attempts;
            pause.Release();
            Assert.True(finished, "a save to another file waited for the slow save");
            Assert.Equal("Save target is already open in another session", await attempts);
            await saving;
        }
        using var slowCopy = DataBall.Open(slow);
        Assert.Equal(new long[] { 1, 2 }, Samples(slowCopy));
    }
}
