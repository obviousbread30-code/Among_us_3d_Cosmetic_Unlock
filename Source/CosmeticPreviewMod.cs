using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Il2CppSG.Airlock;
using Il2CppSG.Airlock.Customization;
using ElementList = Il2CppSystem.Collections.Generic.List<Il2CppSG.Airlock.CustomizationElement>;
using Category = Il2CppSG.Airlock.CustomizationElement.CosmeticType;
using WardrobeMap = Il2CppSystem.Collections.Generic.Dictionary<Il2CppSG.Airlock.CustomizationElement.CosmeticType, Il2CppSystem.Collections.Generic.List<Il2CppSG.Airlock.CustomizationElement>>;

[assembly: MelonInfo(typeof(LocalCosmeticPreview.PreviewMod), "Native Wardrobe Cosmetics", "0.9.0", "Scout")]
[assembly: MelonGame("Schell Games", "Among Us 3D")]
[assembly: HarmonyDontPatchAll]

namespace LocalCosmeticPreview
{
    /// <summary>
    /// Exposes the installed catalog in the real wardrobe. Selection follows the
    /// game's original handlers. Hooks never manually invoke equip/RPC handlers.
    /// Restore UI lock flags on exit; do not revert the selected player equipment.
    /// Platform purchases/entitlements are never fabricated by this plugin.
    /// </summary>
    public sealed class PreviewMod : MelonMod
    {
        internal static PreviewMod Instance;
        private readonly Dictionary<IntPtr, ElementState> _original = new();
        private CustomizationManager _manager;
        private CustomizationButtonCategories _ui;
        private WardrobeMap _originalWardrobe;
        private int _mainThread;
        private float _nextDiscovery;
        private bool _ready, _active, _refreshing, _needsRefresh, _observedEquip;
        private float _nextRefresh;
        private int _observedIndex;
        private string _lastGate;
        private Exception _pendingFailure;
        private OutfitPersistence _outfit;
        private PlayerState _pendingState;
        private Category _pendingType;
        private int _pendingIndex;
        private float _pendingDeadline;
        [ThreadStatic] private static int _ownershipScope;

        public override void OnInitializeMelon()
        {
            Instance = this;
            try
            {
                GameBindings.Validate();
                _outfit = new OutfitPersistence(message => LoggerInstance.Msg(message), message => LoggerInstance.Warning(message));
                HarmonyInstance.PatchAll(typeof(PreviewMod).Assembly);
                _ready = true;
                LoggerInstance.Msg("Native wardrobe v0.9 ready. Outfit auto-save enabled; no host mod required.");
            }
            catch (Exception ex)
            {
                HarmonyInstance.UnpatchSelf();
                LoggerInstance.Error("Wardrobe mod disabled: " + ex);
            }
        }

        public override void OnUpdate()
        {
            if (!_ready) return;
            if (_pendingFailure != null)
            {
                var failure = _pendingFailure; _pendingFailure = null;
                Fail(failure); return;
            }
            // Learn Unity's callback thread rather than assuming loader initialization
            // ran on the same thread as game Update callbacks.
            if (_mainThread == 0) _mainThread = Environment.CurrentManagedThreadId;
            try
            {
                ProcessObservedEquip();
                CheckReplication();
                if (_active && !CanUseWardrobe(_manager)) Deactivate();
                if (_active && _needsRefresh && Time.unscaledTime >= _nextRefresh)
                {
                    _nextRefresh = Time.unscaledTime + 0.5f;
                    RefreshNativeWardrobe();
                }
                if (_active || Time.unscaledTime < _nextDiscovery) return;
                _nextDiscovery = Time.unscaledTime + 0.5f;
                var managers = UnityEngine.Object.FindObjectsOfType<CustomizationManager>();
                for (int i = 0; i < managers.Length; ++i)
                    if (CanUseWardrobe(managers[i])) { Activate(managers[i]); break; }
                if (!_active)
                {
                    string reason = managers.Length == 0 ? "no active customization manager" : "wardrobe not open";
                    for (int i = 0; i < managers.Length; ++i)
                        if (managers[i] && managers[i].InWardrobe) { reason = BlockReason(managers[i]); break; }
                    if (reason != _lastGate) { _lastGate = reason; LoggerInstance.Msg("Wardrobe waiting: " + reason); }
                }
            }
            catch (Exception ex) { Fail(ex); }
        }

