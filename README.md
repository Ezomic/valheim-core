# Core

Shared plumbing for the Ezomic mods. You do not install this on purpose; every mod in the
suite depends on it, and your mod manager fetches it for you.

Built against the installed game (0.221.12, Unity 6000.0.61, BepInEx 5.4.23.3, Harmony 2.9).
Single DLL, no assets.

## What it does

Three things. Two are about multiplayer, and one is about two mods wanting the same field.

**It refuses a connection that would break.** If the server has Yoke 1.2.0 and you have
1.1.0, you are turned away at the door instead of playing for an hour into stacks that only
exist on one machine. Your log says exactly which mod and which versions, because the
game's own rejection screen has no room for it.

**It makes the host's settings the ones that count.** A guest keeps their own config file
(nothing is written, nothing is overwritten) but while they are on your world they play by
your numbers, and they get their own back the moment they disconnect.

**It owns the player inventory's height.** Two mods that both want extra rows cannot each
write the same private int, so instead they each state a number and Core adds them up and
writes once.

All three are off-switchable. None of them is on by accident.

This repo also carries `shared\Prefabs.cs`, which is **not** in the DLL - see below.

## Why it exists

Every mod in the suite needed the same handshake. A copy of it per mod would be one chance
per mod to get the ordering wrong, and worse, one RPC per mod racing the others on the same
connection. Registering once and letting the mods declare *what* they want rather than *how*
it happens is the whole argument for this being a package rather than a file copied around.

## Wiring a mod into it

Three lines, in `Awake`, after config is bound:

```csharp
private void Awake()
{
    YokeConfig.Bind(Config);

    Suite.Register(PluginGuid, PluginName, PluginVersion, Config);
    Suite.Sync(YokeConfig.StackMultiplier, YokeConfig.StackCap);
}
```

`Register` puts the mod on the version gate. `Sync` marks the entries the host decides. A
mod that calls neither still runs and just gets none of this, so the suite can be wired one
mod at a time.

Core is a **soft** dependency, and it is worth keeping it one. Every mod here checks
`Chainloader.PluginInfos` for Core's guid and calls into it from a separate method marked
`MethodImplOptions.NoInlining`. The JIT resolves the assemblies a method needs when it first
compiles that method, so a `Suite` call sitting directly in `Awake` drags this assembly in
before the check can prevent it, and the missing-assembly exception lands during plugin
load. Done properly, a mod runs standalone and says in the log what it is doing without.

### Requirement

```csharp
Suite.Register(PluginGuid, PluginName, PluginVersion, Config, Requirement.HostOnly);
```

`Everyone` is the default and the safe answer. Anything that registers a prefab or changes
item data is `Everyone` whether it looks like it or not: a client that cannot resolve a
prefab hash discards the ZDO as junk rather than failing loudly, so the symptom of getting
this wrong is a creature that silently does not exist for one player.

`HostOnly` says clients without the mod are welcome. They are still checked if they *do*
have it, because a half-updated group is the case that actually happens and it fails in
stranger ways than nobody having it.

### What to sync, and what not to

Registering a mod syncs its whole config file. That is the default because the opt-in version
had two entries opting in across thirteen mods, and a setting that changes the world has to
match on both ends or the two disagree silently.

Two things come out of it. **Keybinds are never synced**, because nothing about which key
opens a window can desync a world and taking someone's keys away for the evening is the kind
of sync that gets a mod uninstalled. Core knows a keybind by its type, which is the only
honest signal it has about a mod it knows nothing about.

Everything else a player would resent losing is the mod's own call:

```csharp
Suite.Local(TetherConfig.HoverText, TetherConfig.UiScale);
```

A UI scale, a colour, a hover-text toggle, a preferred unit. If a mismatch cannot desync
anything, it belongs here.

The reverse also exists, for the strange case where a key really does have to match:

```csharp
Suite.Sync(StowConfig.KeyStow);   // overrides the keybind exception
```

### Data files

```csharp
Suite.Data(File.ReadAllText(path));
```

A mod that reads a text file beside its DLL is not fully described by its version. Two ends
can run the same build and disagree about what is in that file, and the gate would pass it.
`Data` folds the contents into the same comparison. A mod that never calls it is compared as
unknown rather than as a mismatch, so an older Core on the far end costs the check and
nothing else.

### Extra inventory rows

```csharp
InventoryRows.Claim(PluginGuid, 3);   // three rows, mine
InventoryRows.Claim(PluginGuid, 0);   // give them back
```

State a number, not a height. Core sums the claims, captures the vanilla height per player
rather than adding to it, and writes the field itself.

## Config

`BepInEx\config\ezomic.valheim.core.cfg`

| Key | Default | What it does |
| --- | --- | --- |
| `EnforceVersions` | `true` | Refuse a connection when the two ends disagree. Off does not make a mismatch safe; it makes it silent |
| `EnforceBuilds` | `true` | Also refuse when both ends claim the same version and are different builds. Turn it off if you build the mods yourself on more than one machine, since the same commit in a different folder produces a different id |
| `EnforceConfig` | `true` | The host's synced settings win while you are connected |

