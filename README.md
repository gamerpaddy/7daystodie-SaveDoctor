# SaveDoctor – save editor & repair tool for 7 Days to Die

Finds and fixes corrupted 7 Days to Die save files, and lets you edit almost everything in them – players, inventories, skills, the world header, vehicles, drones, the electrical grid, decorations and even individual chunks of the world, with a 3D view.

It is a small local program: you start `SaveDoctor.exe`, it opens a web page in your browser (served only on `127.0.0.1`, protected by a random per-run token) and works directly on the save folder. Nothing is uploaded anywhere.

> **Stop the game / server before editing.** Writing to files the game is also writing corrupts saves. SaveDoctor detects a running game and blocks every write until you stop it (there is an override if the detection is wrong).

<!-- Put screenshots here, for example: ![overview](docs/screenshots/overview.png) -->

## Why

Dedicated servers (and the game itself) sometimes leave half-written files behind when the process is killed, the console window is closed or the power fails: region files with cut-off chunks, player files with a missing tail, zero-filled headers, truncated `multiblocks.7dt`, … The game then logs an error and silently falls back to a backup, loses land claims, or refuses to start.

SaveDoctor reads every file **with the same rules the game uses** (the formats were taken from the game's own `Assembly-CSharp.dll`), tells you exactly what is wrong and where, and offers safe repairs.

## Features

**Check & repair**
- One click scan of the whole save, every file gets a green / yellow / red state with the reason.
- Restore a damaged file from the game's own `.bak` / `.ext.bak`, or from SaveDoctor's own backups.
- Region files (`.7rg`): per-chunk validation (header, sector table, compression, chunk coordinates, block layers, entities, tile entities). Rebuild a region without its damaged chunks, or recover just the damaged chunks from a backup copy.
- `players.xml`, `multiblocks.7dt`, `decoration.7dt`, `power.dat`, the `*.nim` id tables, `main.ttw`, player `.ttp` files, explored-map `.7rm` files, PNG map tiles, …: truncation, wrong counts, duplicate keys, impossible values, ids from removed mods.
- Command line check for scripts: `SaveDoctor.exe scan "<save folder>"` (`--fix` applies the safe repairs).

**Edit**
- **Players** – name, position, vitals, kill/death counters, **inventory / backpack / equipment as an icon grid** (drag to swap, uses the icons of your game install), skills & level, quests, challenges, waypoints, spawn points, unlocked recipes. "Advanced" view shows every decoded field.
- **World header** (`main.ttw`) – world time, seed, game mode, next entity id, …
- **Vehicles & drones** – position, health, owner, lock/password, storage contents, drone orders.
- **Electrical grid** (`power.dat`) – every generator, solar panel, battery, switch, timer, trap, with fuel, charge and wiring tree; duplicate-position repair.
- **Decorations** (`decoration.7dt`) and **multi-blocks** – all attributes of every record, filter, bulk delete / replace by block type.
- **Block / item id tables** (`*.nim`), `players.xml`, any XML/JSON text file, and a **hex editor** for everything else.
- **Region view** – a top-down picture of the real terrain with icons for players, vehicles, drones, waypoints, spawns, land claims, power blocks, containers, zombies and animals; dim chunks a player has not explored; per-chunk export / import / remove; block statistics and "replace block X by Y" (e.g. clean up blocks of a removed mod).
- **3D chunk viewer** – orbit around a chunk (or 3×3 … 7×7 chunks), clipping planes on all three axes with closed cut faces, hide natural terrain to see only what players built, hover for block names and coordinates.

**Safety**
- Every write first copies the previous file to `%APPDATA%\7DaysToDie\SaveDoctorBackups\…` (and a "Snapshot backup" button zips the whole save). Everything can be restored from the *Backups* dialog.
- Writes are atomic (temp file + replace) and every rebuilt file is re-parsed before it is accepted.
- Hover texts, a log of all actions and confirmation dialogs for anything destructive.

## Download

Get the latest build from the [Releases](../../releases) page:

| File | Needs |
|---|---|
| `SaveDoctor-win-x64.zip` | the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (small download) |
| `SaveDoctor-win-x64-selfcontained.zip` | nothing (bigger, runtime included) |

Unzip anywhere, double-click `Start-SaveDoctor.bat` (or `SaveDoctor.exe`). Your browser opens; pick a save from the list or paste the path of the folder that contains `main.ttw`, usually

```
%APPDATA%\7DaysToDie\Saves\<WorldName>\<GameName>
```

Dedicated servers use the folder configured by `UserDataFolder` / `SaveGameFolder`.

Windows only (the tool itself is plain .NET and the UI is a web page, so it may run elsewhere, but process detection and paths are Windows-oriented).

## Usage tips

- Start with **Overview** – it lists every damaged file with a one-click *Auto-fix* for the safe cases.
- A damaged **player file** is best restored from `.bak`; if both are damaged open the *Advanced* tab, the *Hex* tab shows the exact offset where parsing failed.
- A damaged **region file** → *Check & repair* → *Recover damaged chunks from .bak* (keeps your builds) or *Rebuild* (drops the damaged chunks, the game regenerates natural terrain there).
- Item icons are read from `Data\ItemIcons` of your game / dedicated server install. They are found automatically in the usual Steam locations, otherwise paste the folder in the box above the inventory.
- Server stopped with the console window? Next time use `saveworld` and `shutdown` in the server console.

## How it works

SaveDoctor contains its own readers/writers for the game's binary formats, written after decompiling the game assembly:

| File | Format |
|---|---|
| `Region/r.X.Z.7rg` | sector based region file: 4 KB sectors, location + timestamp table, `ttc` chunk records with raw-deflate payload (chunk version 47) |
| `Player/<id>.ttp` | player data version 62 (entity, inventory blobs, quests, challenges, waypoints, progression …) |
| `main.ttw` | world header version 23 |
| `*.nim` | name ↔ id tables for blocks and items |
| `power.dat`, `vehicles.dat`, `drones.dat`, `turrets.dat`, `blockLimits.dat` | game state files |
| `decoration.7dt`, `multiblocks.7dt` | fixed-size record lists |
| `Player/<id>/r.X.Z.7rm` | explored-map tiles (gzip, RGB555) |

The parsers are verified on real saves by re-encoding every file and comparing it **byte for byte** with the original (`SaveDoctor.exe selftest "<save folder>"`).

Formats can change with game updates; SaveDoctor refuses to edit versions it does not know instead of guessing, and always offers the hex editor and `.bak` restore.

## Build

Requires the .NET 8 SDK.

```bash
dotnet build src/SaveDoctor.csproj -c Release
dotnet run --project src/SaveDoctor.csproj -- "<save folder>"
```

A GitHub Action (`.github/workflows/release.yml`) builds both Windows packages and attaches them to the release whenever a tag like `v1.0.0` is pushed.

Layout: `src/Core` (save access, backups, game data), `src/Formats` (one handler per file type + the binary format ports), `src/Api.cs` (local web API), `src/wwwroot` (the UI: `index.html` and the WebGL `viewer3d.js`, no external dependencies).

## Limitations

- Tile entities other than the common container type (lights, power blocks, workstations inside chunks) are not decoded deeply; chunks containing them are validated up to their entity data.
- Editing block contents of containers inside chunks is not supported yet (block replace is).
- Chunk colours in the map / 3D view are derived from block names, not from the game's textures.

## Disclaimer

Not affiliated with The Fun Pimps. Always keep a backup of your save – SaveDoctor makes them automatically, but a second copy never hurts.

## License

[Unlicense](UNLICENSE) – public domain, do whatever you want.
