using System.Collections.Generic;
using System.Reflection;
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

        /// <summary>
        /// Damage-over-time health loss still owed: burning, spirit, poison, smoke ticks (see
        /// DotTickPatch). Paid out like the heal buffers, netted against them in one payout.
        /// </summary>
        internal static readonly PortionBuffer Dot = new PortionBuffer();

        /// <summary>The damage-over-time effect being updated, while the game is inside its update.</summary>
        internal static StatusEffect DotSource;

        /// <summary>True while Character.ApplyDamage runs for a damage-over-time tick of the local player.</summary>
        internal static bool InDotHit;

        /// <summary>The last DoT tick's hit, so a death from the deferred loss keeps vanilla's m_lastHit.</summary>
        internal static HitData LastDotHit;

        /// <summary>True while we write deferred damage, so the SetHealth prefix lets it through.</summary>
        internal static bool PayingDamage;

        /// <summary>Damage the local player has taken in vanilla's eyes but not yet in health.</summary>
        internal static float DotOwed => Dot.Pending + Payout.Debt;

        internal static void Clear()
        {
            Food.Clear();
            OverTime.Clear();
            UpFront.Clear();
            Dot.Clear();
            Payout.Clear();
            OverTimeEarned = 0f;
            LastDotHit = null;
        }

        /// <summary>
        /// Pay every owed DoT hp at once: before any other damage lands (so that hit meets vanilla's
        /// health), and when smoothing is switched off (owed damage is never forfeited).
        /// </summary>
        internal static void SettleDot(Player p)
        {
            if (Gone(p))
            {
                DropDot();
                return;
            }
            // Not the owner right now: keep the debt for when we are, never forgive it.
            if (!CanWrite(p)) return;
            var owed = Dot.Pending + Payout.TakeDebt();
            Dot.Clear();
            if (owed > 0f) DamageHealth(p, owed);
        }

        /// <summary>Dead, or downed by another mod (health 0 without IsDead): nothing left to take.</summary>
        internal static bool Gone(Player p) => p == null || p.IsDead() || p.GetHealth() <= 0f;

        internal static void DropDot()
        {
            Dot.Clear();
            Payout.TakeDebt();
        }

        /// <summary>Lower the debt to <paramref name="owed"/> (a clamp vanilla would not have suffered).</summary>
        internal static void ReduceOwed(float owed)
        {
            var drop = DotOwed - Mathf.Max(0f, owed);
            if (drop <= 0f) return;
            drop -= Payout.ForgiveDebt(drop);
            Dot.Forgive(drop);
        }

        /// <summary>
        /// Health is replicated through the owner's ZDO (Character.SetHealth writes only on the owner,
        /// Character.cs:3015-3028); the debt is local. Only the local, living owner pays it.
        /// Hamingja's downed state sits at health 0 without IsDead.
        /// </summary>
        internal static bool CanWrite(Player p) =>
            p != null && p == Player.m_localPlayer && p.m_nview != null && p.m_nview.IsValid() &&
            p.m_nview.IsOwner() && !p.IsDead() && p.GetHealth() > 0f;

        /// <summary>
        /// Write deferred DoT loss straight to health, as the tick's own SetHealth would have
        /// (Character.ApplyDamage, Character.cs:2459-2467). Only reaches 0 when health dropped by a
        /// path that bypasses ApplyDamage since the tick (max health falling, a mod's SetHealth);
        /// then the death keeps the DoT hit as m_lastHit, as vanilla's own tick would have.
        /// </summary>
        internal static void DamageHealth(Player p, float amount)
        {
            var health = p.GetHealth() - amount;
            if (health <= 0f)
            {
                if (p.InGodMode() || p.InGhostMode()) health = 1f;
                else if (LastDotHit != null) p.m_lastHit = LastDotHit;
            }

            PayingDamage = true;
            try
            {
                p.SetHealth(health);
            }
            finally
            {
                PayingDamage = false;
            }
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
            if (hp <= 0f) return true;
            if (__instance == null || __instance != Player.m_localPlayer) return true;
            if (!Plugin.Enabled.Value || State.Paying) return true;

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
    /// Damage-over-time status effects hurt the player in lumps: SE_Burning (fire every 1 s, spirit
    /// every 0.5 s; SE_Burning.cs:30-56), SE_Poison (1 s; SE_Poison.cs:25-47) and SE_Smoke (1 s;
    /// SE_Smoke.cs:22-35) each call Character.ApplyDamage(hit, showDamageText: true,
    /// triggerEffects: false) once per interval with an attacker-less hit whose amount is already past
    /// resistances and armor (Character.RPC_Damage splits it off, Character.cs:2390-2399).
    ///
    /// That call still runs in full - damage number, stats, m_lastHit, OnDamaged, every other mod's
    /// hooks see vanilla's tick. Only its health write is deferred (SetHealthPatch) and paid out over
    /// the effect's own interval. Not covered on purpose: SE_Stats m_healthPerTick &lt; 0 (Freezing
    /// -1 hp / 1 s, Puke -1 hp / 2 s) and SE_Wet go through Character.Damage, i.e. RPC_Damage with
    /// armor, which is not linear, and are already 1 hp lumps at most.
    /// </summary>
    [HarmonyPatch]
    internal static class DotTickPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.UpdateStatusEffect));
            yield return AccessTools.Method(typeof(SE_Poison), nameof(SE_Poison.UpdateStatusEffect));
            yield return AccessTools.Method(typeof(SE_Smoke), nameof(SE_Smoke.UpdateStatusEffect));
        }

        private static void Prefix(StatusEffect __instance, out StatusEffect __state)
        {
            __state = State.DotSource;
            if (__instance.m_character != null && __instance.m_character == Player.m_localPlayer)
                State.DotSource = __instance;
        }

        private static void Finalizer(StatusEffect __state) => State.DotSource = __state;

        /// <summary>Seconds until the effect's next tick: the window its loss is spread over.</summary>
        internal static float Interval(StatusEffect se)
        {
            float interval;
            switch (se)
            {
                case SE_Burning burning: interval = burning.m_damageInterval; break;
                case SE_Poison poison: interval = poison.m_damageInterval; break;
                case SE_Smoke smoke: interval = smoke.m_damageInterval; break;
                default: interval = 1f; break;
            }
            return interval > 0f ? interval : 1f;
        }
    }

    /// <summary>
    /// Marks the ApplyDamage call of a DoT tick (for SetHealthPatch), and settles owed DoT before any
    /// other damage on the local player, so that hit lands on the health vanilla would have had.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    internal static class ApplyDamagePatch
    {
        private static void Prefix(Character __instance, HitData hit, out bool __state)
        {
            __state = State.InDotHit;
            if (__instance == null || __instance != Player.m_localPlayer) return;

            // A hit nested inside the tick's ApplyDamage (another mod's hook or m_onDamaged callback)
            // is not the tick: it lands in full, on settled health.
            if (State.InDotHit)
            {
                State.InDotHit = false;
                if (State.DotOwed > 0f) State.SettleDot((Player)__instance);
                return;
            }

            if (State.DotSource != null)
            {
                if (!Plugin.Enabled.Value || !Plugin.SmoothDot.Value) return;
                State.InDotHit = true;
                State.LastDotHit = hit;
            }
            else if (State.DotOwed > 0f)
            {
                State.SettleDot((Player)__instance);
            }
        }

        private static void Finalizer(bool __state) => State.InDotHit = __state;
    }

    /// <summary>
    /// The health write of a DoT tick (Character.ApplyDamage -> SetHealth, Character.cs:2467): the
    /// loss is banked instead and paid out over the effect's interval in UpdateStatsPatch. If vanilla's
    /// health - ours minus everything still owed - would reach 0 on this tick, the whole debt lands
    /// now instead: death at vanilla's instant, from vanilla's hit, through vanilla's call.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.SetHealth))]
    internal static class SetHealthPatch
    {
        private static bool Prefix(Character __instance, ref float health)
        {
            if (!State.InDotHit || State.PayingDamage) return true;
            if (!(__instance is Player p) || p != Player.m_localPlayer || State.DotSource == null) return true;

            var current = p.GetHealth();
            var loss = current - health;
            if (loss <= 0f) return true;

            var lethal = RegenMath.LethalDotHealth(current, State.DotOwed, loss, p.InGodMode() || p.InGhostMode());
            if (lethal.HasValue)
            {
                State.Dot.Clear();
                State.Payout.TakeDebt();
                health = lethal.Value;
                return true;
            }

            // Same split as the heals: the instant share rides on this write, the rest is banked.
            var fraction = Mathf.Clamp01(Plugin.InstantFraction.Value);
            var smoothed = loss * (1f - fraction);
            State.Dot.Add(smoothed, DotTickPatch.Interval(State.DotSource));

            health = current - (loss - smoothed);
            return health < current;
        }
    }

    /// <summary>
    /// A heal landing while DoT is owed: our health is above vanilla's by the debt, so RPC_Heal's
    /// max-health clamp (Character.cs:2145-2151) would eat healing vanilla still had room for; that
    /// part pays the debt off instead. On the owner-side RPC_Heal, which every heal reaches - our
    /// payouts and local heals through Heal (:2132-2139), other players' heals as the RPC - once.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.RPC_Heal))]
    internal static class RpcHealAbsorbsDotPatch
    {
        private static void Prefix(Character __instance, float hp)
        {
            if (State.DotOwed <= 0f || hp <= 0f || __instance == null || __instance != Player.m_localPlayer) return;
            if (!__instance.m_nview.IsOwner() || __instance.IsDead() || __instance.GetHealth() <= 0f) return;
            State.ReduceOwed(RegenMath.OwedAfterHeal(__instance.GetHealth(), State.DotOwed, hp, __instance.GetMaxHealth()));
        }
    }

    /// <summary>
    /// Character.SetMaxHealth clamps health down to the new max (Character.cs:3057-3067), e.g. when a
    /// food runs out. Our health is above vanilla's by the debt, so the clamp can take hp vanilla
    /// still had - that part comes off the debt.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.SetMaxHealth))]
    internal static class SetMaxHealthPatch
    {
        private static void Prefix(Character __instance, float health)
        {
            if (State.DotOwed <= 0f || __instance == null || __instance != Player.m_localPlayer) return;
            var current = __instance.GetHealth();
            if (current <= health) return;
            State.ReduceOwed(RegenMath.OwedAfterCap(current, State.DotOwed, health));
        }
    }

    /// <summary>
    /// Player.Save writes only the current health (Player.cs:4677-4681), and spawning clears our
    /// state: quitting mid-burn must not forgive the debt.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Save))]
    internal static class SaveSettlesDotPatch
    {
        private static void Prefix(Player __instance)
        {
            if (__instance == Player.m_localPlayer && State.DotOwed > 0f) State.SettleDot(__instance);
        }
    }

    /// <summary>
    /// Attacks that read health: blood magic pays min(health - 1, cost) (Attack.cs:537, GetAttackHealth
    /// :504-512 scales with health), stamina cost can shrink with missing health (:482-485, checked in
    /// Start :356-390), some weapons scale damage with missing health (:1017-1023, at the trigger).
    /// Vanilla's health is ours minus the debt, so settle it at every one of those points.
    /// </summary>
    [HarmonyPatch]
    internal static class HealthAttackSettlesDotPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Attack), nameof(Attack.Start));
            yield return AccessTools.Method(typeof(Attack), nameof(Attack.Update));
            yield return AccessTools.Method(typeof(Attack), nameof(Attack.OnAttackTrigger));
        }

        // Start gets the attacker as its first argument (m_character is set inside it); Update and
        // the trigger read m_character.
        private static void Prefix(Attack __instance, object[] __args)
        {
            if (State.DotOwed <= 0f) return;
            var attacker = __args.Length > 0 && __args[0] is Humanoid h ? h : __instance.m_character;
            if (!(attacker is Player p) || p != Player.m_localPlayer) return;
            if (__instance.m_attackHealth > 0f || __instance.m_attackHealthPercentage > 0f ||
                __instance.m_staminaReturnPerMissingHP > 0f ||
                __instance.m_damageMultiplierPerMissingHP > 0f || __instance.m_damageMultiplierByTotalHealthMissing > 0f)
                State.SettleDot(p);
        }
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
            var dotDraining = Plugin.SmoothDot.Value && State.DotOwed > 0f;
            if (!RegenMath.BarSkipsDelay(__instance.m_firstSet, __instance.m_value, value, dotDraining)) return true;

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
                // Owed damage is paid, never forfeited: dropping it would be a buff.
                State.SettleDot(__instance);
                State.Clear();
                return;
            }
            if (!Plugin.SmoothDot.Value && State.DotOwed > 0f) State.SettleDot(__instance);

            // Dead or downed (another mod pinning health at 0): nothing left to take. Not the owner
            // (no health write possible): the debt waits, it is not drained into nothing.
            if (State.Gone(__instance)) State.DropDot();
            var canWrite = State.CanWrite(__instance);

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

            // Damage-over-time owed, netted against the heals: one rate, regen minus DoT, so the bar
            // moves one way instead of bouncing +1 / -1 when a mead and a burn overlap.
            if (canWrite) chunk -= State.Dot.Take(dt);

            // Whole 1 hp steps, not a sliver per frame: rate R hp/s = R one-hp steps per second.
            // Flush the sub-1 rest once nothing more is owed, so the total still matches vanilla.
            var drained = !meadRunning && State.Food.Pending <= 0f && State.OverTime.Pending <= 0f &&
                          State.UpFront.Pending <= 0f && State.Dot.Pending <= 0f;
            chunk = State.Payout.Pay(chunk, drained);

            if (chunk < 0f)
            {
                if (canWrite) State.DamageHealth(__instance, -chunk);
                else State.Dot.Add(-chunk, 1f); // back to the debt until we own the health again
                return;
            }

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
