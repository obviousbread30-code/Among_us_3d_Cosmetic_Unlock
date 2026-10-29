using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LocalCosmeticPreview;

// Managed-only data. Never serialize Unity/IL2CPP object references or pointers.
internal sealed class SavedOutfit
{
    public int Schema { get; set; } = 1;
    public string GameVersion { get; set; }
    public List<SavedCosmetic> Items { get; set; } = new();
}

internal sealed class SavedCosmetic
{
    public int Category { get; set; }
    public int Index { get; set; }
    // Catalog atlas page:index, combined with AssetName and Category for identity.
    // This is not a store entitlement or transaction identifier.
    public string ItemId { get; set; } = "";
    public string AssetName { get; set; } = "";

    internal bool Identifies(SavedCosmetic other)
        => other != null && Category == other.Category && ItemId == other.ItemId
           && AssetName == other.AssetName;
}

internal sealed class OutfitStore
{
    private readonly string _path;
    private readonly Action<string> _warn;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    internal OutfitStore(string path, Action<string> warn) { _path = path; _warn = warn; }

    internal SavedOutfit Load()
    {
        foreach (string candidate in new[] { _path, _path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                if (new FileInfo(candidate).Length > 65536) throw new InvalidDataException("Save is too large.");
                var outfit = JsonSerializer.Deserialize<SavedOutfit>(File.ReadAllText(candidate));
                Validate(outfit);
                if (candidate != _path) _warn("Recovered outfit from the last backup.");
                return outfit;
            }
            catch (Exception ex) { _warn("Cannot read saved outfit: " + ex.Message); }
        }
        return null;
    }

    internal void Save(SavedOutfit outfit)
    {
        Validate(outfit);
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        string temporary = _path + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(outfit, Options);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
        // Same-directory replacement keeps an interrupted write from truncating
        // the previous complete outfit. Keep that previous version as a backup.
        if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak");
        else File.Move(temporary, _path);
    }

    internal static void Validate(SavedOutfit outfit)
    {
        if (outfit == null || outfit.Schema != 1 || string.IsNullOrEmpty(outfit.GameVersion)
            || outfit.GameVersion.Length > 128 || outfit.Items == null || outfit.Items.Count > 4)
            throw new InvalidDataException("Unsupported outfit save.");
        var categories = new HashSet<int>();
        foreach (var item in outfit.Items)
            if (item == null || item.Category < 0 || item.Category > 3 || item.Index < 0
                || item.Index > 1000000 || !categories.Add(item.Category)
                || item.ItemId == null || item.ItemId.Length > 1024
                || string.IsNullOrEmpty(item.AssetName) || item.AssetName.Length > 1024)
                throw new InvalidDataException("Invalid or duplicate cosmetic entry.");
    }
}
