using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ezomic.Core
{
    /// <summary>
    /// Where a deed is written down, which decides almost everything else about it.
    /// </summary>
    public enum DeedScope
    {
        /// <summary>
        /// The world's. Shared by everyone who plays there, saved with the world, and never
        /// undone. Counted by the host, because a count each client keeps for itself is not
        /// the world's count - two players at four hundred trolls each never reach a
        /// thousand between them.
        /// </summary>
        World,

        /// <summary>
        /// The character's. Travels with them to any world, because that is what personal
        /// means - an achievement that resets when you visit a friend's server is a
        /// world-scoped one wearing the wrong label. Needs no networking at all: it is read
        /// and written entirely on the owning client.
        /// </summary>
        Personal
    }

    /// <summary>
    /// Whether a deed changes anything, and which way.
    ///
    /// The split is meant to be inferable without reading it: deeds of dwelling soften the
    /// land, deeds of taking harden it. A player who has seen two should be able to guess the
    /// third.
    /// </summary>
    public enum DeedMark
    {
        /// <summary>
        /// Records only. The majority, on purpose - a deed that pays out nothing can be as
        /// strange and specific as you like, because there is nothing to balance. This is
        /// what keeps the list long and the balance surface small.
        /// </summary>
        Plain,

        /// <summary>Softens the world. Deeds of dwelling: building, tending, sailing.</summary>
        Hearth,

        /// <summary>Hardens it. Deeds of taking: slaughter, razing, stripping.</summary>
        Wild
    }

    /// <summary>
    /// One thing worth writing down.
    ///
    /// Declared by whichever mod owns the idea, and carried by Core so the declaring mod
    /// never has to reference Saga. Saga fills in nothing here and interprets nothing: it
    /// counts, latches, persists, announces and lists. What a deed *means* stays with the mod
    /// that declared it, which is the only arrangement where Saga does not end up depending
    /// on every other mod in the suite.
    /// </summary>
    public sealed class Deed
    {
        /// <summary>
        /// Stable identity, and the thing a global key or a character key is named after.
        /// Conventionally "mod.deed" - Core warns about an id without a dot, because two mods
        /// both declaring "hunter" would otherwise share one latch silently.
        ///
        /// Permanent once shipped, for the same reason a prefab name is: the earned state is
        /// stored under it, so renaming an id is indistinguishable from taking the deed away
        /// from everyone who has it.
        /// </summary>
        public string Id;

        /// <summary>What the tab calls it.</summary>
        public string Name;

        /// <summary>
        /// The sentence the tab shows. Written as a statement of what was done rather than
        /// as an instruction - "a hundred trees felled", not "fell 100 trees" - because the
        /// list is a record first and a checklist second.
        /// </summary>
        public string Description;

        public DeedScope Scope;

        public DeedMark Mark;

        /// <summary>
        /// What this changed, in words, or null for a plain deed.
        ///
        /// Not optional for a marked deed and Saga says so in the log if it is missing. A
        /// passive nobody can see is indistinguishable from a bug, and the tab is the only
        /// place a player can ever find out what an earned deed did to their world.
        /// </summary>
        public string Effect;

        /// <summary>
        /// How much of <see cref="Stat"/>, or of <see cref="Deeds.Progress"/>, earns it.
        /// One means the first occurrence does.
        /// </summary>
        public float Threshold = 1f;

        /// <summary>
        /// The vanilla counter this deed reads, when there is one.
        ///
        /// Valheim already keeps 108 of these per character in PlayerProfile.m_playerStats,
        /// and every one of them is incremented through a single public method. A deed
        /// declared against a stat therefore needs no counting code anywhere - not here, not
        /// in Saga, and not in the declaring mod. Left null, the deed is counted by whoever
        /// declared it calling Progress.
        /// </summary>
        public PlayerStatType? Stat;

        /// <summary>
        /// Run once, on the client that earns it, at the moment it is earned. Optional, and
        /// null for the plain deeds that make up most of any list.
        ///
        /// This is where a marked deed's effect goes, and it runs in the declaring mod's own
        /// code rather than in Saga's. It is not called again on later loads - the latch is
        /// what persists, so anything that needs to be true for the rest of the world's life
        /// should be applied from the latch on load, not from here.
        /// </summary>
        public Action Earned;

        /// <summary>
        /// The mod that declared it, taken from the calling assembly rather than passed in,
        /// so it cannot disagree with reality. Used to group the tab and to name the source
        /// when two mods declare the same id.
        /// </summary>
        public string Source;
    }

    /// <summary>
    /// The deed registry every mod in the suite declares into.
    ///
    /// This lives in Core rather than in Saga on purpose, and the reason is the same one that
    /// keeps Surge standing alone: a mod that referenced Saga.dll would stop being installable
    /// without it, and Longhouse would have to pin the two together forever. Every mod here
    /// already references Core, so Core is where a shared vocabulary belongs.
    ///
    /// <code>
    /// // in Awake, after Suite.Register
    /// Deeds.Declare(new Deed {
    ///     Id = "thralls.hauls",
    ///     Name = "Hundred Hauls",
    ///     Description = "A thrall has carried a hundred loads to your depot.",
    ///     Scope = DeedScope.Personal,
    ///     Threshold = 100,
    /// });
    ///
    /// // wherever the thing actually happens
    /// Deeds.Progress("thralls.hauls");
    /// </code>
    ///
    /// With Saga absent both calls still compile and still run, and neither does anything:
    /// Declare costs a dictionary write at startup, and Progress costs one static bool read
    /// and returns. So a mod can declare deeds unconditionally and never ask whether Saga is
    /// installed - which is the whole point, because asking is how soft dependencies rot.
    ///
    /// Nothing here writes to a save, a key or the network. Core is the noticeboard; Saga is
    /// the only thing that reads it and the only thing that persists anything.
    /// </summary>
    public static class Deeds
    {
        /// <summary>
        /// Everything declared so far, by id. Kept whether or not anything is listening,
        /// because plugin load order is not ours to choose - a mod that declares in its Awake
        /// may well run before Saga's, and dropping those on the floor would make the
        /// contents of the tab depend on the alphabet.
        /// </summary>
        private static readonly Dictionary<string, Deed> Declared =
            new Dictionary<string, Deed>(StringComparer.Ordinal);

        /// <summary>
        /// Whether anything is actually consuming progress.
        ///
        /// A plain static bool rather than a null check on the event, so the common case -
        /// Saga not installed, a mod calling Progress in a hot path - is one read and a
        /// branch. Progress is the call that might land per kill or per swing, and it has to
        /// be free to leave in.
        /// </summary>
        private static bool _listening;

        /// <summary>Raised when a mod declares, so a Saga that started first still hears it.</summary>
        private static Action<Deed> _onDeclared;

        /// <summary>Raised by Progress. Only ever non-null while something is attached.</summary>
        private static Action<string, float> _onProgress;

        /// <summary>Raised by Earn, for deeds that are events rather than counts.</summary>
        private static Action<string> _onEarn;

        /// <summary>
        /// Declare a deed. Idempotent by id - declaring the same id again replaces the entry
        /// rather than adding a second, because a plugin reload is a reload and not a bug.
        /// </summary>
        /// <param name="owner">
        /// The declaring assembly, used only for the deed's source name. Left null it is
        /// taken from the caller, which is right for every normal case.
        /// </param>
        // NoInlining for the same reason Suite.Register has it: GetCallingAssembly answers
        // relative to this frame, and a JIT that inlined this into the caller would make
        // every deed in the suite report Core as its source.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Declare(Deed deed, Assembly owner = null)
        {
            if (deed == null) throw new ArgumentNullException(nameof(deed));
            if (string.IsNullOrEmpty(deed.Id)) throw new ArgumentException("A deed needs an id.");

            // Advisory rather than enforced. An id without a namespace is legal and works;
            // it is just the shape that collides, and a collision here is two mods quietly
            // sharing one latch rather than an error anyone would see.
            if (deed.Id.IndexOf('.') < 0)
                CorePlugin.Log.LogWarning("Deed id \"" + deed.Id + "\" has no mod prefix. Two mods "
                    + "declaring that id would share one earned state. Prefer \"mod.deed\".");

            if (deed.Source == null)
                deed.Source = NameOf(owner ?? Assembly.GetCallingAssembly());

            // Said out loud because it is the one mistake that cannot be seen from in game:
            // a deed that changes the world and never explains itself is a passive with no
            // way to find out it exists, which is the failure this whole design is arranged
            // against.
            if (deed.Mark != DeedMark.Plain && string.IsNullOrEmpty(deed.Effect))
                CorePlugin.Log.LogWarning("Deed \"" + deed.Id + "\" is marked "
                    + deed.Mark + " but says nothing about what it changes. It will show in the "
                    + "saga as marked with no explanation, which reads as a bug.");

            Deed existing;
            if (Declared.TryGetValue(deed.Id, out existing) && existing.Source != deed.Source)
                CorePlugin.Log.LogWarning("Deed \"" + deed.Id + "\" was declared by "
                    + existing.Source + " and is now being redeclared by " + deed.Source
                    + ". One of them will lose.");

            Declared[deed.Id] = deed;

            var handler = _onDeclared;
            if (handler != null) handler(deed);
        }

        /// <summary>
        /// Report progress toward a deed this mod counts itself.
        ///
        /// Only needed for things vanilla does not already count. Anything covered by a
        /// PlayerStatType should be declared with <see cref="Deed.Stat"/> instead and left
        /// alone - Saga reads the game's own counter, which is already correct for the whole
        /// life of the character and does not need the declaring mod to be loaded to stay so.
        ///
        /// Safe to call from anywhere and safe to call often. It does nothing at all when
        /// Saga is not installed.
        /// </summary>
        public static void Progress(string id, float amount = 1f)
        {
            if (!_listening) return;

            var handler = _onProgress;
            if (handler != null) handler(id, amount);
        }

        /// <summary>
        /// Earn a deed outright, for the ones that are an event rather than a count - the
        /// first time something happens, or a condition that is either true or it is not.
        /// </summary>
        public static void Earn(string id)
        {
            if (!_listening) return;

            var handler = _onEarn;
            if (handler != null) handler(id);
        }

        /// <summary>
        /// Whether a ledger is installed and listening. Mods should not normally need this -
        /// the calls above are already free without one - but it is here for anything that
        /// would otherwise do real work purely to report progress.
        /// </summary>
        public static bool Listening
        {
            get { return _listening; }
        }

        /// <summary>
        /// Take over as the ledger. Saga calls this; nothing else should.
        ///
        /// Everything declared before now is handed over immediately, and everything declared
        /// afterwards arrives through the same callback - so a ledger that loads last sees
        /// exactly what a ledger that loads first sees. That symmetry is the whole reason
        /// declarations are kept even when nobody is listening.
        /// </summary>
        public static void Attach(
            Action<Deed> onDeclared,
            Action<string, float> onProgress,
            Action<string> onEarn)
        {
            _onDeclared = onDeclared;
            _onProgress = onProgress;
            _onEarn = onEarn;
            _listening = true;

            if (onDeclared == null) return;

            // A copy, because a handler is entitled to declare deeds of its own while
            // draining this - Saga's own catalogue is declared through exactly this API, and
            // iterating the live dictionary while it grows would throw.
            var snapshot = new List<Deed>(Declared.Values);
            foreach (var deed in snapshot) onDeclared(deed);
        }

        /// <summary>
        /// Stand down, so a ledger being unloaded stops being called. Declarations are kept:
        /// the mods that made them are still loaded, and they will not declare again.
        /// </summary>
        public static void Detach()
        {
            _listening = false;
            _onDeclared = null;
            _onProgress = null;
            _onEarn = null;
        }

        /// <summary>Everything declared, for a ledger that wants to walk it rather than be told.</summary>
        public static IEnumerable<Deed> All
        {
            get { return Declared.Values; }
        }

        private static string NameOf(Assembly assembly)
        {
            if (assembly == null) return "";

            try
            {
                return assembly.GetName().Name;
            }
            catch (Exception)
            {
                // A deed with no source name groups under "" in the tab and is otherwise
                // fine. Not worth failing a declaration over.
                return "";
            }
        }
    }
}
