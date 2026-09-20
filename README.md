# SmoothRegen

Valheim health regeneration that rises smoothly instead of jumping.

## What it does

Vanilla Valheim heals you from food in one lump every 10 seconds. Your health
bar sits still, then jerks upward, then sits still again.

SmoothRegen catches that lump and pays out the exact same amount continuously
over the following seconds. The health bar climbs instead of stepping.

**Total healing per minute is identical to vanilla.** This is a feel change,
not a buff. If you ever measure a difference in total healing, that is a bug,
please report it.

## Config

Config file: `BepInEx/config/morgott.valheim.smoothregen.cfg`, generated on
first launch.

| Option | Default | Range | Meaning |
| --- | --- | --- | --- |
| `Enabled` | `true` | on / off | Turn smoothing off without removing the DLL. Vanilla behaviour returns immediately. |
| `SmoothingWindow` | `10` | 0.5 - 30 seconds | How long each food tick is spread over. |
| `InstantFraction` | `0.25` | 0.0 - 1.0 | Share of each tick applied immediately; the rest is spread over the window. |

`SmoothingWindow` at the default of 10 seconds matches the vanilla tick period,
so healing becomes an even trickle. Shorter values give a faster catch-up after
each tick with a short pause before the next one. Longer values overlap
consecutive ticks, which is smoother still but makes healing lag slightly
behind when you eat.

### The trade-off, stated honestly

Smoothing costs you time. Spreading a lump out means the health is in your bar
later than vanilla would have put it there, on average by half the smoothing
window. That lag is unavoidable, not a bug: the game does not know the amount
until the tick fires, so there is nothing to pay out early.

`InstantFraction` is the dial for it. At `0.0` the whole tick is smoothed, which
looks best and lags most. At `1.0` you get vanilla back, jump and all. The
default `0.25` gives you a quarter of each tick straight away, which takes the
edge off the delay in a fight while still keeping the bar smooth. Total healing
per minute is identical at every setting.

### Healing at full health is kept, not wasted

Vanilla throws away a food tick that lands while you are already at full health.
SmoothRegen keeps it: the pending amount stays in the buffer and starts paying
out the moment you take damage. This is a small deliberate deviation from
vanilla, and the only one. It is bounded, pending healing never exceeds twice
your max health, so idling at full health for an hour banks nothing extra.

## Requirements

- Valheim 1.0.7 (network version 39)
- BepInEx 5.4.23.5

No other mod is required, and no other mod is referenced.

## Compatibility

Works with **EpicLoot**, **ValheimPlus**, both together, or neither. There is no
hard dependency, no assembly reference and no incompatibility declaration on
any other plugin.

Whatever regeneration bonuses other mods add still apply in full. SmoothRegen
does not compute a regeneration figure of its own, so there is nothing for it to
double-count.

## Design notes

The obvious implementation is a transpiler that lowers the food tick period, so
the game heals in smaller pieces more often. That is the wrong tool here, and it
is what breaks under EpicLoot.

EpicLoot rewrites the IL of `Player.UpdateFood` to inject a flat per-tick
`AddHealthRegen` bonus, matched against an exact opcode pattern. That bonus is
per tick, not per second. Raise the tick rate by a factor of N and the flat
bonus lands N times more often, which is exactly the runaway regeneration
players saw from the old mod. The opcode match is also fragile, so a second
transpiler over the same method is a coin flip on load order.

SmoothRegen leaves the tick rate and the IL alone. The game computes the heal
however it wants, with every modifier from every mod already folded in.
SmoothRegen intercepts the resulting `Character.Heal` call, buffers the amount
and pays it out over the smoothing window. Only the timing changes, never the
number. Compatibility falls out of the architecture rather than being maintained
mod by mod.

## Origin

This is an original implementation written from scratch, not a fork. It replaces
the abandoned Steady Regeneration, from which only the general idea was taken.
No code, configuration names or text were reused.

## License

Original project code and documentation are licensed under CC BY-NC 4.0; see [LICENSE](LICENSE). Copyright (c) 2026 Morgott.

Third-party dependencies retain their own licenses.
