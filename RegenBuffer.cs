using System;

namespace SmoothRegen
{
    /// <summary>
    /// Holds a lump of healing and pays it out gradually.
    /// Pure arithmetic, no Unity types, so it can be unit tested on its own.
    /// The invariant that matters: everything put in comes out, no more, no less - up to the
    /// one ceiling, a single vanilla tick, which bounds what a payout held back for want of
    /// headroom can discharge as.
    /// </summary>
    public sealed class RegenBuffer
    {
        /// <summary>Vanilla fires the food heal tick every 10 seconds (Player.UpdateFood).</summary>
        public const float VanillaTickPeriod = 10f;

        private readonly bool _boundToOneTick;

        private float _pending;
        private float _rate;

        public float Pending => _pending;

        /// <param name="boundToOneTick">
        /// True for the food tick: the window is clamped to the tick period and pending is capped at
        /// one tick, which is what makes it safe to hold a payout back at full health. False for
        /// heals whose window is the effect's own interval - the gap to its next heal - so ticks
        /// never pile up, and which are paid out unconditionally, exactly as vanilla does.
        /// </param>
        public RegenBuffer(bool boundToOneTick = true)
        {
            _boundToOneTick = boundToOneTick;
        }

        /// <summary>
        /// Queue a tick's worth of healing, to be paid out over <paramref name="window"/> seconds.
        /// </summary>
        public void Add(float amount, float window)
        {
            if (amount <= 0f) return;
            if (window <= 0f) throw new ArgumentOutOfRangeException(nameof(window));

            if (_boundToOneTick)
            {
                // A window longer than the tick period cannot work: the next tick lands before this one
                // is drained, every tick is merged into one pool and re-stretched, and the buffer settles
                // into a permanent backlog instead of paying each tick out over its window. Clamping here
                // rather than at the caller keeps the invariant with the arithmetic that relies on it.
                if (window > VanillaTickPeriod) window = VanillaTickPeriod;

                // Ceiling is one tick: pending/window then never exceeds vanilla's own average rate,
                // so holding a payout back (no headroom to heal into) can never discharge as a burst -
                // the worst case is one vanilla tick, spread out. It is the LARGER of the incoming tick
                // and what is already held: food burns down, so a later tick can be worth less than the
                // one still owed, and clamping to the incoming amount would forfeit the difference.
                var ceiling = Math.Max(amount, _pending);

                _pending += amount;
                if (_pending > ceiling) _pending = ceiling;
            }
            else
            {
                // No ceiling: nothing is ever held back here, so there is no burst to bound, and a cap
                // would forfeit healing whenever two effects tick at once.
                _pending += amount;
            }

            // Re-spread whatever is left, so a tick arriving early does not strand a remainder.
            _rate = _pending / window;
        }

        /// <summary>
        /// Take the share earned by <paramref name="dt"/> seconds. Never overdraws.
        /// </summary>
        public float Take(float dt)
        {
            if (_pending <= 0f || dt <= 0f) return 0f;

            var chunk = _rate * dt;
            if (float.IsNaN(chunk) || chunk >= _pending) chunk = _pending;

            _pending -= chunk;
            if (_pending <= 0f)
            {
                _pending = 0f;
                _rate = 0f;
            }
            return chunk;
        }

        public void Clear()
        {
            _pending = 0f;
            _rate = 0f;
        }
    }

    public static class RegenMath
    {
        /// <summary>
        /// GuiBar.SetValue restarts m_changeDelay on every rise of a smooth-fill bar and the bar
        /// only moves once that delay runs out (GuiBar.cs:73-76, 96-115). A +1 hp heal every
        /// fraction of a second keeps restarting it, so the HP bar froze until healing stopped.
        /// True = this SetValue is a rise that must not restart the delay (value stored directly).
        /// </summary>
        public static bool FillSkipsDelay(bool firstSet, float current, float value) =>
            !firstSet && value > current;

        /// <summary>
        /// Health a <paramref name="total"/>-over-<paramref name="duration"/> effect earns in the
        /// step from <paramref name="elapsed"/> to elapsed + <paramref name="dt"/> game seconds:
        /// its constant rate, cut off at the end of the duration, so the shares sum to the total.
        /// </summary>
        public static float OverTimeShare(float total, float duration, float elapsed, float dt)
        {
            if (total <= 0f || duration <= 0f || dt <= 0f) return 0f;
            var span = Math.Min(dt, duration - elapsed);
            return span > 0f ? total * span / duration : 0f;
        }
    }

    /// <summary>
    /// Turns the fractional hp the buffers earn each frame into whole +1 hp steps, so a rate of
    /// R hp/s lands as R one-hp heals per second. What is earned but not yet a whole hp is carried,
    /// and flushed once nothing more is coming, so the total still equals what went in.
    /// </summary>
    public sealed class WholeHpPayout
    {
        // Float sums of per-frame shares land a hair under the integer they add up to.
        private const float Epsilon = 1e-4f;

        private float _carry;

        public float Carry => _carry;

        /// <param name="earned">Hp earned this frame.</param>
        /// <param name="flush">True when every buffer is empty: pay the fractional rest too.</param>
        /// <returns>Hp to heal now: a whole number, or the final remainder on a flush.</returns>
        public float Pay(float earned, bool flush)
        {
            if (earned > 0f) _carry += earned;
            if (_carry <= Epsilon)
            {
                if (flush) _carry = 0f;
                return 0f;
            }

            var paid = flush ? _carry : (float)Math.Floor(_carry + Epsilon);
            // Subtract rather than zero: a whole step taken from 0.99995 leaves -0.00005, which the
            // next share absorbs instead of being handed out twice.
            _carry -= paid;
            return paid;
        }

        public void Clear() => _carry = 0f;
    }
}
