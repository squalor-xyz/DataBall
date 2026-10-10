// SPDX-License-Identifier: Apache-2.0
using Xunit;

namespace squalor.DataBall.Tests;

public sealed class EngineOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "engine-tests", Guid.NewGuid().ToString("N"));
    private string Csv => Path.Combine(_dir, "input.csv");
    private string Temp => Path.Combine(_dir, "temp");

    public EngineOptionsTests()
    {
        Directory.CreateDirectory(Temp);
        File.WriteAllText(Csv, "Name,Age\nAlice,30\nBob,25\n");
    }

    private DataBall Open(EngineOptions options, string? path = null, string? schema = null) =>
        DataBall.Open(path ?? Csv, schema, engine: options);

    private static string? StorePath(DataBall db) => db.StorePath;

    private static object? Setting(DataBall db, string key) => db.Query($"SELECT current_setting('{key}') AS value")[0]["value"];

    [Fact]
    public void Open_SmallCsv_DefaultIsInMemory()
    {
        using var db = DataBall.Open(Csv);
        Assert.Null(StorePath(db));
    }

    [Fact]
    public void Open_AboveThreshold_UsesTempFileStore()
    {
        string path;
        using (var db = Open(new EngineOptions { InMemoryMaxBytes = 1L, TempDirectory = Temp }))
        {
            path = Assert.IsType<string>(StorePath(db));
            Assert.Equal(Temp, Path.GetDirectoryName(path));
            Assert.True(File.Exists(path));
        }
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".wal"));
    }

    [Fact]
    public void Open_StoreModeOverridesThreshold()
    {
        using var memory = Open(new EngineOptions { Store = StoreMode.Memory, InMemoryMaxBytes = 1L });
        Assert.Null(StorePath(memory));
        using var file = Open(new EngineOptions { Store = StoreMode.File, InMemoryMaxBytes = long.MaxValue, TempDirectory = Temp });
        Assert.NotNull(StorePath(file));
    }

    [Fact]
    public void EngineOptions_AppliedToConnection()
    {
        var options = new EngineOptions { Store = StoreMode.File, MemoryLimit = "256MiB", Threads = 2, TempDirectory = Temp };
        using var db = Open(options);
        Assert.Equal("256.0 MiB", Setting(db, "memory_limit"));
        Assert.Equal(2L, Setting(db, "threads"));
        Assert.Equal(Temp, Setting(db, "temp_directory"));
        using var constructed = new DataBall(engine: options);
        Assert.Equal(Temp, Path.GetDirectoryName(constructed.StorePath));
        Assert.Equal("256.0 MiB", Setting(constructed, "memory_limit"));
        Assert.Equal(2L, Setting(constructed, "threads"));
        Assert.Equal(Temp, Setting(constructed, "temp_directory"));
    }

    [Fact]
    public async Task EngineOptions_NotPersisted()
    {
        var ball = Path.Combine(_dir, "saved.ball");
        using (var db = Open(new EngineOptions { MemoryLimit = "256MiB", Threads = 2 }))
            await db.SaveAsync(ball);
        using var reopened = Open(new EngineOptions { MemoryLimit = "128MiB", Threads = 3 }, ball);
        var config = (string)reopened.Query("SELECT config FROM _databall ORDER BY version DESC LIMIT 1")[0]["config"]!;
        foreach (var key in new[] { "memory_limit", "MemoryLimit", "threads", "Threads", "TempDirectory", "InMemoryMaxBytes", "Store" })
            Assert.DoesNotContain(key, config);
        Assert.Equal("128.0 MiB", Setting(reopened, "memory_limit"));
        Assert.Equal(3L, Setting(reopened, "threads"));
    }

    [Fact]
    public async Task NativeBall_Open_AppliesEngineOptions()
    {
        var ball = Path.Combine(_dir, "native.ball");
        using (var db = DataBall.Open(Csv)) await db.SaveAsync(ball);
        using var reopened = Open(new EngineOptions { Store = StoreMode.Memory, InMemoryMaxBytes = 1L, MemoryLimit = "128MiB", Threads = 2, TempDirectory = Temp }, ball);
        Assert.Equal(Path.GetFullPath(ball), StorePath(reopened));
        Assert.Equal("128.0 MiB", Setting(reopened, "memory_limit"));
        Assert.Equal(2L, Setting(reopened, "threads"));
        Assert.Equal(Temp, Setting(reopened, "temp_directory"));
    }

    [Fact]
    public void TempStore_ConstructorFailure_LeavesNoFile()
    {
        var schema = Path.Combine(_dir, "bad.json");
        File.WriteAllText(schema, "invalid json");
        var options = new EngineOptions { Store = StoreMode.File, TempDirectory = Temp };
        Assert.Throws<DataBallException>(() => Open(options, schema: schema));
        Assert.Empty(Directory.GetFileSystemEntries(Temp));
    }

    [Fact]
    public async Task TempStore_SaveAsync_RoundTrips()
    {
        var ball = Path.Combine(_dir, "roundtrip.ball");
        using var db = Open(new EngineOptions { Store = StoreMode.File, TempDirectory = Temp });
        Assert.NotNull(StorePath(db));
        db.SetMetadata("Operator", "Ada");
        await db.SaveAsync(ball);
        using var reopened = DataBall.Open(ball);
        Assert.Equal(db.Query("SELECT * FROM data ORDER BY Name"), reopened.Query("SELECT * FROM data ORDER BY Name"));
        Assert.Equal(db.Metadata, reopened.Metadata);
    }

    [Fact]
    public void TempStore_InvalidEngineSetting_LeavesNoFile()
    {
        Assert.Throws<DataBallException>(() => Open(new EngineOptions
        {
            Store = StoreMode.File,
            MemoryLimit = "invalid",
            TempDirectory = Temp
        }));
        Assert.Empty(Directory.GetFileSystemEntries(Temp));
    }

    [Fact]
    public void TempStore_InvalidDirectory_WrapsException()
    {
        var ex = Assert.Throws<DataBallException>(() => Open(new EngineOptions
        {
            Store = StoreMode.File,
            TempDirectory = Csv
        }));
        Assert.Equal("Failed to create temporary store", ex.Message);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
    }

    [Theory]
    [InlineData(-1L, null)]
    [InlineData(null, 0)]
    [InlineData(null, -1)]
    public void EngineOptions_InvalidValues_Throw(long? maxBytes, int? threads)
    {
        var ex = Assert.Throws<DataBallException>(() => new DataBall(engine: new EngineOptions
        {
            InMemoryMaxBytes = maxBytes,
            Threads = threads
        }));
        Assert.Equal(maxBytes.HasValue ? "InMemoryMaxBytes must be non-negative" : "Threads must be positive", ex.Message);
    }

    [Theory]
    [InlineData(8L, StoreMode.File)]
    [InlineData(9L, StoreMode.Memory)]
    public void Resolver_RecursiveDirectory_SumsInputs(long threshold, StoreMode expected)
    {
        var hive = Path.Combine(_dir, "hive");
        Directory.CreateDirectory(Path.Combine(hive, "nested"));
        File.WriteAllBytes(Path.Combine(hive, "a"), new byte[4]);
        File.WriteAllBytes(Path.Combine(hive, "nested", "b"), new byte[5]);
        var options = new EngineOptions { InMemoryMaxBytes = threshold };
        Assert.Equal(expected, options.ResolveStore(new[] { hive }));
    }

    [Fact]
    public void Open_AtThreshold_UsesMemory()
    {
        using var db = Open(new EngineOptions { InMemoryMaxBytes = new FileInfo(Csv).Length });
        Assert.Null(StorePath(db));
    }

    [Fact]
    public void TempStore_Dispose_CleansSpillDirectory()
    {
        var db = Open(new EngineOptions { Store = StoreMode.File, TempDirectory = Temp });
        var spill = db.StorePath + ".tmp";
        Directory.CreateDirectory(spill);
        File.WriteAllText(Path.Combine(spill, "spill"), "test");
        db.Dispose();
        Assert.Empty(Directory.GetFileSystemEntries(Temp));
    }

    [Fact]
    public void TempStore_Dispose_CleanupFailure_DoesNotThrow()
    {
        var db = Open(new EngineOptions { Store = StoreMode.File, TempDirectory = Temp });
        // A directory at the WAL path makes File.Delete fail without relying on permissions.
        var wal = db.StorePath + ".wal";
        db.Query("CHECKPOINT");
        Directory.CreateDirectory(wal);
        try
        {
            Assert.Null(Record.Exception(db.Dispose));
            db.Dispose();
        }
        finally { Directory.Delete(wal); }
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
