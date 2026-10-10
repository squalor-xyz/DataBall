// SPDX-License-Identifier: Apache-2.0
using Xunit;
using static squalor.DataBall.Tests.DetectionFixture;

namespace squalor.DataBall.Tests;

[Collection("DataBall.Handlers")]
public class FileTypeTests : DetectionFixture
{
    private string Register(string conditions, string config = "{}", string id = "lab", int version = 1, string? folder = null)
    {
        var path = Path.Combine(folder ?? Root, id + ".json");
        File.WriteAllText(path, """{"id":"$id","version":$version,"detect":{"conditions":[$conditions]},"config":$config}""".Replace("$id", id).Replace("$version", version.ToString()).Replace("$conditions", conditions).Replace("$config", config));
        DataBall.RegisterFileType(path);
        return path;
    }

    [Fact]
    public void JsonType_HostSchemaOverridesType()
    {
        var input = Write("input.csv", "Id,Value\n1,2\n");
        Register("{\"extension\":[\".csv\"]}", """{"columns":{"Value":"double"}}""");
        var schema = Write("schema.json", """{"columns":{"Value":"string"}}""");
        using var db = DataBall.Open(input, schema);
        Assert.Equal("VARCHAR", db.Query("DESCRIBE data").Single(r => (string)r["column_name"]! == "Value")["column_type"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonType_DelegatingUnknownExtension_None(bool open)
    {
        Register("{\"extension\":[\".txt\"]}");
        var input = Write("feed.txt", "Id\n1\n");
        if (open)
            Assert.Contains("lab delegates to generic import; no built-in path for '.txt'", Assert.Throws<DataBallException>(() => DataBall.Open(input)).Message);
        else
            Assert.Equal(DetectionOutcome.None, DataBall.Detect(input).Outcome);
    }

    [Fact]
    public void JsonType_ScratchJsonOutsideTypesFolder_IsNotScanned()
    {
        Write("schema.json", """{"columns":{"Value":"string"}}""");
        Assert.Equal(DetectionOutcome.Generic, DataBall.Detect(Write("input.csv", "Id\n1\n")).Outcome);
        Assert.Empty(DataBall.FileTypes);
    }

    [Fact]
    public void JsonType_UnknownTopLevelKey_Throws()
    {
        var path = Write("unknown.json", """{"id":"lab","version":1,"detect":{"conditions":[{"extension":[".csv"]}]},"transforms":[]}""");
        Assert.Contains($"Invalid file type '{path}': unknown top-level key 'transforms'", Assert.Throws<DataBallException>(() => DataBall.RegisterFileType(path)).Message);
    }

    [Fact]
    public void JsonType_InvalidFolderFile_ThrowsNamingPath()
    {
        var path = Path.Combine(TypesRoot, "invalid.json");
        File.WriteAllText(path, "{");
        Assert.Contains(path, Assert.Throws<DataBallException>(() => DataBall.Detect(Write("input.csv", "Id\n1\n"))).Message);
    }

    [Fact]
    public void JsonType_RegexTimeout_ThrowsNamingOrigin()
    {
        var path = Register("{\"lineMatches\":{\"line\":0,\"regex\":\"^(a+)+$\"}}");
        Assert.Contains($"Regex timed out in file type '{path}'", Assert.Throws<DataBallException>(() => DataBall.Detect(Write("input.csv", new string('a', 60000) + "!"))).Message);
    }

    [Fact]
    public void JsonType_ParquetOverlay_ReachesView()
    {
        var path = Path.Combine(Root, "input.parquet");
        using (var source = new DataBall())
            source.Query($"COPY (SELECT 1 AS Id, 'wafer' AS Tag) TO {DuckDbStore.QuoteString(path)} (FORMAT PARQUET)");
        Register("{\"parquetColumns\":[\"Id\"]}", """{"metadataFieldsAdd":["Tag"]}""");
        using var db = DataBall.Open(path);
        Assert.Equal("wafer", db.Metadata.GetValueOrDefault("Tag"));
    }

    [Theory]
    [InlineData("weight", "-1")]
    [InlineData("weight", "\"bad\"")]
    [InlineData("required", "1")]
    public void JsonType_InvalidWeightOrRequired_Throws(string key, string value)
    {
        Assert.Contains(Path.Combine(Root, "lab.json"), Assert.Throws<DataBallException>(() => Register("{\"fileName\":\"*\",\"" + key + "\":" + value + "},{\"extension\":[\".csv\"],\"weight\":2}")).Message);
    }

    public static IEnumerable<object[]> Conditions()
    {
        yield return ["fileName", "\"INV_*.csv\"", "INV_1.csv", "other.csv"];
        yield return ["fileNameRegex", "\"^INV_.*\\\\.csv$\"", "INV_1.csv", "other.csv"];
        yield return ["extension", "[\".csv\"]", "input.csv", "input.txt"];
        yield return ["headerContainsAll", "[\"Id\",\"Value\"]", "id,vAlUe\n1,2\n", "Id,Other\n1,2\n"];
        yield return ["headerExact", "[\"Id\",\"Value\"]", "Id,Value\n1,2\n", "Value,Id\n1,2\n"];
        yield return ["lineMatches", "{\"line\":1,\"regex\":\"^1,2$\"}", "Id,Value\n1,2\n", "Id,Value\n3,4\n"];
        yield return ["lineMatches", "{\"firstLines\":2,\"regex\":\"^1,2$\"}", "Id,Value\n1,2\n", "Id,Value\n3,4\n1,2\n"];
        yield return ["columnCount", "{\"min\":2,\"max\":2}", "Id,Value\n1,2\n", "Id\n1\n"];
        yield return ["parquetColumns", "[\"Id\"]", "Id", "nested"];
    }

    [Theory]
    [MemberData(nameof(Conditions))]
    public void JsonType_Conditions(string key, string value, string match, string miss)
    {
        string good, bad;
        if (key == "parquetColumns")
        {
            good = Path.Combine(Root, "good.parquet");
            bad = Path.Combine(Root, "bad.parquet");
            using var db = new DataBall();
            db.Query($"COPY (SELECT 1 AS Id) TO {DuckDbStore.QuoteString(good)} (FORMAT PARQUET)");
            db.Query($"COPY (SELECT struct_pack(Id := 1) AS nested) TO {DuckDbStore.QuoteString(bad)} (FORMAT PARQUET)");
        }
        else if (key is "fileName" or "fileNameRegex" or "extension")
        {
            good = Write(match, "Id,Value\n1,2\n");
            bad = Write(miss, "Id,Value\n1,2\n");
        }
        else
        {
            good = Write("good.csv", match);
            bad = Write("bad.csv", miss);
        }
        var condition = $"\"{key}\":{value}";
        Register("{" + condition + "}");
        Assert.Equal(1.0, TypeScore(good));
        Assert.Equal(0.0, TypeScore(bad));
        Register("{" + condition + ",\"weight\":3},{\"fileName\":\"never\",\"weight\":1}");
        Assert.Equal(0.75, TypeScore(good));
        Register("{" + condition + ",\"weight\":3},{\"fileName\":\"never\",\"weight\":1,\"required\":true}");
        Assert.Equal(0.0, TypeScore(good));
    }

    private static double TypeScore(string path)
    {
        return Detect(path).Handlers.Single(s => s.Name == "lab").Score;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonType_OverlayShapesSession(bool layout)
    {
        var path = Write("input.csv", "Id,Value\n1,2\n2,3\n");
        Register("{\"extension\":[\".csv\"]}", layout
            ? """{"columns":{"Value":"double"},"tables":{"rows":{"kind":"rows","columns":["Id","Value"]}}}"""
            : """{"columns":{"Value":"double"}}""");
        using var db = DataBall.Open(path);
        Assert.Equal("DOUBLE", db.Query("DESCRIBE data").Single(r => (string)r["column_name"]! == "Value")["column_type"]);
        Assert.Equal(new double[] { 2, 3 }, db.Query("SELECT Value FROM data ORDER BY Id").Select(r => (double)r["Value"]!));
        Assert.Equal(layout ? "VIEW" : "BASE TABLE", db.Query("SELECT table_type FROM information_schema.tables WHERE table_name='data'")[0]["table_type"]);
    }

    [Fact]
    public void JsonType_BeatsCustomCsv()
    {
        var path = Write("input.csv", "EVM(dB),I_Total(A)\n1,2\n");
        DataBall.RegisterHandler(new Handlers.CustomCsvHandler());
        Register("{\"headerContainsAll\":[\"EVM(dB)\"]}", """{"columns":{"EVM":"string"}}""");
        Assert.Equal("lab", Detect(path).Winner!.Name);
        using var db = DataBall.Open(path);
        Assert.Equal("VARCHAR", db.Query("DESCRIBE data").Single(r => (string)r["column_name"]! == "EVM")["column_type"]);
    }

    [Fact]
    public void JsonType_FolderOrder_LaterOriginOverrides_OriginReported()
    {
        var home = Path.Combine(Root, "home");
        var local = Path.Combine(Root, "local");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(home, "lab.json"), """{"id":"lab","version":1,"detect":{"conditions":[{"fileName":"never"}]}}""");
        var localPath = Path.Combine(local, "lab.json");
        File.WriteAllText(localPath, """{"id":"lab","version":1,"detect":{"conditions":[{"extension":[".csv"]}]}}""");
        SetRoots(home, local);
        var input = Write("input.csv", "Id\n1\n");
        Assert.Equal(1.0, TypeScore(input));
        var types = DataBall.FileTypes.ToArray();
        Assert.Single(types);
        Assert.Equal("lab", types[0].Id);
        Assert.Equal(1, types[0].Version);
        Assert.Equal(localPath, types[0].Origin);
        var explicitPath = Register("{\"fileName\":\"never\"}");
        Assert.Equal(0.0, TypeScore(input));
        types = DataBall.FileTypes.ToArray();
        Assert.Single(types);
        Assert.Equal(explicitPath, types[0].Origin);
        DataBall.ClearHandlers();
        Assert.Equal(1.0, TypeScore(input));
    }

    [Theory]
    [InlineData("{\"unknown\":1}")]
    [InlineData("{\"fileName\":\"*\",\"extension\":[\".csv\"]}")]
    [InlineData("{\"lineMatches\":{\"line\":1,\"firstLines\":1,\"regex\":\"x\"}}")]
    [InlineData("{\"lineMatches\":{\"regex\":\"x\"}}")]
    [InlineData("{\"columnCount\":{}}")]
    [InlineData("{\"fileNameRegex\":\"[\"}")]
    public void JsonType_Invalid_ThrowsNamingFile(string condition)
    {
        var ex = Assert.Throws<DataBallException>(() => Register(condition));
        Assert.Contains(Path.Combine(Root, "lab.json"), ex.Message);
    }
}
