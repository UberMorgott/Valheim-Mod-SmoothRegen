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

        /// <summary>Up-front heals of status effects (m_healthUpFront, e.g. Epic Loot's instant mead).</summary>
        internal static readonly RegenBuffer UpFront = new RegenBuffer(boundToOneTick: false);

        /// <summary>Everything the buffers pay out goes through here, so it lands as +1 hp steps.</summary>
        internal static readonly WholeHpPayout Payout = new WholeHpPayout();

        /// <summary>
        /// Seconds an up-front heal is spread over. Short on purpose: it is meant to be instant, so
        /// it only loses the jump, e.g. 50 hp arrives as 50 one-hp steps within a second.
        /// </summary>
        internal const float UpFrontWindow = 1f;

        /// <summary>True while the game is inside the food regen tick.</summary>
        internal static bool InFoodTick;

        /// <summary>True while a status effect applies its up-front heal.</summary>
        internal static bool InUpFront;

        /// <summary>Mead health-over-time earned since the last payout (see UpdateStatusEffectPatch).</summary>
        internal static float OverTimeEarned;

        /// <summary>The status effect being updated, while the game is inside its update.</summary>
        internal static SE_Stats OverTimeSource;

        /// <summary>True while we are paying the buffer back out, to avoid re-capturing our own heal.</summary>
        internal static bool Paying;

        internal static void Clear()
        {
            Food.Clear();
            OverTime.Clear();
            UpFront.Clear();
            Payout.Clear();
            OverTimeEarned = 0f;
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
    /// Healing meads: SE_Stats pays m_healthOverTime out in m_healthOverTimeDuration /
    /// m_healthOverTimeInterval lumps, the first only one interval (5 s by default) after the
    /// drink (SE_Stats.cs:162-170, 235-243). Buffering those lumps would still wait for the first
    /// one, so instead we pay the effect's rate, m_healthOverTime / m_healthOverTimeDuration,
    /// every frame from the moment it is added until its duration ends, and keep the vanilla lump
    /// from ever firing. Same total, same end time, no wait.
    ///
    /// m_healthPerTick lumps (SE_Stats.cs:211-222) are still diverted by the Heal prefix below,
    /// spread over the effect's m_tickInterval.
    /// </summary>
    [HarmonyPatch(typeof(SE_Stats), nameof(SE_Stats.UpdateStatusEffect))]
    internal static class UpdateStatusEffectPatch
    {
        private static void Prefix(SE_Stats __instance, float dt, out SE_Stats __state)
        {
            __state = State.OverTimeSource;
            if (__instance.m_character == null || __instance.m_character != Player.m_localPlayer) return;

            State.OverTimeSource = __instance;
            if (Plugin.Enabled.Value) PayOverTime(__instance, dt);
        }

        private static void Finalizer(SE_Stats __state) => State.OverTimeSource = __state;

        private static void PayOverTime(SE_Stats se, float dt)
        {
            // Ticks > 0 only when Setup found a health-over-time payout; we never decrement it.
            if (se.m_healthOverTimeTicks <= 0f || se.m_healthOverTimeDuration <= 0f) return;

            // Vanilla's lump fires once the timer passes the interval; restarting it every frame
            // means it never does. Switching the mod off mid-effect lets vanilla resume lumps at
            // the same rate for whatever lifetime is left.
            se.m_healthOverTimeTimer = 0f;

            // m_time is the elapsed time BEFORE this frame; base.UpdateStatusEffect advances it.
            var share = RegenMath.OverTimeShare(se.m_healthOverTime, se.m_healthOverTimeDuration, se.m_time, dt);
            if (share <= 0f) return;

            State.OverTimeEarned += share;
        }
    }

    /// <summary>
    /// SE_Stats.StartupEffects heals m_healthUpFront in one call (SE_Stats.cs:184-187), from Setup
    /// when the effect is added and from ResetTime when it is re-applied - outside
    /// UpdateStatusEffect, so the patch above never sees it.
    /// </summary>
    [HarmonyPatch(typeof(SE_Stats), nameof(SE_Stats.StartupEffects))]
    internal static class StartupEffectsPatch
    {
        private static void Prefix(SE_Stats __instance, out bool __state)
        {
            __state = State.InUpFront;
            if (__instance.m_character != null && __instance.m_character == Player.m_localPlayer)
                State.InUpFront = true;
        }

        private static void Finalizer(bool __state) => State.InUpFront = __state;
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
            else if (State.InUpFront)
            {
                buffer = State.UpFront;
                window = State.UpFrontWindow;
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
        /// Seconds until this effect's next m_healthPerTick heal. Health-over-time lumps never get
        /// here while the mod is on: UpdateStatusEffectPatch pays them itself and stops the lump.
        /// </summary>
        private static float Interval(SE_Stats se) =>
            se.m_tickInterval > 0f ? se.m_tickInterval : RegenBuffer.VanillaTickPeriod;
    }

    /// <summary>
    /// The HUD HP bars are GuiBars with a change delay: every rise restarts m_changeDelay and the
    /// bar does not move until it runs out (GuiBar.cs:73-76, 96-115). Vanilla heals every 10 s, so
    /// the delay always expires; our +1 hp steps arrive faster than it, so the bar froze while
    /// healing and jumped once it stopped (numbers, set every frame by Hud.UpdateHealth,
    /// Hud.cs:1081-1091, were fine). For the two HP bars a rise just updates the target and leaves
    /// any running delay (a damage trail) alone; drains keep vanilla behaviour.
    /// </summary>
    [HarmonyPatch(typeof(GuiBar), nameof(GuiBar.SetValue))]
    internal static class HealthBarFillPatch
    {
        private static bool Prefix(GuiBar __instance, float value)
        {
            if (!Plugin.Enabled.Value) return true;
            if (!RegenMath.FillSkipsDelay(__instance.m_firstSet, __instance.m_value, value)) return true;

            var hud = Hud.instance;
            if (hud == null || (__instance != hud.m_healthBarFast && __instance != hud.m_healthBarSlow)) return true;

            __instance.m_value = value;
            return false;
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
            chunk += State.UpFront.Take(dt);

            // A mead still running earned a share this frame; only once it stops is nothing owed.
            var meadRunning = State.OverTimeEarned > 0f;
            chunk += State.OverTimeEarned;
            State.OverTimeEarned = 0f;

            // Whole +1 hp steps, not a sliver per frame: rate R hp/s = R one-hp heals per second.
            // Flush the sub-1 rest once nothing more is owed, so the total still matches vanilla.
            var drained = !meadRunning && State.Food.Pending <= 0f && State.OverTime.Pending <= 0f && State.UpFront.Pending <= 0f;
            chunk = State.Payout.Pay(chunk, drained);

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
            if (__instance != Player.m_localPlayer) return;
            State.Clear();

            // A fresh Player starts m_foodRegenTimer at 0 and heals only once it reaches 10 s
            // (Player.cs:2449-2465), and the buffer only fills from that heal - so after spawning
            // or loading a world nothing flowed for 10 s. Due the tick now: it fires on the first
            // UpdateFood, its amount (every mod's bonus included) is spread over the next window,
            // and each later tick keeps paying the following one. Ticks run one period early
            // from here on, i.e. the smoothing leads vanilla instead of lagging it.
            if (Plugin.Enabled.Value) __instance.m_foodRegenTimer = RegenBuffer.VanillaTickPeriod;
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
