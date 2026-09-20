using UnityEngine;

namespace Ezomic.Shared
{
    /// <summary>
    /// The suite's one rule for turning a Crafting level into a working radius.
    ///
    /// It exists as shared source rather than as a method on Core because two mods now say
    /// the same sentence to a player - Skaft's repair sweep and Jafna's levelling reach - and
    /// two copies of a curve drift apart in exactly the way that makes "8 metres at 60" true
    /// in one mod and not the other. That is a bug nobody reports, because both numbers look
    /// reasonable on their own.
    ///
    /// It is shared source and not part of Core's DLL for the reason every other file in this
    /// folder is: Core is a soft dependency everywhere, and a mod whose central number came
    /// from an assembly that might be absent would need a second code path that only runs on
    /// machines nobody tests on. Link it instead:
    ///
    ///     &lt;Compile Include="..\core\shared\CraftingReach.cs" Link="shared\CraftingReach.cs" /&gt;
    ///
    /// Skaft still carries its own copy of this arithmetic inside Sweep.Radius. It is not
    /// being moved over here as part of Jafna's work, because Skaft is published and a
    /// refactor of a shipped mod is its own release with its own testing. It should adopt
    /// this file the next time it is opened for a real change.
    /// </summary>
    internal static class CraftingReach
    {
        /// <summary>
        /// Radius in metres for a player at <paramref name="level"/> Crafting.
        ///
        /// <c>min + (max - min) * (level / fullLevel)^curve</c>, clamped so that a level above
        /// fullLevel changes nothing and a level of zero gives exactly min.
        ///
        /// The exponent is the whole design. Below 1 it opens the reach early and grows
        /// gently; above 1 it hoards everything for the top levels, which is the wrong shape
        /// because the experience curve is already brutal up there and a second brake puts the
        /// reward somewhere players do not go. fullLevel is deliberately not 100: reaching 100
        /// Crafting is roughly 20,300 crafts against about 5,700 for 60.
        /// </summary>
        internal static float Radius(float level, float min, float max, float fullLevel, float curve)
        {
            // Guarding both divisors rather than trusting the config. Every one of these is a
            // number a player can type, and a zero fullLevel would be a NaN radius that then
            // travels into a heightmap loop as a CeilToInt - which does not throw, it just
            // iterates a nonsense range.
            float full = Mathf.Max(1f, fullLevel);
            float exponent = Mathf.Max(0.01f, curve);

            float t = Mathf.Pow(Mathf.Clamp01(level / full), exponent);

            return Mathf.Lerp(min, max, t);
        }

        /// <summary>
        /// Crafting as the player sees it in the skills window - 0 to 100 - which is the number
        /// any on-screen line about reach has to quote, because quoting the 0..1 factor the
        /// API actually returns reads as a bug in the mod.
        /// </summary>
        internal static float Level(Player player)
        {
            if (player == null) return 0f;

            return player.GetSkillFactor(Skills.SkillType.Crafting) * 100f;
        }
    }
}
