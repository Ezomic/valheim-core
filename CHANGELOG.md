# Changelog

Notable changes to Core. Format follows [Keep a Changelog](https://keepachangelog.com),
and the mod uses [semantic versioning](https://semver.org).

## [0.2.0] — 2026-08-16

First published release. Earlier numbers were development only and never went out.

### The version gate

- **Refuses a connection the two ends disagree about**, before you have played an hour into
  stacks that only exist on one machine. The log names the mod and both versions, because
  the game's own rejection screen has no room to.
- **Compares builds, not just version strings.** Two ends can both claim 1.0.0 and be
  running different compilations, and that is the mismatch that gets missed — the number
  matches perfectly and the connection is allowed.
- Mods declare `Everyone` or `HostOnly`. `Everyone` is the default and the safe answer:
  anything registering a prefab is `Everyone` whether it looks like it or not, because a
  client that cannot resolve a prefab hash discards the ZDO as junk rather than failing
  loudly.
- **Core is on its own gate.** It was the one mod every other mod depends on whose mismatch
  went unreported, and a Core mismatch is worse than any of theirs because it is the
  handshake itself.

### Host settings

- While you are on someone's world you play by their numbers. **Your own config file is
  never written and never overwritten**, and your values come back the moment you
  disconnect.
- Mods choose which entries are synced rather than Core guessing.

### Elsewhere

- **Loads on dedicated servers.** It did not, which meant the one branch that can actually
  refuse a connection was unreachable on the only setup where it matters.
- **Owns the inventory height**, so two mods can both add rows without cutting each other's
  off or writing before anything has claimed space.
- **Extra rows survive a reload.** They did not, and the failure was total and silent: the
  grid is still its vanilla height when a character is read off disk, and `Inventory.AddItem`
  drops any stack whose saved position is outside the current grid — no log, no error — after
  which the next save writes the inventory back without it. Rows are applied from Core's
  update, which cannot run until the player exists, and that is after the load. So the bottom
  row was destroyed on every single relog, for any item, from any mod. The grid is now opened
  wide before the load and trimmed back afterwards, never below the rows the items themselves
  occupy.
- Runtime prefabs can be soft-referenced, matching how the game now loads its own.
- Both behaviours are off-switchable. Neither is on by accident.

### Known limits

- The gate only sees mods that call `Suite.Register`. A mod in the profile that does not is
  invisible to it, which is by design — but it means the gate answers for this suite, not
  for the whole plugin folder.
