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
- Finalizer clears the flag, so an exception inside the method cannot leave it
  stuck on.
- A `Player.UpdateStats` postfix drains the buffer, paying out
  `buffered * dt / SmoothingWindow` per frame and healing that amount directly.

Only the timing changes. The amount is whatever the game produced, so every
other mod's contribution is preserved verbatim.

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
pending at **one window's worth of vanilla regen**,
`amount * max(1, SmoothingWindow / 10)`:

- At the default 10s window the ceiling is exactly one tick, so the worst case
  burst is what vanilla itself would have handed over at a single tick.
- A longer window legitimately keeps several ticks in flight, and the formula
  scales with it rather than clipping healing the player is owed.
- Either way `pending / SmoothingWindow` never exceeds vanilla's own average
  rate of one tick per 10s. The payout rate is bounded by construction.

The residual difference from vanilla is one tick held in flight, delivered late
rather than forfeited - which is the smoothing lag the mod exists to trade for,
not a buff.

### Switching the mod off at runtime

`Enabled` is read every frame. On the disabled path `UpdateStatsPatch` clears
the buffer instead of merely returning: otherwise the pending amount *and* its
rate survive untouched, and switching the mod back on minutes later resumes
paying out a heal earned before it was turned off. The cost is forfeiting at
most one tick, once, at a moment the player deliberately asked the mod to stop
- the alternative, healing on after being switched off, is worse.

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
  `Character.Heal`. Almanac's custom effects heal through `Character.Heal` but
  from `SE_Update`, outside our flag window, so they are untouched.
- EquipmentAndQuickSlots postfixes `Player.OnDeath` at priority 0, running
  after our buffer clear. The two are independent, order does not matter.

## Verification

A test that fails if smoothing changes the total: sum the health granted over a
simulated regen window and assert it equals the vanilla total within a small
epsilon. Run with EpicLoot-style bonus applied and without it.
