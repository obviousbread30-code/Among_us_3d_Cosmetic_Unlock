# au3d unlock

A MelonLoader cosmetic wardrobe mod for the Windows PC version of **Among Us 3D**. Browse the game's installed cosmetic catalog in its normal wardrobe and automatically save your outfit between sessions.

## Download and install

Download **LocalCosmeticPreview.dll** from this repository's **Releases** page, or use the ZIP package.

1. Install MelonLoader **0.7.3** for the Windows IL2CPP game.
2. Close the game and copy `LocalCosmeticPreview.dll` into its `Mods` folder.
3. Replace any older copy of this plugin; keep only one copy in `Mods`.
4. Start the game, open the normal wardrobe and choose your outfit.

The in-game plugin name is **Native Wardrobe Cosmetics**, version **0.9.0**.

## Features

- Exposes valid installed cosmetics in the native wardrobe: hats, hands/gloves, skins and colors.
- Automatically saves all four outfit slots when you make a selection.
- Restores matching saved selections during normal cosmetic initialization.
- Uses the game's original equip and replication flow. No host mod or W/trigger acceptance step is required.
- Keeps a backup of the previous saved outfit and recovers it if the main save is damaged.
- Includes the native hook fixes that resolved the stack-overflow crash seen in earlier development builds.

The tested game catalog contains 270 eligible entries. This mod uses installed game assets and does not add custom cosmetics or create platform purchase records.

## Compatibility and limitations

| Component | Supported/tested version |
| --- | --- |
| Game | Among Us 3D **1.22.2+226575** |
| Platform | Windows PC, IL2CPP, x64 |
| Loader | MelonLoader **0.7.3** |
| Managed runtime | .NET **6** |

Bindings are restricted to the exact game version above. Game updates require revalidation. PCVR compatibility has not been validated; standalone Quest and PSVR cannot use this Windows DLL.

The host's original authority and validation rules remain in place. Another client's rendered appearance has not been independently verified; a local replicated-ID match is not a second-client visual test. The game may choose a different color when your preferred color is already taken.

## Saved outfits

Choose an outfit once after installation to create:

```text
Among Us 3D/
  UserData/NativeWardrobe/outfit.json
  UserData/NativeWardrobe/outfit.json.bak
```

These are local plugin saves, separate from the game's cloud save. To reset them, close the game and remove both files. Save files and personal logs are excluded from this repository and its release assets.

## Build from source

The repository does not redistribute game or MelonLoader assemblies. Use the generated bindings from your own supported installation after running MelonLoader once.

With a .NET SDK and .NET 6 runtime installed, build without NuGet downloads:

```powershell
.\build-local.ps1 -GameRoot 'D:\SteamLibrary\steamapps\common\Among Us 3D'
```

The result is `bin/LocalCosmeticPreview.dll`; the script does not deploy it. Alternatively, use a .NET 6 targeting pack:

```powershell
dotnet build .\au3d-unlock.csproj -c Release -p:GameRoot='D:\SteamLibrary\steamapps\common\Among Us 3D'
```

## Validation

- 14 managed persistence checks cover save/load, retaining other slots, atomic replacement, backups, malformed data and asset identity checks.
- In-game selection wrote all four categories to the local save.
- A complete game restart loaded the saved outfit; the balloon hat was already selected and rendered when the wardrobe reopened.
- Public-lobby joins and local replicated cosmetic IDs were observed during development. Remote rendered appearance remains unverified.

Run the managed persistence checks with a .NET 6 SDK/targeting pack:

```powershell
dotnet run --project .\Tests\OutfitStoreTests.csproj -- .\Tests
```

## Uninstall

Close the game and remove `Mods/LocalCosmeticPreview.dll`. Remove the two outfit save files as well if you want to clear the saved selection.

Unofficial community mod; not affiliated with or endorsed by Innersloth or Schell Games.
