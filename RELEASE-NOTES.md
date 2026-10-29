# au3d unlock v0.9.0

Native wardrobe cosmetics with automatic outfit saving for Windows Among Us 3D **1.22.2+226575**, MelonLoader **0.7.3**, IL2CPP / .NET 6.

- Browse installed hats, hands/gloves, skins and colors in the game's normal wardrobe.
- Automatically save all four outfit slots and restore matching entries between sessions.
- Recover the previous outfit from a local backup if the current save is damaged.
- No host mod, custom approval prompt, or W/trigger acceptance step required.
- Retains the native hook fixes for earlier stack-overflow crashes.

**Install:** close the game and copy `LocalCosmeticPreview.dll` into `Mods`, replacing the older copy. Choose your outfit once to create the local save. The ZIP includes the DLL under `Mods`, installation instructions, and source.

**Validation:** 14 managed persistence checks passed. An in-game outfit save survived a full restart, and the restored hat was selected and rendered when the wardrobe reopened.

**Limits:** exact game version only. Remote rendered appearance has not been independently verified. Original server validation and color-conflict rules remain in place. PCVR is unverified; standalone Quest/PSVR are unsupported.
