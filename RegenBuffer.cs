using System;

namespace SmoothRegen
{
    /// <summary>
    /// Holds a lump of healing and pays it out gradually.
    /// Pure arithmetic, no Unity types, so it can be unit tested on its own.
    /// The invariant that matters: everything put in comes out, no more, no less.
    /// </summary>
    public sealed class RegenBuffer
    {
        private float _pending;
        private float _rate;

        public float Pending => _pending;

        /// <summary>
        /// Queue a tick's worth of healing, to be paid out over <paramref name="window"/> seconds.
        /// <paramref name="cap"/> bounds the total held, so an idle player at full health
        /// cannot bank an unbounded heal.
        /// </summary>
        public void Add(float amount, float window, float cap = float.PositiveInfinity)
        {
            if (amount <= 0f) return;
            if (window <= 0f) throw new ArgumentOutOfRangeException(nameof(window));

            _pending += amount;
            if (_pending > cap) _pending = cap;
            // Re-spread whatever is left, so a tick arriving early does not strand a remainder.
            _rate = _pending / window;
        }

        /// <summary>
        /// Take the share earned by <paramref name="dt"/> seconds, never more than
        /// <paramref name="limit"/>. Never overdraws. A limit of zero pays nothing and
        /// leaves the buffer intact - that is how a full-health player keeps their pending heal.
        /// </summary>
        public float Take(float dt, float limit = float.PositiveInfinity)
        {
            if (_pending <= 0f || dt <= 0f || limit <= 0f) return 0f;

            var chunk = _rate * dt;
            if (float.IsNaN(chunk) || chunk >= _pending) chunk = _pending;
            if (chunk > limit) chunk = limit;

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
