using System.Security.Cryptography;
using System.Text.Json.Serialization;
using DepotVault.Core.Persistence;

namespace DepotVault.Core.Library;

public sealed class DepotManifestRef
{
    public uint DepotId { get; set; }
    public ulong ManifestId { get; set; }
    public bool Complete { get; set; }
}

public sealed class VersionRecord
{
    public string Id { get; set; }
    public uint AppId { get; set; }
    public string Label { get; set; }
    public string Notes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ManifestDateUtc { get; set; }
    public uint BuildId { get; set; }
    public string Root { get; set; }
    public bool Adopted { get; set; }
    public List<DepotManifestRef> Manifests { get; set; } = [];

    [JsonIgnore]
    public bool IsComplete => Manifests.Count > 0 && Manifests.TrueForAll(m => m.Complete);

    public static string NewId()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        Span<byte> rnd = stackalloc byte[8];
        RandomNumberGenerator.Fill(rnd);
        Span<char> id = stackalloc char[8];
        for (var i = 0; i < id.Length; i++)
            id[i] = alphabet[rnd[i] & 31];
        return new string(id);
    }
}

public sealed class LibraryApp
{
    public uint AppId { get; set; }
    public string Name { get; set; }
    public string ActiveVersionId { get; set; }
    public string AdoptedVersionId { get; set; }
}

public sealed class LibraryDocument : ISchemaVersioned
{
    public int SchemaVersion { get; set; }
    public List<LibraryApp> Apps { get; set; } = [];
    public List<VersionRecord> Versions { get; set; } = [];
}
