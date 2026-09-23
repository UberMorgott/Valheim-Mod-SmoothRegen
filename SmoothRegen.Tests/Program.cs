using System;

namespace SmoothRegen.Tests
{
    /// <summary>
    /// The whole point of SmoothRegen is that it changes WHEN healing arrives,
    /// never HOW MUCH. Every check here fails if that stops being true.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            PaysOutExactlyWhatWentIn();
            PaysOutOverTheWindowNotInstantly();
            NeverOverdraws();
            OverlappingTicksKeepTotal();
            IgnoresNonPositiveInput();
            ClearDropsPending();
            SplitTickStillTotalsTheOriginal();
            CapLimitsWhatIsBanked();
            FullHealthDoesNotBankABurst();
            DisableThenReenablePaysNothingStale();
            FullHealthThenDamageStillPaysTheWholeTick();
            LongWindowStillPaysEachTickWithinTheTickPeriod();
            ASmallerTickDoesNotTruncateWhatIsAlreadyHeld();
            UnboundBufferKeepsConcurrentEffectsWhole();
            UnboundBufferTakesAWindowLongerThanATick();
            RandomChurnKeepsEveryInvariant();

            if (_failures == 0)
            {
                Console.WriteLine("all checks passed");
                return 0;
            }

            Console.WriteLine($"{_failures} check(s) FAILED");
            return 1;
        }

        // A single 20 hp tick, drained frame by frame, must total 20 hp.
        private static void PaysOutExactlyWhatWentIn()
        {
            var buffer = new RegenBuffer();
            buffer.Add(20f, 10f);

            var total = DrainSeconds(buffer, seconds: 15f, dt: 1f / 60f);

            Near("total paid out", total, 20f);
            Near("nothing left pending", buffer.Pending, 0f);
        }

        // Half the window elapsed should pay out roughly half, not everything.
        private static void PaysOutOverTheWindowNotInstantly()
        {
            var buffer = new RegenBuffer();
            buffer.Add(10f, 10f);

            var half = DrainSeconds(buffer, seconds: 5f, dt: 1f / 60f);

            if (half < 4.5f || half > 5.5f)
                Fail($"expected about half of 10 after 5s of a 10s window, got {half}");
        }

        // A frame longer than the whole window must not hand out more than is held.
        private static void NeverOverdraws()
        {
            var buffer = new RegenBuffer();
            buffer.Add(7f, 10f);

            var chunk = buffer.Take(60f);

            Near("clamped to pending", chunk, 7f);
            Near("emptied", buffer.Pending, 0f);
            Near("nothing more to give", buffer.Take(1f), 0f);
        }

        // A window is clamped to the 10s tick period, so a tick can only land on an undrained one
        // when payouts were held back. The remainder must not be stranded, and the total must survive.
        private static void OverlappingTicksKeepTotal()
        {
            var buffer = new RegenBuffer();
            var total = 0f;

            buffer.Add(10f, 20f);
            total += DrainSeconds(buffer, seconds: 10f, dt: 1f / 60f);

            buffer.Add(10f, 20f); // second tick arrives while the first is still paying out
            total += DrainSeconds(buffer, seconds: 30f, dt: 1f / 60f);

            Near("both ticks fully paid", total, 20f);
            Near("drained", buffer.Pending, 0f);
        }

        private static void IgnoresNonPositiveInput()
        {
            var buffer = new RegenBuffer();
            buffer.Add(0f, 10f);
            buffer.Add(-5f, 10f);

            Near("nothing queued", buffer.Pending, 0f);
            Near("zero dt pays nothing", buffer.Take(0f), 0f);

            try
            {
                buffer.Add(1f, 0f);
                Fail("expected a zero window to be rejected");
            }
            catch (ArgumentOutOfRangeException)
            {
                // expected
            }
        }

        private static void ClearDropsPending()
        {
            var buffer = new RegenBuffer();
            buffer.Add(50f, 10f);
            buffer.Clear();

            Near("cleared", buffer.Pending, 0f);
            Near("pays nothing after clear", buffer.Take(1f), 0f);
        }

        // InstantFraction splits the lump. Whatever the split, the player must receive
        // exactly the amount the game produced - this mirrors HealPatch's arithmetic.
        private static void SplitTickStillTotalsTheOriginal()
        {
            foreach (var fraction in new[] { 0f, 0.25f, 1f })
            {
                const float hp = 20f;
                var smoothed = hp * (1f - fraction);
                var instant = hp - smoothed;

                var buffer = new RegenBuffer();
                buffer.Add(smoothed, 10f);
                var paid = instant + DrainSeconds(buffer, seconds: 15f, dt: 1f / 60f);

                Near($"fraction {fraction}: total delivered", paid, hp);
                Near($"fraction {fraction}: drained", buffer.Pending, 0f);
            }
        }

        // Ticks banked while at full health must not grow without bound. The ceiling is one vanilla
        // tick, whatever the configured window: the window is clamped to the 10s tick period, so
        // pending / window never exceeds vanilla's own average rate.
        private static void CapLimitsWhatIsBanked()
        {
            var short10 = new RegenBuffer();
            for (var i = 0; i < 20; i++) short10.Add(20f, 10f);

            Near("capped at one tick for a 10s window", short10.Pending, 20f);
            Near("pays out only the cap", DrainSeconds(short10, seconds: 30f, dt: 1f / 60f), 20f);

            var long30 = new RegenBuffer();
            for (var i = 0; i < 20; i++) long30.Add(20f, 30f);

            Near("still one tick for a 30s window", long30.Pending, 20f);
            Near("still vanilla's rate", DrainSeconds(long30, seconds: 10f, dt: 1f / 60f), 20f);
        }

        // Regression guard for b2f8500: however long the player idles, the buffer must never
        // swell into a bank that dumps the instant damage opens headroom - a hit that heals
        // straight back off. Ten minutes of ticks may leave at most one tick pending.
        private static void FullHealthDoesNotBankABurst()
        {
            const float dt = 1f / 50f;   // Player.UpdateStats(float) runs from FixedUpdate
            const float tick = 15f;      // one food regen tick
            var buffer = new RegenBuffer();

            // Ten minutes at full health: a tick every 10s, drained every frame into a full bar.
            for (var frame = 1; frame <= 30000; frame++)
            {
                if (frame % 500 == 0) buffer.Add(tick, 10f);
                buffer.Take(dt);
            }

            if (buffer.Pending > tick)
                Fail($"banked {buffer.Pending} hp at full health, more than one {tick} hp tick");

            // Player finally takes a hit: the first second must not dump a bank.
            var firstSecond = DrainSeconds(buffer, seconds: 1f, dt: dt);
            if (firstSecond > tick / 10f + 0.1f)
                Fail($"burst on damage: {firstSecond} hp in the first second, expected <= {tick / 10f}");
        }

        // Switching the mod off mid-window strands whatever is still owed, and the amount AND the
        // rate survive: switching it back on later resumed paying a heal earned minutes ago.
        // UpdateStatsPatch now clears the buffer on its disabled early return.
        private static void DisableThenReenablePaysNothingStale()
        {
            const float dt = 1f / 50f;
            var buffer = new RegenBuffer();

            buffer.Add(20f, 10f);
            DrainSeconds(buffer, seconds: 2f, dt: dt);   // 2s in, ~16 hp still owed

            buffer.Clear();                              // the mod is switched off

            Near("nothing stranded on disable", buffer.Pending, 0f);
            Near("re-enabled, nothing stale to pay", DrainSeconds(buffer, seconds: 30f, dt: dt), 0f);

            // The next real tick pays its own amount at its own rate, not the abandoned one.
            buffer.Add(5f, 10f);
            Near("fresh tick pays only itself", DrainSeconds(buffer, seconds: 15f, dt: dt), 5f);
        }

        // Vanilla clamps at the tick instant only: damage taken at t=9.9 still collects the whole
        // tick at t=10. Draining into a full bar forfeited every full-health frame instead, so
        // topping off before a fight healed strictly less than playing with no mod at all.
        // UpdateStatsPatch now returns before Take when there is no headroom; the cap in Add is
        // what keeps that hold from becoming a burst.
        private static void FullHealthThenDamageStillPaysTheWholeTick()
        {
            const float dt = 1f / 50f;
            const float tick = 15f;
            const float window = 10f;
            var buffer = new RegenBuffer();

            // A minute at full health. Mirrors the patch: no headroom, so Take is never called.
            for (var frame = 1; frame <= 3000; frame++)
                if (frame % 500 == 0) buffer.Add(tick, window);

            if (buffer.Pending > tick)
                Fail($"held {buffer.Pending} hp at full health, more than one {tick} hp tick");

            // Damage opens headroom. The first second must not dump a bank...
            var firstSecond = DrainSeconds(buffer, seconds: 1f, dt: dt);
            if (firstSecond > tick / 10f + 0.1f)
                Fail($"burst on damage: {firstSecond} hp in the first second, expected <= {tick / 10f}");

            // ...and the tick the player was owed must arrive in full, exactly like vanilla.
            var total = firstSecond + DrainSeconds(buffer, seconds: window, dt: dt);
            Near("whole tick delivered after topping off", total, tick);
        }

        // A configured window longer than vanilla's 10s tick period used to merge every tick into
        // one pool and re-stretch the leftovers, banking ~amount * window / 10 hp and delaying each
        // tick far past the window it was promised in. Add now clamps the window to the tick period,
        // so ticks never overlap: at most one tick is ever in flight and each one is fully paid
        // before the next arrives.
        private static void LongWindowStillPaysEachTickWithinTheTickPeriod()
        {
            const float dt = 1f / 50f;
            const float tick = 20f;
            const float window = 30f;   // user-raised, three times the vanilla tick period
            var buffer = new RegenBuffer();
            var paid = 0f;

            for (var n = 1; n <= 10; n++)
            {
                buffer.Add(tick, window);

                if (buffer.Pending > tick + 0.01f)
                    Fail($"tick {n}: banked {buffer.Pending} hp with a {window}s window, " +
                         $"expected at most one {tick} hp tick in flight");

                paid += DrainSeconds(buffer, seconds: RegenBuffer.VanillaTickPeriod, dt: dt);

                // Deadline: tick n must be fully delivered before tick n+1 arrives.
                if (paid < n * tick - 0.01f)
                    Fail($"tick {n}: only {paid} hp delivered after {n * RegenBuffer.VanillaTickPeriod}s, " +
                         $"expected {n * tick} - a tick missed its payout deadline");
            }

            Near("ten ticks, nothing lost", paid, 10f * tick, 0.01f);
            Near("nothing left in flight", buffer.Pending, 0f);
        }

        // The ceiling is "one vanilla tick", not "the last tick". Food burns down, so the next tick
        // can be smaller than the one still held at full health; clamping to the incoming amount
        // threw the difference away and healed less than vanilla would have.
        private static void ASmallerTickDoesNotTruncateWhatIsAlreadyHeld()
        {
            var buffer = new RegenBuffer();

            buffer.Add(20f, 10f);   // full stomach, held because the bar is full
            buffer.Add(5f, 10f);    // food has burned down: the next tick is worth far less

            Near("the held tick survives a smaller one", buffer.Pending, 20f);
            Near("and it is all paid out", DrainSeconds(buffer, seconds: 15f, dt: 1f / 60f), 20f);
        }

        // Real play churns the inputs: gear swaps, buffs dropping, the puke effect stripping food,
        // max health moving, EpicLoot affixes coming and going. Every one of those changes the tick
        // amount, the window, or wipes the buffer, and they can land between two vanilla ticks.
        // So: a seeded pseudo-random event sequence, and after EVERY step the four invariants -
        //   1. pending never exceeds one vanilla tick's worth (the Max(amount, pending) ceiling),
        //   2. a payout never exceeds the rate frozen at the last Add (no burst),
        //   3. everything paid was added, and nothing added vanishes beyond the documented ceiling,
        //   4. a tick's amount is frozen at insertion - later events do not change money in flight.
        // Own LCG, not System.Random, so the sequence is identical on any runtime: a failure prints
        // the seed and step index and replays exactly.
        // Status-effect heals share one buffer, so two healing meads ticking in the same frame must
        // both be paid in full: the one-tick ceiling that guards the food buffer would forfeit the
        // smaller of the two.
        private static void UnboundBufferKeepsConcurrentEffectsWhole()
        {
            var buffer = new RegenBuffer(boundToOneTick: false);
            buffer.Add(25f, 5f);
            buffer.Add(10f, 5f);

            Near("both lumps held", buffer.Pending, 35f);
            Near("both lumps paid", DrainSeconds(buffer, seconds: 10f, dt: 1f / 60f), 35f);
        }

        // An effect whose interval is longer than the food tick period is paid out over that
        // interval, not squeezed into 10s: the clamp belongs to the food buffer alone.
        private static void UnboundBufferTakesAWindowLongerThanATick()
        {
            var buffer = new RegenBuffer(boundToOneTick: false);
            buffer.Add(30f, 30f);

            var afterTenSeconds = DrainSeconds(buffer, seconds: 10f, dt: 1f / 60f);

            Near("a third paid after a third of the window", afterTenSeconds, 10f, 0.05f);
            Near("the rest paid by the end", DrainSeconds(buffer, seconds: 20f, dt: 1f / 60f), 20f, 0.05f);
        }

        private static void RandomChurnKeepsEveryInvariant()
        {
            const uint seed = 20260910u;
            const int steps = 4000;
            const float dt = 0.02f;      // FixedUpdate
            var rng = seed;
            uint Next(uint bound) { rng = rng * 1664525u + 1013904223u; return (rng >> 8) % bound; }

            var buffer = new RegenBuffer();
            double added = 0, paid = 0, forfeited = 0;
            double rate = 0;             // what Add froze; mirrors _pending / clamped window

            for (var step = 0; step < steps; step++)
            {
                var before = buffer.Pending;
                var roll = Next(100);

                if (roll < 20)
                {
                    // Add: amounts from zero/negative up to a huge tick, windows from a hair to
                    // three times the tick period (clamped), including exactly 10s.
                    var amount = new[] { 0f, -5f, 0.001f, 1f, 7.5f, 15f, 20f, 250f }[Next(8)];
                    var window = new[] { 10f, 10f, 10f, 0.5f, 3f, 9.99f, 30f }[Next(7)];
                    buffer.Add(amount, window);

                    if (amount <= 0f)
                    {
                        Churn(step, seed, "a non-positive Add changed the buffer",
                            buffer.Pending, before);
                        continue;
                    }

                    added += amount;
                    var ceiling = Math.Max(amount, before);
                    if (buffer.Pending > ceiling + 0.001f)
                        Churn(step, seed, $"pending {buffer.Pending} broke the ceiling {ceiling} " +
                                          $"(add {amount} onto {before})");
                    // Nothing truncated except by that ceiling.
                    Churn(step, seed, $"add {amount} onto {before} lost hp",
                        buffer.Pending, Math.Min(before + amount, ceiling));
                    forfeited += Math.Max(0f, before + amount - buffer.Pending);

                    var clamped = Math.Min(window, RegenBuffer.VanillaTickPeriod);
                    rate = buffer.Pending / clamped;
                    // No burst: the rate for the amount in flight never beats vanilla's own average
                    // for a full tick paid over the tick period, given the user's window.
                    if (rate > ceiling / clamped + 0.001f)
                        Churn(step, seed, $"rate {rate} exceeds vanilla's {ceiling / clamped}");
                }
                else if (roll < 95)
                {
                    // Take, including zero/negative dt and frames far longer than the window.
                    var frame = roll < 90 ? dt : new[] { 0f, -1f, 0.5f, 60f }[Next(4)];
                    var chunk = buffer.Take(frame);

                    if (chunk < 0f)
                        Churn(step, seed, $"paid a negative {chunk}");
                    if (chunk > before + 0.001f)
                        Churn(step, seed, $"paid {chunk} out of {before} held");
                    if (frame > 0f && chunk > rate * frame + 0.001f)
                        Churn(step, seed, $"burst: paid {chunk} in {frame}s, frozen rate allows " +
                                          $"{rate * frame}");
                    // Money in flight only moves by what was paid - nothing rewrites it.
                    Churn(step, seed, "pending drifted from before - paid",
                        buffer.Pending, before - chunk);
                    paid += chunk;
                    if (buffer.Pending <= 0f) rate = 0;
                }
                else
                {
                    buffer.Clear();
                    forfeited += before;
                    rate = 0;
                    Churn(step, seed, "Clear left something behind", buffer.Pending, 0f);
                }

                // The books balance at every single step.
                var drift = added - paid - forfeited - buffer.Pending;
                if (Math.Abs(drift) > 0.001 * Math.Max(1.0, added))
                    Churn(step, seed, $"conservation drift {drift}: added {added}, paid {paid}, " +
                                      $"forfeited {forfeited}, pending {buffer.Pending}");
            }

            if (paid <= 0 || added <= 0)
                Fail($"churn seed {seed}: the sequence did nothing (added {added}, paid {paid})");
        }

        // One-line failure with everything needed to replay: seed, step, and the offending values.
        private static void Churn(int step, uint seed, string what) =>
            Fail($"churn seed {seed} step {step}: {what}");

        private static void Churn(int step, uint seed, string what, float actual, float expected)
        {
            if (Math.Abs(actual - expected) > 0.001f)
                Churn(step, seed, $"{what} (got {actual}, expected {expected})");
        }

        private static float DrainSeconds(RegenBuffer buffer, float seconds, float dt)
        {
            var total = 0f;
            for (var elapsed = 0f; elapsed < seconds; elapsed += dt)
                total += buffer.Take(dt);
            return total;
        }

        private static void Near(string what, float actual, float expected, float epsilon = 0.001f)
        {
            if (Math.Abs(actual - expected) > epsilon)
                Fail($"{what}: expected {expected}, got {actual}");
        }

        private static void Fail(string message)
        {
            Console.WriteLine("FAIL: " + message);
            _failures++;
        }
    }
}
