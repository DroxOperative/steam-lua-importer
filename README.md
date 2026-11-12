# Steam Lua Importer (Playnite extension)

Reads Steam app IDs from a folder of *.lua files and importing the resolved IDs
as uninstalled Steam games owned by Playnite's Steam integration (source = Steam),
then selects them so you can run Download Metadata with your default settings.

## Requirements

- Playnite 10.x and SDK 6.11 (built/tested against 10.64 and SDK 6.18)
- PowerShell 5.1 or higher
- .NET 6 SDK or higher

## Install

1. Download the latest `SteamLuaImporter_xxx.pext` from Releases.
2. Double-click or drag it into an open Playnite window.
3. Click 'Yes' and restart Playnite.

## Build

1. Clone or download the repository.
2. From the repository folder, run: `powershell -ExecutionPolicy Bypass -File build.ps1`.

## Usage and considerations

Main menu > Extensions > Steam Lua Importer:

- **Import Steam games from lua folder** asks for the folder (e.g. `config\st-plugin`) the first time, then on every run:
  1. finds `{appid}.lua` files (top level only; file contents are ignored),
  2. skips app IDs already in Playnite as Steam games or on your import exclusion list,
  3. looks up each new app ID's name and type on the Steam store,
  4. imports store types `game`, `demo`, `mod` and `advertising` (configurable; see below) as uninstalled Steam games,           Source: "Steam", and
  5. selects the new games; you must then **manually click Download Metadata** on the selection if you wish to pull in           metadata at this point.
- **Change folder...** picks a different folder.
- **Clear Steam lookup cache** forgets previous store lookups.

Note: Steam's store API allows roughly 200 requests per 5 minutes, so lookups are
paced at about 1.6s each: around 40 new IDs a minute. Results are cached in
the plugin's data folder, so later runs only look up new app IDs. Cancelling keeps
everything looked up so far and imports it; the next run continues.

Settings can be edited from `config.json` in the plugin's data folder.
(edit with Playnite closed):

| Setting | Default | Meaning |
| --- | --- | --- |
| `FolderPath` | (asked) | Folder to scan |
| `AllowedTypes` | `["game", "demo", "mod", "advertising"]` | Store types to import, e.g. add `"dlc"`, `"movie"` |
| `TagName` | `""` | Tag added to imported games (empty = none; can be any custom tag) |
| `RequestDelayMs` | `1600` | Pause between store requests |
| `RetryFailedAfterDays` | `7` | When unresolved AppIDs are looked up again |