        private bool CanUseWardrobe(CustomizationManager manager)
        {
            return BlockReason(manager) == null;
        }

        private string BlockReason(CustomizationManager manager)
        {
            if (!_ready) return "mod disabled";
            if (Environment.CurrentManagedThreadId != _mainThread) return "not Unity callback thread";
            if (!manager || !manager.isActiveAndEnabled) return "manager inactive";
            if (!manager.InWardrobe) return "wardrobe not open";
            if (!manager.ButtonDisplay) return "native wardrobe UI not ready";
            if (manager.ButtonDisplay.InStoreMode) return "store tab open (switch to wardrobe)";
            if (!manager._dummyTransform || !manager._dummyTransform.gameObject.activeInHierarchy) return "preview dummy not visible";
            if (!manager._dummyCosmetics) return "dummy cosmetic component missing";
            // Public-lobby dummy components can have valid NetworkObjects. This
            // plugin now preserves original equip handlers and RPC authority, so
            // the old local-preview-only NetworkObject gate is inappropriate.
            if (manager.ElementCollection?.AllCustomizationElements == null) return "catalog not loaded";
            return null;
        }

        internal bool Owns(CustomizationManager manager)
            => _active && manager != null && _manager && manager.Pointer == _manager.Pointer;

        internal bool Owns(CustomizationButtonCategories ui)
            => _active && ui && _ui && ui.Pointer == _ui.Pointer;

        internal bool OverrideOwnership(CustomizationManager manager)
            => OutfitPersistence.Allows(manager) || (_ownershipScope > 0 && Owns(manager) && CanUseWardrobe(manager));

        internal IDisposable BeginOutfitRestore(CustomizationManager manager)
            => _ready ? _outfit?.BeginRestore(manager) : null;

        private void Activate(CustomizationManager manager)
        {
            _manager = manager;
            _ui = manager.ButtonDisplay;
            _originalWardrobe = CopyWardrobe(_ui._wardrobeElements);
            _active = true; // Permit rollback after partial initialization.
            CaptureAndUnlock();
            RefreshNativeWardrobe();
            _lastGate = null;
            LoggerInstance.Msg($"Exposed {_original.Count} native wardrobe entries. Selection equips your player through the game's normal path.");
        }

        private static WardrobeMap CopyWardrobe(WardrobeMap source)
        {
            if (source == null) return null;
            // Native refresh routines can clear the old dictionary in place.
            // Preserve independent lists rather than retaining that dictionary.
            var copy = new WardrobeMap();
            foreach (var category in Enum.GetValues<Category>())
            {
                if (!source.ContainsKey(category)) continue;
                var list = new ElementList();
                var originalList = source[category];
                if (originalList != null)
                    for (int i = 0; i < originalList.Count; ++i) list.Add(originalList[i]);
                copy.Add(category, list);
            }
            return copy;
        }

        private void CaptureAndUnlock()
        {
            var items = _manager.ElementCollection.AllCustomizationElements;
            for (int i = 0; i < items.Count; ++i)
            {
                var element = items[i];
                if (element == null || element.Pointer == IntPtr.Zero || element.Type == Category.NotSet) continue;
                if (!_original.ContainsKey(element.Pointer))
                    _original.Add(element.Pointer, new ElementState(element, i));
                element.IsLocked = false;
            }
        }

