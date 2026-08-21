# Changelog

Notable changes to Core. Format follows [Keep a Changelog](https://keepachangelog.com),
and the mod uses [semantic versioning](https://semver.org).

## [Unreleased]

### Fixed

- **A host no longer takes your keybinds.** Registering a mod syncs its whole config file,
  which is right for anything a mismatch could desync and wrong for the one class of setting
  the readme had already warned against in as many words. Three mods here bind a key -
  Tether, Vaettir's stow and Devkit - so for as long as this stood, joining a server moved
  your keys to whatever the host had chosen, greyed the entry out, and put the value back if
  you tried to change it. Keybinds are now held back by type. A mod that genuinely needs one
  to match can still say so with `Suite.Sync`.

### Added

- **A crash no longer costs you half an hour of inventory.** `Game.m_saveInterval` is 1800
  seconds, and outside it your character is written only by a clean quit and by sleeping.
  Everything between the last of those and a crash is thrown away. On a dedicated server only
  one of the two machines rolls back, so the failure is not "you lose progress" - it is that
  loot taken out of a chest before the crash exists nowhere afterwards, and loot put *into*
  one exists twice. Core now watches `Inventory.Changed` on the local player, which is the
  game's own answer to "these are not the items you had", and reaches every route into it:
  chests, crafting, pickups, drops, quick-stack, eating. It marks the character dirty rather
  than saving, because dumping a haul into a wall of chests fires that once per chest and
  twenty writes in twenty seconds is twenty Steam cloud round trips for one trip home. At
  most one save per `SaveGapSeconds` (30 by default), and the flag stays set until a save
  actually happens, so the last change always lands. Only `SavePlayerData` and
  `profile.Save()` are called, not `Game.SavePlayerProfile` - the third thing that does is
  `Minimap.SaveMapData`, which recompressed 8.4MB down to 9.3KB in this machine's own log and
  is most of the cost of a save. `m_saveTimer` is deliberately not reset, so vanilla's
  thirty-minute save still runs and still writes the map.
- **`Suite.Local`**, the other half of that. Keybinds are the only thing Core can recognise
  on its own, and they are not the only thing a player would resent losing - a UI scale, a
  colour, a hover-text toggle. Anything a mismatch cannot desync belongs to the player, and
  only the mod knows which of its settings those are.
- **`shared\Prefabs.cs`: one runtime prefab registry for the suite, as shared source rather
  than as part of this DLL.** Five mods have their own copy, and the copies are not the
  point: the wrong version of this destroys saved objects in silence. ZNetScene and ObjectDB
  are rebuilt on every world load, including a trip to the menu and back, so a mod answering
  "registered yet?" from a static bool says yes to a scene that has never heard of the
  prefab, registration early-returns, and every ZDO of it is discarded as junk with nothing
  written to any log. Stow lost a built piece that way on 2026-08-16. Everything here asks
  the live scene instead. `Prefabs.Keep` takes a name and a builder and holds the thing
  registered for whatever world is loaded - both of ZNetScene's lookups, ObjectDB when it is
  an item, a tool's build menu when it is a piece.

  It is linked into each mod's csproj and excluded from this project, so **Core gains no new
  responsibility and the mods gain no new dependency**. That is the whole reason it is a file
  and not a class in here: Core is soft everywhere, and a mod that could not register its
  prefab would load, patch nothing into the world and look broken - so owning this would have
  made Core mandatory for five mods to do anything at all. A runtime fallback was considered
  and rejected for costing two code paths, the second of which only ever runs where nobody
  tests, which is how the bug above survived in the first place.

  Nothing links it yet; that is a change per mod.

## [1.0.1] - 2026-08-18

Documentation only. No code changed, and the DLL differs from 1.0.0 only in the version it
reports.

### Added

- **The readme says where to report a bug.** It did not, in any mod here, so anyone who
  installed this from Thunderstore had the comment section and nothing else - which is not a
  route for anything that needs a log file attached. Discord first, because the common case
  is a player who cannot tell whether what they are seeing is a bug, a config value or
  vanilla, and that is a conversation rather than an issue.

## [1.0.0] - 2026-08-18

Core is what the suite actually needs from it and nothing else.

### Removed

- **The deed registry and the soft-reference asset loader are gone.** Both were written for
  mods that are not in this release, and shipping the plumbing for something nobody can
  install is how a shared library turns into a junk drawer. They live on the
  `deeds-and-softref` branch and come back with the mods that use them, not before.
- The `SoftReferenceableAssets` reference goes with them, so Core now builds against
  assembly_valheim, assembly_utils, four Unity assemblies, BepInEx and Harmony.

### Changed

- The README describes all three of the things this does. It described two, and the
  inventory height had never been written down anywhere a person would look.
- `EnforceBuilds` is in the config table. It has been in the config file since 0.2.0 and
  missing from the documentation for exactly as long.

## [0.2.0] - 2026-08-16

First published release. Earlier numbers were development only and never went out.

### The version gate

- **Refuses a connection the two ends disagree about**, before you have played an hour into
  stacks that only exist on one machine. The log names the mod and both versions, because
  the game's own rejection screen has no room to.
- **Compares builds, not just version strings.** Two ends can both claim 1.0.0 and be
  running different compilations, and that is the mismatch that gets missed. The number
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
  drops any stack whose saved position is outside the current grid, with no log and no error, after
  which the next save writes the inventory back without it. Rows are applied from Core's
  update, which cannot run until the player exists, and that is after the load. So the bottom
  row was destroyed on every single relog, for any item, from any mod. The grid is now opened
  wide before the load and trimmed back afterwards, never below the rows the items themselves
  occupy.
- Runtime prefabs can be soft-referenced, matching how the game now loads its own.
- Both behaviours are off-switchable. Neither is on by accident.

### Known limits

- The gate only sees mods that call `Suite.Register`. A mod in the profile that does not is
  invisible to it, which is by design, but it means the gate answers for this suite, not
  for the whole plugin folder.
