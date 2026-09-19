# Changelog

Notable changes to Core. Format follows [Keep a Changelog](https://keepachangelog.com),
and the mod uses [semantic versioning](https://semver.org).

## [Unreleased]

### Added

- **Server chat lines.** A server can now put a line in each player's chat window, under a
  name it picks, the way a player's message appears. Vanilla has no way to do that: its chat
  message is drawn under the name of the connected player it came from, and a server is not a
  player, so every attempt is dropped with an error on the client. The window's own
  plain-title overload has no network path, so the client half has to be a mod every player
  has, which is Core. Crier uses it to show chat written on the Longhouse site in the chat
  window instead of the corner where the game puts notices. `<` and `>` in the line become
  spaces on arrival, as vanilla does to every chat line. An older Core ignores the call.

## [1.2.4] - 2026-09-12

### Changed

- Rewritten README. Same mod, clearer documentation: what it does and how to install it come
  first, then configuration, multiplayer behaviour, compatibility and troubleshooting. Every
  config table was checked against the plugin's own Config.Bind calls, so the settings,
  sections and defaults listed are the ones actually bound. No code changed in this release.

## [1.2.3] - 2026-09-11

Reported by a player who bought an inventory row from Haldor and found its slots drawn over
open water with no wood behind them.

### Fixed

- **A row bought from the trader had no panel behind it.** The backdrop grew the wooden panel
  by the rows *mods* had claimed above the vanilla baseline, and that baseline is learned from
  the `invrows` key - so buying a row moved the baseline up with it and the count stayed at
  zero. Vanilla sizes the panel when the window is built and never resizes one already open,
  and Core is the only other thing that writes that rect, so nothing corrected it for the rest
  of the session.

  The panel is now sized from the grid on screen against what the captured art already fits,
  both measured rather than assumed. That covers all three reasons the wood has to move at
  once - rows a mod claimed, rows the player bought, and the tick's own clamp up to whatever
  the items occupy - where only the first was counted.

- **Every chest window was a row of wood too tall, top and bottom.** The container window is a
  child of the player window, so the search for the backdrop reached the chest panel's own
  background and frame: same sprite, near enough the same width, and both filters waved them
  through. Skipped by position now, because nothing about the art separates them.

- **The chest window could end up below the screen.** It is pushed down by what the inventory
  panel gained, which is right for a row or two and puts it under the taskbar once a character
  has bought every row a trader sells. It is lifted back on now, overlapping the inventory
  instead - at that height the two cannot both fit, so the choice is which one is reachable,
  and it says in the log when it happens.

### Changed

- The backdrop runs whether or not Core's own row claims are safe to apply. It used to sit
  behind that guard, which was right when the panel was sized from claims; sized from the grid
  it cannot outrun the slots it is drawn behind. The player who reported this claims no rows at
  all, so the guard would have kept the fix from the people who need it.

- The panel work says what it measured - which rects it grabbed, what the art fits, how far the
  container was lifted. Two of the three bugs above were found by reading those numbers after
  reasoning about the hierarchy had produced a confident wrong answer.

## [1.2.2] - 2026-09-10

### Fixed

- **A character logging into 1.0 for the first time came up with a fifteen row inventory.**
  Valheim 1.0.7's `Player.OnSpawned` only calls `SetInventorySize` when the character already
  carries an `invrows` key, and otherwise just writes the key and returns. Every character
  made before 1.0 takes that second branch exactly once, on its first 1.0 login, so the prefix
  that learns vanilla's row count never fired and the baseline stayed at -1. The load-time
  widening then added its sixteen rows of working space to that -1 and the grid came up
  fifteen tall, after which the fallback measured the widened grid and adopted fifteen as
  vanilla's own height.

  Two changes. `Player.OnSpawned` now has a postfix that reads `invrows` back and learns the
  baseline from it, which is correct on both branches rather than only the broken one: on the
  else branch it returns the 4 vanilla just wrote, and on the other one `Restore` has already
  put the true base back into that key, so it is a no-op. And the widening now works from a
  height that exists rather than from an unlearned baseline, with the pre-widening height kept
  so the fallback can no longer measure the working space and mistake it for the inventory.

  No items were ever at risk - the eviction fence in `VanillaRowsDrop` holds the grid at
  whatever rows are occupied, and it did. The condition also clears itself on the next login,
  because vanilla wrote the key on the way through. It was wrong and alarming rather than
  destructive.

  Found on the live server an hour after updating it, reported as "i logged in and i have a 15
  row inventory". Fifteen is `LoadSlack - 1`, which is what identified it.

  `Player.OnSpawned` is now in `CorePlugin.Verify`'s list, so a future failure to attach that
  patch is named at startup instead of showing up as a strange row count.

## [1.2.1] - 2026-09-10

**1.2.0 shipped the wrong binary. If you have it, replace it.** The package published under
that number contains the 1.1.0 assembly, built 2026-08-28, so none of the fixes listed below
under 1.2.0 are in it - including the inventory row protection, which is the one that stops
items being destroyed. There is no code change between 1.2.0 and 1.2.1; this release exists
because a Thunderstore version number cannot be reused.

### Fixed

- **The published package now contains the code the version number claims.** `tcli build`
  assembles a package from `core/package/BepInEx/plugins/Core`, a staging folder that nothing
  refreshed or checked. It held an August build. Core was the only mod published that way -
  the other nine went up from `package.ps1` zips, and all nine were verified correct by
  installing the actual Thunderstore downloads and reading the versions they registered.

  Packaging now refuses to build when a staged assembly's version disagrees with
  `manifest.json`, so this cannot be published silently again.

  Found by updating the live server to Longhouse 2.0.0 and watching it come up announcing
  `Registered Core 1.1.0`, alongside the `Ambiguous match for Inventory.Load` that 1.2.0
  fixed - which is to say the row protection was not merely absent from the package, it was
  visibly broken on a server the pack had just been installed on.

## [1.2.0] - 2026-09-09

Rebuilt for Valheim 1.0. This version does not run on pre-1.0 Valheim, and the previous
one does not run on 1.0.

### Fixed

- **A failure in one patch group no longer takes the others with it, and the inventory
  patches apply first.** They used to be five bare `PatchAll` calls with the ZNet handshake
  leading. A throw there meant the last two never ran - while the component was still added
  and `Update` still ticked, so the extra inventory rows kept being claimed with the
  `Player.Load` protection that keeps items in them absent. That combination destroys the
  bottom row on every relog and at every grave. Protect the data first, then wire the network.
- **The inventory load protection applies again on Valheim 1.0.** 1.0 added a second
  `Inventory.Load` overload, so naming the method alone became an ambiguous match and Harmony
  refused it. Both overloads are named now. This is exactly the failure the reordering above
  was written for, and it arrived on the first launch against 1.0 - the rows were refused and
  the reason was on screen, instead of items going quietly missing.
- **A HostOnly mod is now allowed to be absent on either end.** The manifest always sent each
  mod's requirement and the reading end threw it away, so `HostOnly` only ever protected the
  direction where the *client* lacked the mod. A Core server without Skaft refused every
  client that had it - which is why Skaft shipped standalone and stayed out of the pack for a
  week. It is in the pack now.

### Changed

- **The log says when the gate is not actually there.** Every mod's `Registered ...` line
  carries `** NOT ENFORCED **` when the handshake patches did not apply, and Core prints
  `came up DEGRADED` naming what is missing rather than its usual `ready.` line. Twelve
  confident lines describing a gate that was never wired is how this used to read.

## [1.1.0] - 2026-08-23

Two changes, both about who owns what. Neither touches a prefab name or a saved value, so
nothing already in a world is at stake.

**Not in this release: the save-on-inventory-change guard.** It is written and it works, and
it is held back on the `saveguard` branch rather than shipped, because it changes when every
player's character is written to disk and this release is not the one to find that out in.


### Fixed

- **A host no longer takes your keybinds.** Registering a mod syncs its whole config file,
  which is right for anything a mismatch could desync and wrong for the one class of setting
  the readme had already warned against in as many words. Three mods here bind a key -
  Tether, Vaettir's stow and Devkit - so for as long as this stood, joining a server moved
  your keys to whatever the host had chosen, greyed the entry out, and put the value back if
  you tried to change it. Keybinds are now held back by type. A mod that genuinely needs one
  to match can still say so with `Suite.Sync`.

### Added

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

  Linked so far by Taum; the five mods carrying their own copy are a change each.

## [1.0.2] - 2026-08-19

**Dying no longer eats what was on the extra rows.** One fix, and nothing else is in this
release: it is 1.0.1 with a single patch class added, so that the mod every player is
required to have moves as little as possible.

### Fixed

- **A grave loses the rows it was buried with.** `InventoryRows` already widened the player's
  grid before `Player.Load`, which fixed relogging and left the worse half of the same bug
  standing - the items it ate were the ones you died holding.

  Vanilla's own path, in order. `Player.CreateTombStone` copies the player's width and height
  onto the grave, so the grave is born the right size and **nothing is lost yet** - which is
  exactly why looting your own grave straight away looks fine. But a grave is a Container, so
  its inventory round-trips through the ZDO, and `Inventory.Save` writes a version, a count
  and the items and **not the height**. The grid is rebuilt from the tombstone prefab's own
  height, which is vanilla's. Then `Inventory.Load` re-adds each item at its saved position
  through a private `AddItem` that ends:

      AddItem(component.m_itemData, component.m_itemData.m_stack, pos.x, pos.y);
      UnityEngine.Object.Destroy(gameObject);
      return true;

  The positional `AddItem` starts with a bounds check and returns false when `y >= m_height`
  - and **that result is thrown away**. The item is instantiated, refused, never added, and
  destroyed, and the method returns true regardless. Then the grave saves again without it.

  So the loss is silent *and* delayed. Loot the grave before its zone unloads and everything
  is there; relog or walk away first and the bottom row is gone. That is what made it read as
  random rather than as a rule.

  The fix is the shape the player one already had: open the grid up for the duration of the
  load, let the items land where they were, then let the contents decide the height. It is
  applied to every inventory rather than only to graves, because the defect is not specific
  to graves - any container read into a grid shorter than the one that wrote it deletes the
  difference, and no caller can be told apart at this level. Widening can only ever keep an
  item that would otherwise have been destroyed; being wrong here costs a container that
  draws one row too many until it is emptied.

### Not in this release

Core's main branch also carries the keybind change, the move of `Prefabs.cs` out of this DLL
into shared source, and a save-on-inventory-change guard. **None of them are here.** This
release was cut from the 1.0.1 commit with the grave fix alone applied on top, because Core
is the one mod every player must have at the same build, and a version gate is not the place
to ship four things when one was asked for.

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
