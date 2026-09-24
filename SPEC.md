# SmoothRegen

Valheim BepInEx plugin. Replaces the abandoned `Smoothbrain/SteadyRegeneration`.
Only the idea is carried over; no code is reused.

- Plugin GUID: `morgott.valheim.smoothregen`
- Author: Morgott
- Output: `SmoothRegen.dll`
- Target game: Valheim 1.0.7 (network version 39), BepInEx 5.4.23.5

## Problem

Valheim regenerates health and stamina in large discrete ticks. The result feels
steppy. We want the same total regen per unit of time, delivered smoothly.

## Requirements

1. Smooth the regen tick. Same amount over the same period, applied in small
   increments instead of one jump.
2. Total regen per minute must stay equal to vanilla. This is a feel change,
   not a buff. Any deviation is a bug.
3. Must work correctly with EpicLoot and its regen magic effects.
4. Must work correctly with ValheimPlus and its regen config.
5. Must work with either, both, or neither installed. No hard assembly
   reference to any other plugin, no required dependency.

## Why the old mod broke

Steady Regeneration transpiled `Player.UpdateFood`, cutting the tick period from
10s to 0.1s and scaling each heal by 0.01 - the same total, a hundred times more
often. EpicLoot's `AddHealthRegen` is a FLAT bonus added per tick, so it landed a
hundred times more often too. That, not a double-counted formula, is where the
runaway mega-regen came from. The mod also declared itself incompatible with
ValheimPlus outright, so BepInEx refuses to load it at all:

    Could not load [Steady Regeneration 1.0.3] because it is incompatible with:
    org.bepinex.plugins.valheim_plus

## Design rule that follows

Hook the point where the game has ALREADY folded in every modifier from every
other mod, then redistribute that final number over time. Never recompute the
regen amount ourselves, never read another mod's effects. Compatibility then
falls out of the architecture instead of being maintained mod by mod.

## Design

Hook `Player.UpdateFood`:

- Prefix sets a flag marking that we are inside a food tick for the local player.
- While the flag is set, intercept the single `Character.Heal` call the method
  makes and divert the amount into a buffer instead of applying it.
- Finalizer restores the flag's previous value (Harmony `__state`), so an
  exception inside the method cannot leave it stuck on. Save/restore rather
  than clear-to-false: the prefix only raises the flag for the local player,
  so an unconditional clear would be asymmetric, and a nested `UpdateFood`
  would end the outer tick's window early. Vanilla never nests it.
- A `Player.UpdateStats` postfix drains the buffer, earning
  `buffered * dt / SmoothingWindow` per frame and healing it in whole hp (see below).

Only the timing changes. The amount is whatever the game produced, so every
other mod's contribution is preserved verbatim.

### Health-over-time status effects (healing meads)

The food tick is not the only steppy heal. `SE_Stats` pays `m_healthOverTime` out in
`m_healthOverTimeDuration / m_healthOverTimeInterval` lumps, the interval defaulting
to **5 s** (`SE_Stats.cs:162-170, 235-243`), so a mead that restores 50 hp over 10 s
lands as two 25 hp jumps. The same method also applies one `m_healthPerTick` lump per
`m_tickInterval` (`SE_Stats.cs:211-222`).

Same design rule, same hook shape: a prefix/finalizer on `SE_Stats.UpdateStatusEffect`
records the effect being updated for the local player, and the `Character.Heal` prefix
diverts whatever it heals into a second buffer. The window is the effect's **own**
interval - the gap to its next lump - so each lump finishes paying out exactly as the
next one arrives, and the effect's total and duration are untouched.

That second buffer is constructed with `boundToOneTick: false`:

- **No window clamp.** The 10 s clamp exists because the food buffer merges ticks that
  arrive on an undrained pool. Here the window *is* the gap between lumps, so nothing
  ever piles up, and an effect with a 30 s interval should be spread over 30 s.
- **No one-tick ceiling.** Two healing meads ticking in the same frame would forfeit
  the smaller lump under `max(incoming, held)`. The ceiling only ever existed to bound
  what a *held* payout can discharge as, and nothing is held here.
