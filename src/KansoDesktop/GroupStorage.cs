using System.Collections.Generic;
using System.IO;
using System.Text.Json;
namespace KansoDesktop;
// Read-only compatibility aid. Beta.3 has no code path that moves files into this storage.
public sealed record StoredItem(string Path, string Original, string Group);
public static class LegacyStorageCatalog
{
    public static IReadOnlyList<StoredItem> Read(string catalog) => File.Exists(catalog)
        ? JsonSerializer.Deserialize<List<StoredItem>>(File.ReadAllText(catalog)) ?? new()
        : new List<StoredItem>();
}

