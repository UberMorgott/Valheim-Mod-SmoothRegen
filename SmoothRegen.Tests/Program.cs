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

        // A new tick landing before the old one drained must not strand the remainder.
        private static void OverlappingTicksKeepTotal()
        {
            var buffer = new RegenBuffer();
            var total = 0f;

            buffer.Add(10f, 10f);
            total += DrainSeconds(buffer, seconds: 4f, dt: 1f / 60f);

            buffer.Add(10f, 10f); // second tick arrives early
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

        // Ticks banked while at full health must not grow without bound.
        private static void CapLimitsWhatIsBanked()
        {
            var buffer = new RegenBuffer();
            const float cap = 50f;

            for (var i = 0; i < 20; i++)
                buffer.Add(20f, 10f, cap);

            Near("pending capped", buffer.Pending, cap);
            Near("pays out only the cap", DrainSeconds(buffer, seconds: 30f, dt: 1f / 60f), cap);
        }

        // Character.RPC_Heal drops anything above max health, so regen earned at full health is
        // forfeited, not banked. Holding it back instead lets the buffer swell and dump the whole
        // bank the instant damage opens headroom - a hit that heals straight back off.
        private static void FullHealthDoesNotBankABurst()
        {
            const float dt = 1f / 50f;   // Player.UpdateStats(float) runs from FixedUpdate
            const float tick = 15f;      // one food regen tick
            var buffer = new RegenBuffer();

            // Ten minutes at full health: a tick every 10s, drained every frame into a full bar.
            for (var frame = 1; frame <= 30000; frame++)
            {
                if (frame % 500 == 0) buffer.Add(tick, 10f, cap: 200f);
                buffer.Take(dt);
            }

            if (buffer.Pending > tick)
                Fail($"banked {buffer.Pending} hp at full health, more than one {tick} hp tick");

            // Player finally takes a hit: the first second must not dump a bank.
            var firstSecond = DrainSeconds(buffer, seconds: 1f, dt: dt);
            if (firstSecond > tick / 10f + 0.1f)
                Fail($"burst on damage: {firstSecond} hp in the first second, expected <= {tick / 10f}");
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
