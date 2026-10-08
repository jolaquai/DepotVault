using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepotVault.Core.Persistence;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(Library.AppRecord))]
[JsonSerializable(typeof(Download.QueueDocument))]
[JsonSerializable(typeof(Library.VersionState))]
[JsonSerializable(typeof(Library.LibraryDocument))]
public sealed partial class JsonContext : JsonSerializerContext;