        /// <summary>
        /// Populate the game's actual wardrobe dictionary, including hidden items.
        /// Native containers and original records preserve atlas/index lookups.
        /// </summary>
        internal void Reindex(CustomizationButtonCategories ui)
        {
            if (!Owns(ui) || !CanUseWardrobe(_manager)) return;
            CaptureAndUnlock();
            var map = new WardrobeMap();
            var items = _manager.ElementCollection.AllCustomizationElements;
            var seen = new HashSet<IntPtr>();
            for (int i = 0; i < items.Count; ++i)
            {
                var element = items[i];
                if (element == null || element.Pointer == IntPtr.Zero || element.Type == Category.NotSet) continue;
                if (!seen.Add(element.Pointer)) continue;
                if (!map.ContainsKey(element.Type)) map.Add(element.Type, new ElementList());
                map[element.Type].Add(element);
            }
            ui._wardrobeElements = map;
            var category = ui.CurrentCategory;
            if (map.ContainsKey(category)) ui._currentElements = map[category];
        }

        private void RefreshNativeWardrobe()
        {
            if (_refreshing || !_active || !CanUseWardrobe(_manager)) return;
            _refreshing = true;
            ++_ownershipScope;
            try
            {
                _ui.RefreshCustomizationElements();
                Reindex(_ui);
                var category = _ui.CurrentCategory;
                if (category == Category.NotSet) category = Category.Hat;
                _ui.RefreshCurrentTypeTab(category, false);
            }
            finally { --_ownershipScope; _refreshing = false; _needsRefresh = false; }
        }

        internal void RefreshLocks(CustomizationManager manager)
        {
            if (!Owns(manager)) return;
            // Only set a managed flag inside the game's ownership refresh callback.
            // Calling native UI refreshes here can recursively re-enter this chain.
            _needsRefresh = true;
        }

        internal void TryReindex(CustomizationButtonCategories ui)
        {
            try { Reindex(ui); }
            catch (Exception ex) { _pendingFailure = ex; }
        }

        internal int BeginNativeSelection(CustomizationManager manager)
        {
            if (!Owns(manager)) return -1;
            int previous = _ownershipScope;
            ++_ownershipScope;
            return previous;
        }

        internal static void EndNativeSelection(int previous)
        {
            if (previous >= 0) _ownershipScope = previous;
        }

        internal void ObserveSelection(CustomizationManager manager, CustomizationElement element)
        {
            if (!Owns(manager) || element == null) return;
            if (_original.TryGetValue(element.Pointer, out var state)) ObserveSelection(manager, state.Index);
        }

        internal void ObserveSelection(CustomizationManager manager, int index)
        {
            if (!Owns(manager) || OutfitPersistence.Restoring) return;
            _outfit?.Capture(manager, index);
            _observedIndex = index;
            _observedEquip = true;
        }

        private void ProcessObservedEquip()
        {
            if (!_observedEquip) return;
            _observedEquip = false;
            if (!_active || !_manager) return;
            var items = _manager.ElementCollection?.AllCustomizationElements;
            if (items == null || _observedIndex < 0 || _observedIndex >= items.Count) return;
            var element = items[_observedIndex];
            if (element == null) return;
            if (_manager.sceneNetworked && _manager._playerState)
            {
                _pendingState = _manager._playerState;
                _pendingType = element.Type;
                _pendingIndex = _observedIndex;
                _pendingDeadline = Time.unscaledTime + 8f;
                string peerRole = _manager.Runner && _manager.Runner.IsServer ? "host" : "client";
                LoggerInstance.Msg($"Observed original network equip ({peerRole}): {element.Type}, catalog index {_observedIndex}. Awaiting replicated state.");
            }
            else LoggerInstance.Msg($"Observed original local equip: {element.Type}, catalog index {_observedIndex}.");
        }

