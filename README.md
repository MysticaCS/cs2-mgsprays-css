# MG Sprays 1.0.2

English | [日本語](README-ja.md)

A CounterStrikeSharp plugin that projects Workshop materials onto CS2 walls and floors as sprays.

## Supported Platforms

[CounterStrikeSharp](https://github.com/roflmuffin/counterstrikesharp) is required.

The distribution supports **Windows x64 and Linux x64**. The shared ZIP contains SQLite libraries for both operating systems.

| OS | Native SQLite Library |
| --- | --- |
| Windows x64 | `runtimes/win-x64/native/e_sqlite3.dll` |
| Linux x64 | `runtimes/linux-x64/native/libe_sqlite3.so` |

SQLite persistence and dependency loading have been verified on Windows. The inclusion and referencing of the Linux library have been checked, but **in-game operation on a Linux server has not been verified**.
## Installation and Updates

1. Copy the `addons` folder from the update files into `game/csgo/`.
2. Upload the included `workshop` folder to the Steam Workshop you use for your server assets. 
3. Restart the server or reload the plugin.

**Install the entire plugin folder, not just the plugin DLL.** The following dependencies are also required:

- `Microsoft.Data.Sqlite.dll`
- SQLitePCLRaw DLLs
- The SQLite library for your operating system (see the table above)

The update files does not overwrite existing JSON or cfg files.

## Saving Personal Preferences

A SQLite database is automatically created at:

```text
addons/counterstrikesharp/plugins/MgSprays/data/mgsprays.db
```

The table is named `player_settings`.

| Column | Stored Value |
| --- | --- |
| `steam_id` | SteamID64, stored as a `TEXT` primary key to preserve its full precision. |
| `spray_name` | The `Id` defined in JSON, such as `Spray1` or `LambdaLogo`. `NULL` means random selection. |
| `spray_volume` | An integer volume percentage from 0 to 100. The default is 100. |

A row for the player's SteamID is created and saved when they change a preference.
Players without a saved record default to **random sprays and 100% volume**.

## Configuration

### CVars (cfg)

CVars are managed in:

```text
game/csgo/cfg/mgsprays/mgsprays.cfg
```

The cfg is automatically executed only once per server process and is not reloaded on map changes.

### Images and Audio (JSON)

Image and audio settings are managed in:

```text
addons/counterstrikesharp/configs/plugins/MgSprays/MgSprays.json
```

The JSON contains `ConfigVersion`, `SpraySoundEvent`, `SpraySoundResource`, and `Sprays`.

Each image is defined by `Id`, `Name`, `Material`, `Width`, and `Height`. Set the width and height individually for each image. Both default to **48** and accept values from **1 to 256**.

See [examples/MgSprays.json](examples/MgSprays.json) for a configuration example.

## Player Commands

| Command | Action |
| --- | --- |
| `!sprays` | Display the spray list. |
| `!spray <id/name>` | Change the selected spray using its `Id` or `Name`. |
| `!sprays <id/name>` | Change the selected spray using the existing selection command. |
| `!spray` / `css_spray` | Place a spray on a wall or floor. Do not provide an argument. |
| `!sprays none` / `!spray none` | Set sprays to random selection. |
| `!sprayvol <0-100>` | Adjust the volume of all players' spray sounds heard by you. 0 mutes them. |
| `!sprayvol` | Display your current volume. |

`lp_mgspray_keep_across_rounds` controls whether sprays persist across rounds. Sprays are removed when they expire, are replaced, the map changes, or spraying is disabled.
Refer to the CVars for details.

## Audio

When using the included Soundevents definitions, set the following values in JSON:

```json
{
  "SpraySoundEvent": "LP.MGSpray.Paint",
  "SpraySoundResource": "soundevents/lp_mgspray.vsndevts"
}
```

All definitions reference the same audio file, with recipients grouped by volume. If you use a custom event name, provide matching volume-specific definitions for that name.
