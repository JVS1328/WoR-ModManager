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
3. In `..\WoR-ModManager-releases` (the public repo players read), write `changelog.txt` and run `.\release.ps1 X.Y.Z`.
4. Upload the zip from `publish\` to Nexus for new downloads.

Running managers see the new release and offer "Update now".

## Red's Corner list

`catalog.txt` lives in the public [WoR-ModManager-releases](https://github.com/JVS1328/WoR-ModManager-releases) repo (`..\WoR-ModManager-releases`): one mod per line, the mod's releases repo URL; line order is card order; `#` hides a line. Players get changes on Refresh, no new exe needed.

This source repo is private. The app only reads public repos: `WoR-ModManager-releases` (catalog + manager releases) and each mod's releases repo.

## Releasing a mod

Each mod has its own releases repo with a `release.ps1` and a README describing the checklist (tag `vX.Y.Z`, all paks + `changelog.txt` + `gamebuild.txt`, and a root `thumbnail.png|jpg` + `description.txt` whose first line is the Nexus link):

- https://github.com/JVS1328/WoR-MAW-releases
- https://github.com/JVS1328/WoR-1812-releases

## Where data lives

- `<Game>\ModManagerMods` (changeable in Settings): `state.json`, `hashcache.json`, `journal.json`, `Downloads\`, `Backup\`, `Disabled\<mod-id>\`.
- `%LocalAppData%\WoRModManager`: `settings.json` (game root, mods folder) and the HTTP cache.