- **No hold at full health.** `UpdateStatsPatch` skips the food buffer when there is no
  headroom, because vanilla's tick would have collected damage taken up to the tick
  instant. A status effect has no such instant to defend: vanilla pays each lump on its
  own schedule and lets `RPC_Heal` clamp the excess away. Holding one back would hand
  the player healing vanilla never gave.

Tests: `UnboundBufferKeepsConcurrentEffectsWhole`,
`UnboundBufferTakesAWindowLongerThanATick`.

**Superseded for `m_healthOverTime`:** buffering its lumps still waited one interval (5 s)
for the first lump. `UpdateStatusEffectPatch` now pays `m_healthOverTime /
m_healthOverTimeDuration` per game second from the drink (`RegenMath.OverTimeShare`, cut off
at the duration) and restarts `m_healthOverTimeTimer` every step so vanilla's lump never
fires. Only `m_healthPerTick` lumps still go through the buffer above.
Test: `MeadLumpsLandAsWholeHpStepsAndTotalVanilla`.

**World join:** a fresh Player's `m_foodRegenTimer` is 0, so the first food tick - and with
it the first smoothed hp - came 10 s after spawn. `OnSpawnedPatch` sets it to 10 so the tick
fires on the first `UpdateFood`; smoothing then leads vanilla by one period instead of
lagging it. Test: `FoodPaysFromTheFirstFrameAfterSpawn`.

`m_healthUpFront` is applied from `StartupEffects` (`SE_Stats.cs:182-187`), called from
`Setup` and `ResetTime`, outside `UpdateStatusEffect`. It is meant to be instant (Epic
Loot's instant mead moves a mead's whole over-time heal into it), so a prefix/finalizer on
`StartupEffects` diverts it into a third unbound buffer spread over a fixed 1 s: no jump,
at most one second of lag.

### HP bar

