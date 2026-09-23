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
        /// <summary>The 10 s food regen tick.</summary>
        internal static readonly RegenBuffer Food = new RegenBuffer();

        /// <summary>Health-over-time status effects: healing meads, per-tick healing effects.</summary>
        internal static readonly RegenBuffer OverTime = new RegenBuffer(boundToOneTick: false);

        /// <summary>True while the game is inside the food regen tick.</summary>
        internal static bool InFoodTick;

        /// <summary>The status effect being updated, while the game is inside its update.</summary>
        internal static SE_Stats OverTimeSource;

        /// <summary>True while we are paying the buffer back out, to avoid re-capturing our own heal.</summary>
        internal static bool Paying;

        internal static void Clear()
        {
            Food.Clear();
            OverTime.Clear();
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UpdateFood))]
    internal static class UpdateFoodPatch
    {
        // Save and restore rather than set/clear: a nested UpdateFood would otherwise clear the
        // flag on the inner exit and leave the outer tick unsmoothed. Vanilla never nests it.
        private static void Prefix(Player __instance, out bool __state)
        {
            __state = State.InFoodTick;
            if (__instance == Player.m_localPlayer) State.InFoodTick = true;
        }

        // Finalizer rather than Postfix: the flag must be restored even if something throws.
        private static void Finalizer(bool __state) => State.InFoodTick = __state;
    }

    /// <summary>
    /// Healing meads and other health-over-time effects are just as steppy as the food tick:
    /// SE_Stats pays m_healthOverTime out in m_healthOverTimeDuration / m_healthOverTimeInterval
    /// lumps (SE_Stats.cs:168-169, 235-243, interval defaulting to 5 s), plus one m_healthPerTick
    /// lump per m_tickInterval (SE_Stats.cs:211-222). Marking the effect being updated lets the
    /// Heal prefix below divert those lumps too, with the effect's own interval as the window, so
    /// each lump is paid out exactly as the next one arrives.
    /// </summary>
    [HarmonyPatch(typeof(SE_Stats), nameof(SE_Stats.UpdateStatusEffect))]
    internal static class UpdateStatusEffectPatch
    {
        private static void Prefix(SE_Stats __instance, out SE_Stats __state)
        {
            __state = State.OverTimeSource;
            if (__instance.m_character != null && __instance.m_character == Player.m_localPlayer)
                State.OverTimeSource = __instance;
        }

        private static void Finalizer(SE_Stats __state) => State.OverTimeSource = __state;
    }

    [HarmonyPatch(typeof(Character), nameof(Character.Heal))]
    internal static class HealPatch
    {
        private static bool Prefix(Character __instance, ref float hp)
        {
            if (!Plugin.Enabled.Value) return true;
            if (State.Paying) return true;
            if (hp <= 0f) return true;
            if (__instance == null || __instance != Player.m_localPlayer) return true;

            RegenBuffer buffer;
            float window;
            if (State.InFoodTick)
            {
                buffer = State.Food;
                window = Plugin.Window.Value;
            }
            else if (State.OverTimeSource != null)
            {
                buffer = State.OverTime;
                window = Interval(State.OverTimeSource);
            }
            else
            {
                return true;
            }

            // Split the lump: the instant share rides on the original call (still ONE heal),
            // the rest goes into the buffer. Subtracting keeps instant + smoothed == hp exactly.
            var fraction = Mathf.Clamp01(Plugin.InstantFraction.Value);
            var smoothed = hp * (1f - fraction);
            buffer.Add(smoothed, window);

            hp -= smoothed;
            return hp > 0f;
        }

        /// <summary>
        /// Seconds until this effect's next heal. An effect can in principle carry both a
        /// health-over-time payout and a per-tick one; they are indistinguishable from inside the
        /// Heal prefix, so the over-time interval wins. Worst case is one lump spread over the
        /// other's interval, never a lost or an extra hp.
        /// </summary>
        private static float Interval(SE_Stats se)
        {
            if (se.m_healthOverTime > 0f && se.m_healthOverTimeInterval > 0f) return se.m_healthOverTimeInterval;
            return se.m_tickInterval > 0f ? se.m_tickInterval : RegenBuffer.VanillaTickPeriod;
        }
    }

    // Player has both UpdateStats() and UpdateStats(float); name alone is ambiguous.
    [HarmonyPatch(typeof(Player), nameof(Player.UpdateStats), new[] { typeof(float) })]
    internal static class UpdateStatsPatch
    {
        private static void Postfix(Player __instance, float dt)
        {
            if (__instance != Player.m_localPlayer) return;

            // Switching the mod off mid-window must not strand what is still owed: without this
            // the buffer keeps its amount and its rate, and switching back on resumes paying a
            // stale heal earned minutes ago.
            if (!Plugin.Enabled.Value)
            {
                State.Clear();
                return;
            }

            // Character.RPC_Heal clamps to max health, but only AT THE TICK INSTANT: vanilla
            // damage taken at t=9.9 still collects the whole tick at t=10. So hold the food payout
            // while there is no headroom rather than draining it into a full bar, which would
            // forfeit every full-health frame and heal strictly less than no mod at all.
            // RegenBuffer.Add caps pending at one window's worth of vanilla regen, so the hold
            // can never discharge faster than vanilla's own average rate.
            var chunk = 0f;
            if (__instance.GetHealth() < __instance.GetMaxHealth()) chunk += State.Food.Take(dt);

            // Status-effect heals get no such hold: vanilla pays every one of their lumps out on
            // its own schedule and lets RPC_Heal clamp the excess away, so holding one back would
            // hand the player healing vanilla never gave.
            chunk += State.OverTime.Take(dt);

            // (Heal() is also an RPC when we are not the owner - no point calling it for nothing.)
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

    /// <summary>
    /// The buffer is static and outlives the world: returning to the main menu destroys the Player
    /// but not the plugin, so without this a tick banked by one character is paid to the next one.
    /// Game.SpawnPlayer is the only path that creates the local player - world entry and respawn
    /// both route through it - and it calls OnSpawned after SetLocalPlayer.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
    internal static class OnSpawnedPatch
    {
        private static void Postfix(Player __instance)
        {
            if (__instance == Player.m_localPlayer) State.Clear();
        }
    }

    /// <summary>Dropping or respawning should not carry a half-paid buffer across.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    internal static class OnDeathPatch
    {
        private static void Postfix(Player __instance)
        {
            if (__instance == Player.m_localPlayer) State.Clear();
        }
    }
}
