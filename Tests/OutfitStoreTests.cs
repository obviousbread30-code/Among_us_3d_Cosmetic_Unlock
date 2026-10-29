using System;
using System.IO;
using System.Linq;
using LocalCosmeticPreview;

int checks = 0;
void Check(bool pass, string message) { if (!pass) throw new Exception(message); ++checks; }
void Reject(SavedOutfit value, string message)
{ try { OutfitStore.Validate(value); } catch (InvalidDataException) { ++checks; return; } throw new Exception(message); }
string folder = Path.Combine(Path.GetFullPath(args[0]), "outfit-test-" + Guid.NewGuid().ToString("N"));
string path = Path.Combine(folder, "outfit.json");
var store = new OutfitStore(path, _ => { });
try
{
    Check(store.Load() == null, "Missing save should use native defaults.");
    var outfit = new SavedOutfit { GameVersion = "test-build" };
    for (int i = 0; i < 4; ++i)
        outfit.Items.Add(new SavedCosmetic { Category = i, Index = i, ItemId = "0:" + i, AssetName = "Asset " + i });
    store.Save(outfit);
    var read = store.Load();
    Check(read.Items.Count == 4 && read.GameVersion == "test-build", "Whole-outfit round trip failed.");
    Check(read.Items[0].Index == 0, "Default/unequipped index zero was lost.");
    outfit.Items[1].Index = 98;
    outfit.Items[1].AssetName = "New Hat";
    store.Save(outfit);
    read = store.Load();
    Check(read.Items[1].Index == 98 && read.Items[2].Index == 2 && read.Items[3].Index == 3, "Single edit lost other categories.");
    Check(File.Exists(path + ".bak") && !File.Exists(path + ".tmp"), "Atomic replacement or backup failed.");
    File.WriteAllText(path, "{broken JSON");
    Check(store.Load().Items[1].Index == 1, "Corrupt primary did not recover the previous complete outfit.");
    File.WriteAllText(path + ".bak", "{}");
    Check(store.Load() == null, "Invalid primary and backup should fall back safely.");
    File.WriteAllText(path, new string('x', 65537));
    Check(store.Load() == null, "Oversized save accepted.");
    Reject(new SavedOutfit { Schema = 99, GameVersion = "test" }, "Unknown schema accepted.");
    outfit.Items[1].Category = 0;
    Reject(outfit, "Duplicate categories accepted.");
    outfit.Items[1].Category = 1; outfit.Items[1].Index = -1;
    Reject(outfit, "Negative index accepted.");
    var a = new SavedCosmetic { Category = 1, Index = 98, ItemId = "2:4", AssetName = "Hat" };
    var b = new SavedCosmetic { Category = 1, Index = 99, ItemId = "2:4", AssetName = "Hat" };
    Check(a.Identifies(b), "Reordered identical catalog asset was not recognized.");
    b.AssetName = "Different Hat";
    Check(!a.Identifies(b), "Different asset identity accepted.");
    b.AssetName = "Hat"; b.Category = 2;
    Check(!a.Identifies(b), "Different category accepted.");
    Console.WriteLine($"PASS: {checks} outfit persistence checks.");
}
finally
{
    foreach (string file in new[] { path, path + ".bak", path + ".tmp" })
        if (File.Exists(file)) File.Delete(file);
    if (Directory.Exists(folder)) Directory.Delete(folder); // Empty, nonrecursive.
}
