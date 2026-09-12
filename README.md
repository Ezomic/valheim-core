# Longhouse Core

Shared library for the Ezomic Valheim mods. It handles the parts that only make sense once,
per machine rather than per mod: checking that a client and a server are running the same
mods, applying the host's settings while you are connected, and arbitrating extra player
inventory rows between mods that each want some.

You normally get it as part of [the Longhouse pack](https://thunderstore.io/c/valheim/p/Ezomic/Longhouse/).
It is a single DLL with no assets, built against Valheim 1.0.7, Unity 6000.0.75, BepInEx
5.4.23.5 and Harmony 2.9.

## Features

- **Version check.** Each Ezomic mod registers its guid, version and build id. When a client
  connects, both ends exchange that list, and the server rejects a client whose list does not
  match. The reason is written to both logs and appended to the client's "Incompatible
  version" screen, which otherwise says nothing useful.
- **Host config.** The server sends its settings for every registered mod, and the client
  uses them for as long as it is connected. Nothing is written to the client's config file,
  and the client's own values come back on disconnect.
- **Shared inventory height.** Mods claim a number of extra player inventory rows instead of
  writing `Inventory.m_height` themselves. Core sums the claims and writes the field once, so
  two mods that both want rows get both, and it protects the contents of those rows during
  loads.

All three can be turned off in the config file.

This repository also carries `shared/Prefabs.cs` and `shared/BiomeIndex.cs`, which are source
files mods link into their own projects. They are not part of the DLL. See
[For mod authors](#for-mod-authors).

## Installation

Install [BepInEx 5.4.2350](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
first. These mods use the BepInEx 5 API and will not load on BepInEx 6.

A mod manager handles it if you install the Longhouse pack, which pins Core along with every
other member. To install it on its own, drop `EzomicCore.dll` in
`BepInEx\plugins\Core\`.

Install it on the dedicated server as well as on clients. The server is the only side that
can actually refuse a connection, so a server without Core does not check anything, whatever
its clients are running.

Core is an optional soft dependency for the individual mods. Each one checks whether Core is
loaded and, if it is not, logs a line saying it is running standalone and carries on without
the version check and the host config. The exception is Delve, which requires it outright.

## Configuration

`BepInEx\config\ezomic.valheim.core.cfg`, all under a `Multiplayer` section.

| Key | Default | Effect |
| --- | --- | --- |
| `EnforceVersions` | `true` | Refuse a connection when the two ends disagree about which Ezomic mods are installed or about their versions. Turning it off does not make a mismatch safe, it makes it silent |
| `EnforceBuilds` | `true` | Also refuse when both ends claim the same version but were built from different source. Turn this off if you build the mods yourself on more than one machine: the build id depends on source paths, so the same commit in a different folder produces a different id |
| `EnforceConfig` | `true` | Apply the host's settings while connected. On a server this decides whether it sends them, on a client whether it accepts them |

## Multiplayer

The mod list goes out in `ZNet.OnNewConnection`, before either side sends `PeerInfo`, so it
has arrived by the time the check runs. Every disagreement is reported at once rather than
one per reconnect attempt.

What gets compared, per registered mod: the version string, then the build id (the assembly's
module version id, which the compiler derives from the compilation inputs), then a hash of the
mod's data file if it declared one. A missing build id or data hash from an older Core on the
far end is treated as unknown, not as a mismatch.

A mod registers as `Everyone` or `HostOnly`. `Everyone` means both ends need it at the same
version. `HostOnly` means clients without it are let in, but a client that does have it is
still checked against the host, in both directions. Skaft is `HostOnly`, for example: it is
client-side hammer repair and the server neither gains nor loses by a client having it.

The client checks too, but only to write a readable log and put the detail on the refusal
screen. There is one place a connection is actually closed, and it is on the server.

**Config sync.** Registering a mod syncs its whole config file, minus keybinds and minus
anything the mod held back with `Suite.Local`. Values are swapped in memory on the client, the
originals are kept, and they are restored when `ZNet.Shutdown` runs, which covers quitting to
the menu, being kicked and the connection dropping. If ConfigurationManager is installed, an
imposed entry is greyed out, and editing one anyway puts the host's value straight back.

Keybinds (`KeyCode` and `KeyboardShortcut` entries) are never imposed on a client unless the
mod that owns them calls `Suite.Sync` on them explicitly.

A client can set `EnforceConfig = false` and keep its own settings. The version check is what
the server enforces; the config sync is cooperative.

## Inventory rows

Valheim 1.0 has its own inventory row feature, and it evicts anything below the row count it
believes in. Core works with that rather than against it.

- The grid is capped at **9 rows**. That is vanilla's clamp in `Player.SetInventorySize` and
  there is no way past it.
- **Rows bought from the trader win.** A character that has bought rows arrives with a higher
  baseline, and mod claims get whatever is left up to 9. When claims are truncated, Core logs
  a warning naming the numbers. Nothing is dropped.
- The character's `invrows` key is written back to vanilla's own count, not the inflated one,
  so it does not compound across logins and so uninstalling Core leaves a normal character.
  Vanilla will then drop whatever was in the extra rows on the ground, which is the honest
  outcome for rows nothing is providing any more.
- Any inventory is widened while it is being read from disk and trimmed back afterwards, never
  below what the items occupy. Without this, an item saved in a row below the grid it loads
  into is silently destroyed. That applies to graves as well as to the player, which is why it
  covers every container rather than only the player's.
- The wooden panel behind the inventory grid is resized to match, measured against the grid on
  screen so it covers rows from any source. The container window is pushed down, and lifted
  back on screen if it would fall below it, overlapping the inventory instead.

If the row patches do not all apply, Core does not drive rows at all and logs an error. Rows
claimed without the load protection is the one combination that destroys items.

## For mod authors

Register from `Awake`, after binding config:

```csharp
private void Awake()
{
    YokeConfig.Bind(Config);

    Suite.Register(PluginGuid, PluginName, PluginVersion, Config);
}
```

`Suite.Register(guid, name, version, config, requirement = Requirement.Everyone, owner = null)`
puts the mod on the version check and absorbs its config for syncing. `Requirement.HostOnly`
is the other option; use `Everyone` for anything that registers a prefab or changes item data,
because a client that cannot resolve a prefab hash discards the ZDO rather than erroring.

The rest of the API:

- `Suite.Sync(entry, ...)` forces an entry into the synced set. Rarely needed, since
  registering syncs everything already, except to insist that a keybind really must match.
- `Suite.Local(entry, ...)` keeps an entry out of the host's hands: UI scale, colours, hover
  text, preferred units, anything a mismatch cannot desync.
- `Suite.Data(contents, guid = null)` folds a data file into the version check, for a mod that
  is a DLL plus a text file that decides what it does. It hashes the contents with line
  endings normalised, so a CRLF difference is not a mismatch.
- `Suite.ExplainRefusal(reason)` sets the text appended to the next connection-failure screen.
  Call it just before dropping someone.
- `Suite.Display(order, advanced, name)` returns a ConfigurationManager attributes object for
  a `ConfigDescription` tag, so the in-game settings window is ordered rather than
  alphabetical.
- `InventoryRows.Claim(PluginGuid, 3)` asks for three extra player rows, replacing whatever
  that guid asked for before. `Claim(PluginGuid, 0)` gives them back. Cheap to call every
  frame. `InventoryRows.Total` is what everyone claimed; `InventoryRows.Extra` is how much
  taller the grid actually is, which can be more.

Keep Core a soft dependency. Check `Chainloader.PluginInfos` for `ezomic.valheim.core` and put
the `Suite` call in a separate method marked `[MethodImpl(MethodImplOptions.NoInlining)]`. The
JIT resolves the assemblies a method needs when it first compiles that method, so a `Suite`
call sitting directly in `Awake` pulls in the assembly before the check can prevent it, and the
missing-assembly exception lands during plugin load.

### Shared source

`shared/Prefabs.cs` and `shared/BiomeIndex.cs` live in this repository and are excluded from
the DLL. Mods link them:

```xml
<Compile Include="..\core\shared\Prefabs.cs" Link="shared\Prefabs.cs" />
<Compile Include="..\core\shared\BiomeIndex.cs" Link="shared\BiomeIndex.cs" />
```

They are source rather than classes in the DLL because a mod that cannot register its prefab
or classify an item does not degrade, it does nothing, and putting them in Core would make
Core mandatory for every mod that uses them. A runtime fallback would mean a second code path
that only runs where nobody tests.

**`Prefabs`** is the runtime prefab registry. `Prefabs.Keep(name, build, item, buildTool)`
plus `Prefabs.Tick()` from the plugin's `Update` builds the prefab once and re-registers it
into every world: both of ZNetScene's lookups, ObjectDB when it is an item, and a tool's
build menu when it is a piece. It checks the live scene each time rather than a flag, which
matters because ZNetScene and ObjectDB are rebuilt on every world load, including a trip to
the menu and back. A registry that answers "already done?" from a static bool early-returns
into a scene that has never heard of the prefab, and every ZDO of that prefab is then
discarded with nothing written to any log. The steps are also available individually:
`Known`, `Holder`, `Clone`, `Donor`, `Register`, `RegisterItem`, `ToolPieces`, `InTool`,
`AddToTool`, and `Drop` to stop keeping one. Set `Prefabs.Log` to the plugin's own logger.

**`BiomeIndex`** answers which biome an item comes from, derived from the game's own tables:
`ZoneSystem.m_vegetation`, the spawn lists and their CharacterDrops, smelter and cooking
recipes, plus a small override string for the handful none of those reach (iron scrap, for
one, which only exists inside Sunken Crypts). Earliest biome wins. `BiomeIndex.BiomeOf(name)`
is the lookup; `BiomeForKey` and `Overrides` are the delegates the host mod fills in. Yoke and
Hirsla link it.

Fixing either file means rebuilding every mod that links it.

## Troubleshooting

**A mod's log line says `** NOT ENFORCED, Core's version gate failed to apply **`.** One of
Core's patch groups did not apply, usually after a game update. Core logs `came up DEGRADED`
with the list of what is missing instead of its normal `ready.` line. Read the errors above it.

**Clients are refused with "different build".** Both ends have the same version number but
different binaries. Rebuild whichever is behind, or set `EnforceBuilds = false` if you build
from source in more than one working folder.

**A mod in the plugins folder is not being checked.** The version check only sees mods that
call `Suite.Register`. Anything else, including third-party mods, is invisible to it.

**Settings on a client are not following the server.** Check `EnforceConfig` on both ends, and
check that the mod in question registers with Core at all. Keybinds are excluded by design.

## Bug reports

[The Discord](https://discord.gg/hJzAVaZ5wb) is the fastest route, and the right one if you
are not sure whether what you are seeing is a bug. Issues on
[the repository](https://github.com/Ezomic/valheim-core) work too and suit anything long.

Attach `BepInEx\LogOutput.log`, and say whether you were on a dedicated server, hosting, or in
single player. For a connection that was refused, the log from both ends is worth far more
than either alone. If a vanilla mechanic broke rather than a mod feature, check
`AppData\LocalLow\IronGate\Valheim\Player.log` as well: exceptions thrown mid-frame land there
and not in the BepInEx log.

## Discord

[discord.gg/hJzAVaZ5wb](https://discord.gg/hJzAVaZ5wb) is where mod information, updates,
support, bug reports and compatibility questions go.

## Server

There is also a small EU server running the pack if you want somewhere to play: hard combat
difficulty, resources at 1x, everything else vanilla. Connection details are in the Discord.

## Licence

MIT. Robbin Thijssen, Thijssen Software. See [LICENSE](LICENSE), and
[CHANGELOG.md](CHANGELOG.md) for what shipped when.

## Part of Longhouse

Core is a member of [the Longhouse pack](https://thunderstore.io/c/valheim/p/Ezomic/Longhouse/),
which pins one set of versions that a server will accept. You do not need the pack to use
Core, and it behaves the same on its own.