        private void CheckReplication()
        {
            if (!_pendingState || !_pendingState.Object || !_pendingState.Object.IsValid)
            { _pendingState = null; return; }
            int actual = _pendingType switch
            {
                Category.Hat => _pendingState.HatId,
                Category.Hands => _pendingState.HandsId,
                Category.Skin => _pendingState.SkinId,
                Category.Color => _pendingState.ColorId,
                _ => -1
            };
            if (actual == _pendingIndex)
            {
                LoggerInstance.Msg($"Replicated {_pendingType} state matches requested catalog index {_pendingIndex}.");
                _pendingState = null;
            }
            else if (Time.unscaledTime >= _pendingDeadline)
            {
                LoggerInstance.Warning($"Equip not confirmed: requested {_pendingType} {_pendingIndex}, replicated value remains {actual}. Host acceptance is unverified.");
                _pendingState = null;
            }
        }

        /// <summary>Restore shared records before releasing the native wardrobe.</summary>
        internal void Deactivate()
        {
            if (!_active) return;
            _active = false;
            try
            {
                foreach (var saved in _original.Values)
                {
                    try { saved.Element.IsLocked = saved.Locked; }
                    catch (Exception ex) { LoggerInstance.Warning("Record restore: " + ex.Message); }
                }
                if (_ui)
                {
                    _ui._wardrobeElements = _originalWardrobe;
                    var category = _ui.CurrentCategory;
                    if (_originalWardrobe != null && _originalWardrobe.ContainsKey(category))
                        _ui._currentElements = _originalWardrobe[category];
                    else _ui._currentElements = new ElementList();
                    int count = _ui._currentElements.Count;
                    int perPage = Math.Max(1, _ui._elementsPerPage);
                    _ui._currentPage = 0;
                    _ui._elementsOfCurrentType = count;
                    _ui._pagesOfCurrentType = Math.Max(1, (count + perPage - 1) / perPage);
                }
                // Do not invoke native wardrobe/equip methods from an exit hook.
                // Original engine cleanup continues after this prefix returns.
            }
            finally
            {
                _original.Clear(); _originalWardrobe = null; _ui = null;
                _manager = null; _needsRefresh = false; _observedEquip = false;
            }
        }

        internal void TryDeactivate()
        {
            try { Deactivate(); }
            catch (Exception ex) { _pendingFailure = ex; }
        }

        private void Fail(Exception ex)
        {
            try { Deactivate(); } catch (Exception restoreEx) { LoggerInstance.Warning(restoreEx.ToString()); }
            _ready = false;
            HarmonyInstance.UnpatchSelf();
            LoggerInstance.Error("Native wardrobe mod disabled: " + ex);
        }

        public override void OnDeinitializeMelon()
        {
            try { Deactivate(); }
            finally { _ready = false; HarmonyInstance.UnpatchSelf(); Instance = null; }
        }

        private sealed class ElementState
        {
            internal readonly CustomizationElement Element;
            internal readonly bool Locked;
            internal readonly int Index;
            internal ElementState(CustomizationElement element, int index)
            { Element = element; Locked = element.IsLocked; Index = index; }
        }
    }

    internal static class GameBindings
    {
        internal const string SupportedVersion = "1.22.2+226575";
        private static MethodInfo Require(string name, params Type[] args)
            => AccessTools.DeclaredMethod(typeof(CustomizationManager), name, args)
               ?? throw new MissingMethodException(typeof(CustomizationManager).FullName, name);
        internal static IEnumerable<MethodBase> WriteMethods()
        {
            yield return Require("ApplyElementNetworked", typeof(int), typeof(int));
            yield return Require("ApplyElementLocal", typeof(CustomizationElement), typeof(int));
            yield return Require("ApplyCurrentCosmetics");
            yield return Require("ApplyAddOn", typeof(UnityEngine.Object));
            yield return Require("RPC_ColorSelected", typeof(int));
        }
        internal static IEnumerable<MethodBase> LockMethods()
        {
            // The zero-argument and bool overloads BOTH compile to RVA 0x22C6110.
            // Hooking them separately chains detours on the same native thunk.
            // Patch the unique int implementation instead (RVA 0x22C6120).
            yield return Require("RefreshCustomizationElementLockedStates", typeof(int));
        }
        internal static void Validate()
        {
            if (Application.version != SupportedVersion)
                throw new NotSupportedException($"Bindings target {SupportedVersion}; game reports {Application.version}.");
            Require("IsEntitled", typeof(CustomizationElement)); Require("IsEntitled", typeof(int));
            Require("ApplyElement", typeof(CustomizationElement), typeof(bool)); Require("ApplyElement", typeof(int));
            // DO NOT patch ApplySelectedObject: RVA 0x696300 is a shared setter,
            // also used by Photon matchmaking CurrentCluster/CloudRegion setters.
            foreach (var method in WriteMethods()) { }
            foreach (var method in LockMethods()) { }
        }
    }