## Shared source, which is not part of this plugin

`shared\Prefabs.cs` lives in this repo and is excluded from this DLL. Mods link it:

```xml
<Compile Include="..\core\shared\Prefabs.cs" Link="shared\Prefabs.cs" />
```

It is the runtime prefab registry every mod that invents a piece, an item or a creature
needs: `Prefabs.Keep(name, build, item, buildTool)` builds once and re-registers into every
world from the mod's own update - both of ZNetScene's lookups, ObjectDB when it is an item, a
tool's build menu when it is a piece - checking the live scene each time rather than a flag.
The steps are available separately as `Known`, `Holder`, `Clone`, `Donor`, `Register`,
`RegisterItem`, `ToolPieces`, `InTool` and `AddToTool`.

**Why source and not a class in here.** Core is a soft dependency by design: a mod without it
loses the version gate and the host's settings and otherwise works. Registration is not like
that - a mod that could not register its prefab would load, patch nothing into the world and
look broken - so putting it in this DLL would have made Core mandatory for five mods to do
anything at all. A runtime fallback would have kept both properties and cost two code paths,
the second of which only ever runs where nobody tests. One shared file is one code path.

The failure it exists to prevent is worth stating plainly, because it is silent: ZNetScene
and ObjectDB are rebuilt on every world load, including a trip to the menu and back, so a mod
that answers "registered yet?" from a static bool says yes to a scene that has never heard of
the prefab, registration early-returns, and every ZDO of that prefab is discarded as junk
with nothing written to any log. A built piece was lost to it on 2026-08-16.

## Design notes

**The handshake goes out in `OnNewConnection`**, which happens on both ends before either
sends `PeerInfo`. ZRpc delivers in order on one connection, so by the time the gate runs in
`RPC_PeerInfo` the other end's mod list has already arrived. Sending it any later means
gating on data that is not there yet, and the symptom is a gate that lets the first
connection through and works ever after.

**Only the server refuses.** The client compares too, but only to write a readable log.
There is exactly one place a connection dies, and it is `rpc.Invoke("Error",
ConnectionStatus.ErrorVersion)` on the server.

**Every disagreement is reported at once.** Fixing them one reconnect at a time is how a
five-mod mismatch becomes an evening.

**Builds are compared, not just version strings.** A version string is whatever was last
remembered to be edited, and during development every build carries the same number, so a
client three commits ahead of the server matches perfectly and connects. That is the
mismatch that actually happens, and a version check is the least able to see it.

**Synced values are swapped in memory, never written to disk.** That is the reason this is
more code than rewriting the client's config file would be. A player who joins a server with
doubled stacks must not find their own single-player world quietly changed the next evening.

**A local edit while the host is deciding gets put straight back**, via the config file's
own `SettingChanged`. Without that, the in-game config window happily lets someone drag a
slider that has no effect, and the mod looks broken rather than governed. Where a mod passed
a display tag, the entry also greys out.

**Version strings come from `PluginVersion`, not the assembly.** It is the constant the
packaging script cross-checks against the manifest, so what the gate compares is what people
actually installed.

**The inventory height is written as a field, not patched as an accessor.** Patching
`Inventory.GetHeight()` would have been tidier and is wrong: the UI reads the accessor, but
`ValidPos`, `FindEmptySlot`, `HaveEmptySlot`, `NrOfFreeStacks`, `AddItem`'s bounds check and
`Load` all read the field directly. A postfix on the accessor draws rows the inventory
itself does not believe in, and items cannot be put in them.

**The grid is opened wide before a character loads and trimmed back afterwards.** Rows are
applied from an update, which cannot run until the player exists, and that is after the
load. `Inventory.AddItem` drops any stack whose saved position is outside the current grid,
with no log and no error, and the next save then writes the inventory back without it. So
the bottom row was destroyed on every relog, for any item, from any mod. The trim never goes
below the rows the items themselves occupy.

## Limits

The gate only sees mods that call `Suite.Register`. A mod in the profile that does not is
invisible to it. That is by design, and it means the gate answers for this suite rather than
for the whole plugin folder.

## Reporting bugs

[The Discord](https://discord.gg/hJzAVaZ5wb) is the fastest route, and the right one if
you are not sure whether what you are seeing is a bug at all. Issues on
[the repo](https://github.com/Ezomic/valheim-core) work too and suit anything long.

Bring `BepInEx\LogOutput.log` if you can, and say whether you were on a server or your
own world. The log is most of the difference between a fix and a guess, and it is written
every session whether or not anything went wrong.

## Part of the Longhouse pack

This is one of [the Longhouse pack](https://thunderstore.io/c/valheim/p/Ezomic/Longhouse/),
a pinned set of my mods that installs in one click and is what the Longhouse server runs. You
do not need the pack to use this on its own, and nothing here behaves differently outside it.

[The Discord](https://discord.gg/hJzAVaZ5wb) is where the server lives if you want to play on
it: small, EU, hard combat difficulty and everything else vanilla.
