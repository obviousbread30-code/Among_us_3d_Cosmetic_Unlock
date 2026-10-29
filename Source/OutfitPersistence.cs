using System;
using System.IO;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppSG.Airlock;
using Il2CppSG.Airlock.Customization;
using MelonLoader.Utils;

namespace LocalCosmeticPreview;

internal sealed class OutfitPersistence
{
    private readonly OutfitStore _store;
    private readonly Action<string> _log, _warn;
    private SavedOutfit _saved;
    private bool _saveDirty;
    [ThreadStatic] private static RestoreScope _scope;
    internal static bool Restoring => _scope != null;
    internal static bool Allows(CustomizationManager manager)
        => _scope != null && manager && _scope.Manager == manager.Pointer;

    internal OutfitPersistence(Action<string> log, Action<string> warn)
    {
        _log = log; _warn = warn;
        string path = Path.Combine(MelonEnvironment.UserDataDirectory, "NativeWardrobe", "outfit.json");
        _store = new OutfitStore(path, warn);
        _saved = _store.Load();
        _log(_saved == null ? "Outfit saving enabled: " + path : "Loaded saved outfit: " + path);
    }

    private static SavedCosmetic Describe(CustomizationElement item, int index)
        => new() { Category = (int)item.Type, Index = index,
            ItemId = item.AtlasPage.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + item.AtlasIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AssetName = item.name };

    internal void Capture(CustomizationManager manager, int selectedIndex)
    {
        if (Restoring) return; // Native startup reapplication is not a user edit.
        try
        {
            var catalog = manager.ElementCollection?.AllCustomizationElements;
            if (catalog == null || selectedIndex < 0 || selectedIndex >= catalog.Count) return;
            var selected = catalog[selectedIndex];
            if (!selected || (int)selected.Type < 0 || (int)selected.Type > 3) return;
            bool first = _saved == null;
            _saved ??= new SavedOutfit { GameVersion = GameBindings.SupportedVersion };
            if (first)
            {
                // First edit captures the rest of the currently selected outfit
                // too, including explicit default/no-hat records.
                var ids = manager._cosmeticIDs;
                for (int category = 0; ids != null && category < ids.Count && category < 4; ++category)
                {
                    var variable = ids[category];
                    if (!variable) continue;
                    int index = variable.Value;
                    if (index < 0 || index >= catalog.Count) continue;
                    var item = catalog[index];
                    if (item && (int)item.Type == category) _saved.Items.Add(Describe(item, index));
                }
            }
            var next = Describe(selected, selectedIndex);
            int at = _saved.Items.FindIndex(i => i.Category == next.Category);
            if (!_saveDirty && !first && at >= 0 && _saved.Items[at].Index == next.Index && _saved.Items[at].Identifies(next)) return;
            if (at >= 0) _saved.Items[at] = next; else _saved.Items.Add(next);
            _saved.GameVersion = GameBindings.SupportedVersion;
            _saveDirty = true;
            _store.Save(_saved);
            _saveDirty = false;
            _log($"Saved outfit selection: {selected.Type} {selectedIndex}.");
        }
        catch (Exception ex) { _warn("Outfit save failed (wardrobe remains usable): " + ex.Message); }
    }

    internal IDisposable BeginRestore(CustomizationManager manager)
    {
        if (_saved == null || !manager || !manager.isActiveAndEnabled) return null;
        RestoreScope scope = null;
        try
        {
            var catalog = manager.ElementCollection?.AllCustomizationElements;
            var ids = manager._cosmeticIDs;
            if (catalog == null || ids == null || ids.Count < 4) return null;
            // The native manager's initialization/equip flow targets its local
            // rig. We only seed its local preference variables; the original
            // routine retains responsibility for networking and color conflicts.
            var resolved = new List<SavedCosmetic>();
            foreach (var saved in _saved.Items)
            {
                int index = -1;
                if (saved.Index < catalog.Count && catalog[saved.Index]
                    && saved.Identifies(Describe(catalog[saved.Index], saved.Index))) index = saved.Index;
                else
                {
                    // Never blindly reuse a catalog index after asset changes.
                    for (int i = 0; i < catalog.Count; ++i)
                        if (catalog[i] && saved.Identifies(Describe(catalog[i], i)))
                        { if (index >= 0) { index = -1; break; } index = i; }
                }
                if (index >= 0 && ids[saved.Category]) resolved.Add(Describe(catalog[index], index));
            }
            if (resolved.Count == 0) return null;
            scope = new RestoreScope(manager.Pointer);
            foreach (var item in resolved)
            {
                var variable = ids[item.Category];
                // Do not Raise the variable's event here: subscribers can reenter
                // initialization. The original method immediately consumes Value
                // and performs the normal equip/replication itself.
                variable.runtimeValue = item.Index;
                variable.loaded = true;
            }
            _log($"Restored {resolved.Count} saved outfit categories before native initialization.");
            return scope;
        }
        catch (Exception ex)
        { scope?.Dispose(); _warn("Outfit restore skipped: " + ex.Message); return null; }
    }

    private sealed class RestoreScope : IDisposable
    {
        internal readonly IntPtr Manager;
        private readonly RestoreScope _previous;
        internal RestoreScope(IntPtr manager) { Manager = manager; _previous = _scope; _scope = this; }
        public void Dispose() { if (_scope == this) _scope = _previous; }
    }
}

// These are distinct, audited native methods. No equip method is called from
// inside its own patch; the prefix seeds data and the original engine code runs.
[HarmonyPatch]
internal static class RestoreSavedOutfitPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(CustomizationManager), "VerifyCosmetics", Type.EmptyTypes);
        yield return AccessTools.DeclaredMethod(typeof(CustomizationManager), "ApplyCurrentCosmetics", Type.EmptyTypes);
    }
    [HarmonyPrefix]
    private static void Prefix(CustomizationManager __instance, out IDisposable __state)
        => __state = PreviewMod.Instance?.BeginOutfitRestore(__instance);
    [HarmonyFinalizer]
    private static Exception Finalizer(Exception __exception, IDisposable __state)
    { __state?.Dispose(); return __exception; }
}
