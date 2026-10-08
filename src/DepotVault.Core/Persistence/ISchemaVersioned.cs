using System.Text.Json.Nodes;

namespace DepotVault.Core.Persistence;

public interface ISchemaVersioned
{
    int SchemaVersion { get; set; }
}

public interface IJsonMigrator
{
    int CurrentVersion { get; }
    void Migrate(JsonObject root, int fromVersion);
}
