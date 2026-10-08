using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DepotVault.Core.Persistence;

namespace DepotVault.Tests.Persistence;

public sealed class TestDoc : ISchemaVersioned
{
    public int SchemaVersion { get; set; }
    public string Name { get; set; }
    public int Count { get; set; }
}

[JsonSerializable(typeof(TestDoc))]
public sealed partial class TestJsonContext : JsonSerializerContext;

public class AtomicJsonStoreTests
{
    private sealed class RenameMigrator : IJsonMigrator
    {
        public int CurrentVersion => 2;
        public void Migrate(JsonObject root, int fromVersion)
        {
            if (fromVersion < 2 && root.Remove("OldName", out var v))
                root["Name"] = v;
        }
    }

    [Fact]
    public void RoundTrips()
    {
        using var dir = new TempDir();
        using var store = new AtomicJsonStore<TestDoc>(dir.Combine("a.json"), TestJsonContext.Default.TestDoc);
        store.Save(new TestDoc { Name = "x", Count = 3 });
        var loaded = store.Load();
        Assert.Equal("x", loaded.Name);
        Assert.Equal(3, loaded.Count);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.False(File.Exists(dir.Combine("a.json.tmp")));
    }

    [Fact]
    public void MissingFileYieldsDefault()
    {
        using var dir = new TempDir();
        using var store = new AtomicJsonStore<TestDoc>(dir.Combine("sub", "a.json"), TestJsonContext.Default.TestDoc);
        var loaded = store.Load();
        Assert.Null(loaded.Name);
        Assert.Equal(1, loaded.SchemaVersion);
    }

    [Fact]
    public void LeftoverTmpIsIgnored()
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.json");
        using var store = new AtomicJsonStore<TestDoc>(path, TestJsonContext.Default.TestDoc);
        store.Save(new TestDoc { Name = "good" });
        File.WriteAllText(path + ".tmp", "{ \"Name\": \"tru");
        Assert.Equal("good", store.Load().Name);
        store.Save(new TestDoc { Name = "next" });
        Assert.Equal("next", store.Load().Name);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task DebounceCoalesces()
    {
        using var dir = new TempDir();
        await using var store = new AtomicJsonStore<TestDoc>(dir.Combine("a.json"), TestJsonContext.Default.TestDoc, debounce: TimeSpan.FromMilliseconds(100));
        for (var i = 0; i < 20; i++)
            store.ScheduleSave(new TestDoc { Count = i });
        Assert.Equal(0, store.WriteCount);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal(1, store.WriteCount);
        Assert.Equal(19, store.Load().Count);
    }

    [Fact]
    public void DisposeFlushesPending()
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.json");
        using (var store = new AtomicJsonStore<TestDoc>(path, TestJsonContext.Default.TestDoc, debounce: TimeSpan.FromHours(1)))
            store.ScheduleSave(new TestDoc { Count = 7 });
        using var reader = new AtomicJsonStore<TestDoc>(path, TestJsonContext.Default.TestDoc);
        Assert.Equal(7, reader.Load().Count);
    }

    [Fact]
    public void MigratesOldSchema()
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.json");
        File.WriteAllText(path, """{ "SchemaVersion": 1, "OldName": "legacy", "Count": 2 }""");
        using var store = new AtomicJsonStore<TestDoc>(path, TestJsonContext.Default.TestDoc, new RenameMigrator());
        var doc = store.Load();
        Assert.Equal("legacy", doc.Name);
        Assert.Equal(2, doc.Count);
        Assert.Equal(2, doc.SchemaVersion);
    }
}
