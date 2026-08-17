# Core

Shared plumbing for the Ezomic mods. You do not install this on purpose; every mod in the
suite depends on it, and your mod manager fetches it for you.

Built against the installed game (0.221.12, Unity 6000.0.61, BepInEx 5.4.23.3, Harmony 2.9).
Single DLL, no assets.

## What it does

Two things, both about multiplayer.

**It refuses a connection that would break.** If the server has Hoard 1.2.0 and you have
1.1.0, you are turned away at the door instead of playing for an hour into stacks that only
exist on one machine. Your log says exactly which mod and which versions, because the
game's own rejection screen has no room for it.

**It makes the host's settings the ones that count.** A guest keeps their own config file
(nothing is written, nothing is overwritten) but while they are on your world they play by
your numbers, and they get their own back the moment they disconnect.

Both are off-switchable. Neither is on by accident.

## Why it exists

Nine mods needed the same handshake. Nine copies of it would be nine chances to get the
ordering wrong, and worse, nine RPCs racing each other on the same connection. Registering
once and letting the mods declare *what* they want rather than *how* it happens is the whole
argument for this being a package rather than a file copied around.

## Wiring a mod into it

Three lines, in `Awake`, after config is bound:

```csharp
private void Awake()
{
    HoardConfig.Bind(Config);

    Suite.Register(PluginGuid, PluginName, PluginVersion, Config);
    Suite.Sync(HoardConfig.StackMultiplier, HoardConfig.StackCap);
}
```

`Register` puts the mod on the version gate. `Sync` marks the entries the host decides. A
mod that calls neither still runs and just gets none of this, so the nine can be wired one
at a time.

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

Sync anything a mismatch would desync: item data, stack sizes, a range that decides whether
two clients agree a chest is in reach.

Leave keybinds, messages and anything cosmetic alone. Forcing a host's keybinds onto a guest
is the kind of sync that gets a mod uninstalled.

## Config

`BepInEx\config\ezomic.valheim.core.cfg`

| Key | Default | What it does |
| --- | --- | --- |
| `EnforceVersions` | `true` | Refuse a connection when the two ends disagree. Off does not make a mismatch safe; it makes it silent |
| `EnforceConfig` | `true` | The host's synced settings win while you are connected |

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
