# WoR Mod Manager

A single-exe, Steam-Workshop-style mod manager for War of Rights. Players install, update, disable and uninstall Red's mods from the **Red's Corner** list; it also imports other local paks (optionally linked to their Nexus page, so the manager says when a new version is posted) and ReShade presets. Mods are plain `.pak` files in the game's `Assets` folder, downloaded from public GitHub releases and verified by SHA-256.

## Build and publish the exe

```powershell
.\publish.ps1     # -> publish\WoRModManager.exe (self-contained, single file), the Nexus zip, and publish\legacy\
dotnet test       # Core tests
```

## Release a new manager version

1. Bump `<Version>` in `src/WoRModManager.App/WoRModManager.App.csproj` (e.g. `1.1.0`).
2. Run `.\publish.ps1`.
3. On `JVS1328/WoR-ModManager`, create a release tagged `vX.Y.Z` (same number as `<Version>`) and attach `publish\WoRModManager.exe`,
   `publish\WoR-Mod-Manager-vX.Y.Z.zip`, and `publish\legacy\War of Rights Mod Manager.exe` (the same exe under the 1.0.x name, so copies
   still on 1.0.x can update themselves; drop it once nobody is on 1.0.x).
4. Upload the zip to Nexus for new downloads.

Running managers see the new tag and show an "update available" bar.

## Red's Corner list

`catalog.txt` at the repo root: one mod per line, the mod's releases repo URL; line order is card order; `#` starts a comment. Add a line to list a mod, delete it to remove it. Players get changes on Refresh, no new exe needed.

## Releasing a mod

Each mod has its own releases repo with a `release.ps1` and a README describing the checklist (tag `vX.Y.Z`, all paks + `changelog.txt` + `gamebuild.txt`, and a root `thumbnail.png|jpg` + `description.txt` whose first line is the Nexus link):

- https://github.com/JVS1328/WoR-MAW-releases
- https://github.com/JVS1328/WoR-1812-releases

## Where data lives

- `<Game>\ModManagerMods` (changeable in Settings): `state.json`, `hashcache.json`, `journal.json`, `Downloads\`, `Backup\`, `Disabled\<mod-id>\`.
- `%LocalAppData%\WoRModManager`: `settings.json` (game root, mods folder) and the HTTP cache.
