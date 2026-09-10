using HarmonyLib;
using UnityEngine;

namespace SmoothRegen
{
    /// <summary>
    /// Vanilla heals the player in one lump every 10 seconds from food
    /// (Player.UpdateFood -> Character.Heal). We intercept that single call and
    /// pay the same amount out frame by frame instead.
    ///
    /// Deliberately NOT a transpiler. EpicLoot rewrites the IL of Player.UpdateFood
    /// to inject its flat AddHealthRegen bonus, and matches an exact opcode pattern
    /// around the Heal call; changing that IL makes EpicLoot log a conflict and
    /// silently drop its regen effects. Leaving the tick alone also keeps EpicLoot's
    /// flat per-tick bonus applied once per tick - the old SteadyRegeneration mod
    /// raised the tick rate 100x, so that flat bonus landed 100x too, which is where
    /// the runaway regen came from.
    /// </summary>
    internal static class State
    {
        internal static readonly RegenBuffer Buffer = new RegenBuffer();

        /// <summary>True while the game is inside the food regen tick.</summary>
        internal static bool InFoodTick;

        /// <summary>True while we are paying the buffer back out, to avoid re-capturing our own heal.</summary>
        internal static bool Paying;

        /// <summary>
        /// The buffer survives a spell at full health instead of being thrown away, so it must
        /// not grow forever. Two times max health is roughly 100 seconds of good food regen -
        /// enough to cover any normal full-health stretch, small enough that an hour idle
        /// does not turn into a free instant heal.
        /// </summary>
        internal const float PendingCapMultiplier = 2f;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UpdateFood))]
    internal static class UpdateFoodPatch
    {
        private static void Prefix(Player __instance)
        {
            if (__instance == Player.m_localPlayer) State.InFoodTick = true;
        }

        // Finalizer rather than Postfix: the flag must clear even if something throws.
        private static void Finalizer() => State.InFoodTick = false;
    }

    [HarmonyPatch(typeof(Character), nameof(Character.Heal))]
    internal static class HealPatch
    {
        private static bool Prefix(Character __instance, ref float hp)
        {
            if (!Plugin.Enabled.Value) return true;
            if (!State.InFoodTick || State.Paying) return true;
            if (hp <= 0f) return true;
            if (__instance == null || __instance != Player.m_localPlayer) return true;

            // Split the lump: the instant share rides on the original call (still ONE heal),
            // the rest goes into the buffer. Subtracting keeps instant + smoothed == hp exactly.
            var fraction = Mathf.Clamp01(Plugin.InstantFraction.Value);
            var smoothed = hp * (1f - fraction);
            State.Buffer.Add(smoothed, Plugin.Window.Value,
                __instance.GetMaxHealth() * State.PendingCapMultiplier);

            hp -= smoothed;
            return hp > 0f;
        }
    }

    // Player has both UpdateStats() and UpdateStats(float); name alone is ambiguous.
    [HarmonyPatch(typeof(Player), nameof(Player.UpdateStats), new[] { typeof(float) })]
    internal static class UpdateStatsPatch
    {
        private static void Postfix(Player __instance, float dt)
        {
            if (!Plugin.Enabled.Value) return;
            if (__instance != Player.m_localPlayer) return;

            // Character.RPC_Heal clamps to max health and silently drops the excess, so only
            // draw what actually fits. At full health headroom is zero, the buffer is untouched
            // and waits for the player to take damage.
            var headroom = __instance.GetMaxHealth() - __instance.GetHealth();
            var chunk = State.Buffer.Take(dt, headroom);
            if (chunk <= 0f) return;

            State.Paying = true;
            try
            {
                // showText:false - one floating "+hp" per frame would be unreadable.
                __instance.Heal(chunk, false);
            }
            finally
            {
                State.Paying = false;
            }
        }
    }

    /// <summary>Dropping or respawning should not carry a half-paid buffer across.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    internal static class OnDeathPatch
    {
        private static void Postfix(Player __instance)
        {
            if (__instance == Player.m_localPlayer) State.Buffer.Clear();
        }
    }
}