The HUD HP bars are `GuiBar`s: `SetValue` restarts `m_changeDelay` on every rise of a
smooth-fill bar and `LateUpdate` moves the bar only once it runs out (`GuiBar.cs:73-76,
96-115`; vanilla fill bar: `m_smoothFill 1`, `m_changeDelay 0.5`). Vanilla heals every 10 s
so the delay expires; +1 hp steps arrive faster, so the bar froze while healing and jumped
when healing stopped (the number, set directly by `Hud.UpdateHealth`, was fine).
`HealthBarFillPatch` makes a rise on the two HP bars update the target without restarting
the delay. Test: `HealthBarFollowsSmallHeals` (replica of GuiBar's logic).

### Whole +1 hp steps

Every buffer's per-frame share goes through `WholeHpPayout` before `Heal`: fractions are
carried and only whole hp are healed, so a rate of R hp/s lands as R one-hp heals per
second. Shares are earned per game second (`dt`), so tick i of A hp over T s is due at
`i*T/A` regardless of step rate; every tick due by a step is paid in that step (100 hp/s at
the 50 Hz `FixedUpdate` = 2 hp per step). When nothing more is owed the sub-1 rest is
flushed, so totals still match. Tests: `TickCountFollowsGameTimeNotStepRate`,
`PayoutFlushesTheFractionalRest`.

### The lag, and InstantFraction

Smoothing is not free. A continuous payout of the same total always trails
vanilla's instant step, on average by half the window. It cannot be removed:
the amount is not known before the tick computes it, and forecasting from the
previous tick yields an identical distribution. So it is managed, not solved.

`InstantFraction` (0.0-1.0, default 0.0 - fully smooth) is the knob. The prefix takes
`ref float hp`, buffers `hp * (1 - InstantFraction)` and lowers `hp` by that
same amount, letting the original call through for the remainder. Subtraction
rather than a second multiply, so instant + smoothed equals the original
exactly. It stays ONE heal - the prefix skips the original only when the
instant share is zero. 0.0 is fully smooth with maximum lag, 1.0 is vanilla.

### Holding the buffer at full health, and the cap that bounds it

`Character.RPC_Heal` clamps to `GetMaxHealth()` and silently discards the
excess - but only **at the instant the tick fires**. Vanilla damage taken at
t=9.9 still collects the whole tick at t=10. `UpdateStatsPatch` therefore
returns *before* `Take` when there is no headroom, holding the payout instead
of draining it into a full bar. Draining it was a real nerf: every full-health
frame forfeited its own 1/500th of a tick, so topping off before a fight healed
strictly less than playing with no mod installed. (Skipping `Heal` is also right
on its own terms - nothing would land, and `Heal` is an RPC when we are not the
owner.)

Holding is only safe because it is bounded. The original `Take(dt, headroom)`
design let the buffer accumulate to `2 * GetMaxHealth()` and, once damage opened
headroom, discharge at `pending / SmoothingWindow` - ~20 hp/s, a hit that heals
straight back off. That is the opposite of the mod's purpose. `Take` still takes
no limit, so that cannot come back by accident; instead `RegenBuffer.Add` caps
pending at **one tick** - specifically `max(incoming, already held)`, because
food burns down and a later tick can be worth less than the one still owed;
clamping to the incoming amount alone forfeited the difference and healed less
than vanilla. Either way the worst case burst is what vanilla itself would
have handed over at a single tick, and `pending / SmoothingWindow` never exceeds
vanilla's own average rate of one tick per 10s. The payout rate is bounded by
construction.

### Why the window is clamped to the 10s tick period

`SmoothingWindow` accepts 0.5-10 and `RegenBuffer.Add` clamps anything above
`VanillaTickPeriod` (10) down to it. The buffer is a single merged
`_pending`/`_rate` pair, so a window longer than the tick period means the next
tick lands on an undrained one, `_rate` is recomputed over the merged pool, and
the previous tick's remainder is re-stretched. At 20 hp per tick and a 30s
window that settles into a permanent ~40 hp backlog with pending pinned near
`amount * window / 10`, and the promise on the tin - "each tick is paid out over
`SmoothingWindow` seconds" - stops holding: measured, tick 10 was still 40 hp
short after 100s.

Clamping instead of tracking per-tick portions is deliberate. A window longer
than the tick period buys nothing: at exactly 10s the payout is already
continuous, one tick handed over precisely as the next arrives. Anything longer
only adds latency and a backlog. Below 10s the knob still does something real -
pay the tick out faster, then idle until the next one - so the range keeps its
lower half. Regression test: `LongWindowStillPaysEachTickWithinTheTickPeriod`.

The residual difference from vanilla is one tick held in flight, delivered late
rather than forfeited - which is the smoothing lag the mod exists to trade for,
not a buff.

### Switching the mod off at runtime

`Enabled` is read every frame. On the disabled path `UpdateStatsPatch` clears
the buffer instead of merely returning: otherwise the pending amount *and* its
rate survive untouched, and switching the mod back on minutes later resumes
paying out a heal earned before it was turned off. The cost is forfeiting at
most one tick, once, at a moment the player deliberately asked the mod to stop
— the alternative, healing on after being switched off, is worse.

That cost includes a tick banked in the very same frame. `UpdateFood` runs from
inside `UpdateStats(dt)`, so the `Heal` prefix banks before our `UpdateStats`
postfix reads `Enabled` again; if the config flips between those two reads (only
possible from BepInEx's config-file watcher thread, since nothing else runs
mid-call) the freshly banked tick is cleared unpaid. Accepted, not fixed:
tracking it would need extra state for a race whose entire cost is the one tick
this section already forfeits by design, and once the mod is off that tick
should not be paid out anyway.

### Clearing the buffer on spawn

The buffer is `static` and lives on the plugin, which survives the trip to the
main menu; `Plugin.OnDestroy` runs only on plugin unload. Without a clear, up to
one banked tick earned by one character is paid out to whatever character is
loaded next. A `Player.OnSpawned` postfix clears it. `Game.SpawnPlayer` is the
only code path that constructs the local player - both world entry and respawn
after death reach it through `Game.UpdateRespawn` - and it calls `OnSpawned`
*after* `SetLocalPlayer`, so the `m_localPlayer` guard holds. The `OnDeath`
clear stays: the corpse remains the local player until `_RequestRespawn`
destroys it seconds later, and it has headroom to be paid into.

### Confirmed facts

- Vanilla food tick period is 10s, gated on `m_foodRegenTimer >= 10f`.
- `SEMan.ModifyHealthRegen(ref float)` is a MULTIPLIER, starting at 1.0.
- EpicLoot postfixes `SEMan.ModifyHealthRegen` AND transpiles
  `Player.UpdateFood`, injecting a flat per-tick `AddHealthRegen` bonus matched
  on an exact opcode pattern. Changing the tick rate multiplies that flat bonus,
  which is the runaway-regen bug. We do not touch the tick rate or the IL.
- ValheimPlus does not patch health regen at all. Its only related patch is on
  `Player.Awake`, for stamina fields.

### Why not just make the tick faster

The obvious alternative is to raise the tick rate so the game itself heals more
often. It does not work, for two independent reasons:

- There is no variable to change. The period is the literal `10f` inside
  `Player.UpdateFood`; reaching it means rewriting IL.
- EpicLoot's `AddHealthRegen` is a flat amount added PER TICK. N times more
  ticks means N times the bonus. The quantity is bound to the tick event, not
  to elapsed time, so no scaling factor fixes it. This is exactly the runaway
  regen the old mod produced.

Intercepting the final heal avoids both: every contributor has already been
folded into that one number, whatever it came from.

### Conflict audit (2026-09-10, against the user's 22 installed plugins)

- **Nothing else patches `Character.Heal`.** Our skipping prefix is exclusive,
  so no `[HarmonyPriority]` is needed.
- `Player.UpdateFood` is patched only by EpicLoot's transpiler. We add a
  prefix/finalizer that touch a flag and no IL, so the two compose.
- AugaLite patches `Hud.UpdateFood` - same method name, different type, not a
  conflict.
- No plugin writes `Character.m_health` directly or heals the player outside
  `Character.Heal`. Almanac's custom effects heal through `Character.Heal` from
  `SE_Update`; since the `SE_Stats.UpdateStatusEffect` hook was added they are
  smoothed too, which is the same feel change for the same total.
- EquipmentAndQuickSlots postfixes `Player.OnDeath` at priority 0, running
  after our buffer clear. The two are independent, order does not matter.

### Known limitation: the in-tick flag is scoped to the method, not to the call

`State.InFoodTick` is set for the whole of `Player.UpdateFood`, and
`State.OverTimeSource` for the whole of `SE_Stats.UpdateStatusEffect`, so the `Heal`
prefix captures *any* heal on the local player that happens inside those calls -
not only vanilla's own. Vanilla's `UpdateFood` contains exactly one `Heal`
(confirmed in the 1.0.7 decompile), so today nothing else is caught. The
exposure is another mod healing the local player from inside the same call:
from its own `UpdateFood` prefix/postfix, or from a postfix on something
`UpdateFood` invokes (`SetMaxHealth`, `SEMan.ModifyHealthRegen`, `Message`,
`ShowTutorial`). Such a heal would be smoothed rather than instant. It would
still be delivered in full - the buffer never changes the total - so the worst
case is a heal arriving late, not one going missing.

This is left unfixed on purpose. Narrowing the window to vanilla's single `Heal`
would mean matching the call site, i.e. a transpiler on `UpdateFood`, which is
exactly what this design refuses to do (EpicLoot transpiles the same method).
Distinguishing the caller from inside the prefix would need a stack walk every
tick. Both are worse than the limitation. No plugin in the 2026-09-10 conflict
audit heals from inside `UpdateFood`; recheck this if that changes.

## Verification

A test that fails if smoothing changes the total: sum the health granted over a
simulated regen window and assert it equals the vanilla total within a small
epsilon. Run with EpicLoot-style bonus applied and without it.
