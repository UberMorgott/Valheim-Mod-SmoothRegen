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

        private float _pending;
        private float _rate;

        public float Pending => _pending;

        /// <summary>
        /// Queue a tick's worth of healing, to be paid out over <paramref name="window"/> seconds.
        /// </summary>
        public void Add(float amount, float window)
        {
            if (amount <= 0f) return;
            if (window <= 0f) throw new ArgumentOutOfRangeException(nameof(window));

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
}