    [HarmonyPatch]
    internal static class OwnershipPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(CustomizationManager), "IsEntitled", new[] { typeof(CustomizationElement) });
            yield return AccessTools.DeclaredMethod(typeof(CustomizationManager), "IsEntitled", new[] { typeof(int) });
        }
        [HarmonyPrefix]
        private static bool Prefix(CustomizationManager __instance, ref bool __result)
        {
            if (PreviewMod.Instance == null || !PreviewMod.Instance.OverrideOwnership(__instance)) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(CustomizationButtonCategories), "RefreshCustomizationElements", new Type[0])]
    internal static class NativeCatalogPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CustomizationButtonCategories __instance) => PreviewMod.Instance?.TryReindex(__instance);
    }

    [HarmonyPatch]
    internal static class LockRefreshPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => GameBindings.LockMethods();
        [HarmonyPostfix]
        private static void Postfix(CustomizationManager __instance) => PreviewMod.Instance?.RefreshLocks(__instance);
    }

    [HarmonyPatch(typeof(CustomizationManager), "ApplyElement", new Type[] { typeof(CustomizationElement), typeof(bool) })]
    internal static class NativeElementSelectionPatch
    {
        [HarmonyPrefix]
        private static void Prefix(CustomizationManager __instance, out int __state)
            => __state = PreviewMod.Instance?.BeginNativeSelection(__instance) ?? -1;
        [HarmonyPostfix]
        private static void Postfix(CustomizationManager __instance, CustomizationElement __0)
            => PreviewMod.Instance?.ObserveSelection(__instance, __0);
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, int __state)
        { PreviewMod.EndNativeSelection(__state); return __exception; }
    }

    [HarmonyPatch(typeof(CustomizationManager), "ApplyElement", new Type[] { typeof(int) })]
    internal static class NativeIndexSelectionPatch
    {
        [HarmonyPrefix]
        private static void Prefix(CustomizationManager __instance, out int __state)
            => __state = PreviewMod.Instance?.BeginNativeSelection(__instance) ?? -1;
        [HarmonyPostfix]
        private static void Postfix(CustomizationManager __instance, int __0)
            => PreviewMod.Instance?.ObserveSelection(__instance, __0);
        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, int __state)
        { PreviewMod.EndNativeSelection(__state); return __exception; }
    }

    [HarmonyPatch(typeof(CustomizationButtonCategories), "InitiatePurchase", new Type[0])]
    internal static class StockPurchaseButtonGuard
    {
        [HarmonyPrefix]
        private static bool Prefix(CustomizationButtonCategories __instance)
            => PreviewMod.Instance == null || !PreviewMod.Instance.Owns(__instance);
    }

    [HarmonyPatch(typeof(CustomizationManager), "CleanupFromWardrobe", new Type[0])]
    internal static class WardrobeExitPatch
    {
        [HarmonyPrefix]
        private static void Prefix(CustomizationManager __instance)
        { if (PreviewMod.Instance?.Owns(__instance) == true) PreviewMod.Instance.TryDeactivate(); }
    }

}
