using System;
using System.Collections.Generic;

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

    /// <summary>
    /// Damage-over-time loss owed, kept as separate portions, each paid linearly over its own
    /// window: a tick is fully paid by its deadline (one interval after it fired), however other
    /// ticks overlap it. A merged pool would re-stretch the rest of an earlier tick past its deadline.
    /// </summary>
    public sealed class PortionBuffer
    {
        private readonly List<float> _left = new List<float>();
        private readonly List<float> _rate = new List<float>();

        public float Pending { get; private set; }

        public void Add(float amount, float window)
        {
            if (amount <= 0f) return;
            if (window <= 0f) throw new ArgumentOutOfRangeException(nameof(window));
            _left.Add(amount);
            _rate.Add(amount / window);
            Pending += amount;
        }

        /// <summary>The share earned by <paramref name="dt"/> seconds, summed over every portion.</summary>
        public float Take(float dt)
        {
            if (Pending <= 0f || dt <= 0f) return 0f;
            var taken = 0f;
            for (var i = _left.Count - 1; i >= 0; i--)
            {
                var chunk = Math.Min(_left[i], _rate[i] * dt);
                taken += chunk;
                _left[i] -= chunk;
                if (_left[i] <= 0f) Remove(i);
            }
            Pending = Math.Max(0f, Pending - taken);
            if (_left.Count == 0) Pending = 0f;
            return taken;
        }

        /// <summary>Drop up to <paramref name="amount"/> of what is owed, oldest portion first.</summary>
        public float Forgive(float amount)
        {
            var forgiven = 0f;
            while (amount > 0f && _left.Count > 0)
            {
                var chunk = Math.Min(_left[0], amount);
                _left[0] -= chunk;
                amount -= chunk;
                forgiven += chunk;
                if (_left[0] <= 0f) Remove(0);
            }
            Pending = _left.Count == 0 ? 0f : Math.Max(0f, Pending - forgiven);
            return forgiven;
        }

        public void Clear()
        {
            _left.Clear();
            _rate.Clear();
            Pending = 0f;
        }

        private void Remove(int i)
        {
            _left.RemoveAt(i);
            _rate.RemoveAt(i);
        }
    }

    public static class RegenMath
    {
        /// <summary>
        /// DoT owed after a clamp of health to <paramref name="cap"/> (Character.SetMaxHealth,
        /// Character.cs:3057-3067, or a heal clamped by RPC_Heal at max health). Vanilla, having already
        /// paid the debt, sits at <paramref name="health"/> - <paramref name="owed"/>: the clamp takes
        /// from it only what is above the cap, so the debt shrinks by what our clamp took too much.
        /// </summary>
        public static float OwedAfterCap(float health, float owed, float cap)
        {
            if (owed <= 0f) return 0f;
            var ours = Math.Min(health, cap);
            var vanilla = Math.Min(health - owed, cap);
            return Math.Max(0f, Math.Min(owed, ours - vanilla));
        }

        /// <summary>
        /// DoT owed after a heal of <paramref name="hp"/> lands at <paramref name="health"/> under
        /// <paramref name="max"/>. Vanilla heals from health - owed, so the part our heal loses to the
        /// max-health clamp would have landed there: it pays off that much debt instead.
        /// </summary>
        public static float OwedAfterHeal(float health, float owed, float hp, float max)
        {
            if (owed <= 0f || hp <= 0f) return Math.Max(0f, owed);
            var excess = health + hp - max;
            return excess > 0f ? Math.Max(0f, owed - excess) : owed;
        }

        /// <summary>
        /// GuiBar.SetValue restarts m_changeDelay on every rise of a smooth-fill bar and the bar
        /// only moves once that delay runs out (GuiBar.cs:73-76, 96-115). A +1 hp heal every
        /// fraction of a second keeps restarting it, so the HP bar froze until healing stopped.
        /// True = this SetValue is a rise that must not restart the delay (value stored directly).
        /// </summary>
        public static bool FillSkipsDelay(bool firstSet, float current, float value) =>
            !firstSet && value > current;

        /// <summary>
        /// Same for a drop while smoothed damage-over-time is being paid: -1 hp steps every fraction
        /// of a second would keep restarting the trail bar's delay, so it froze at the pre-DoT value
        /// and jumped once the burn ended. Other drops (a real hit) keep vanilla's delayed trail.
        /// </summary>
        public static bool BarSkipsDelay(bool firstSet, float current, float value, bool dotDraining) =>
            FillSkipsDelay(firstSet, current, value) || (!firstSet && dotDraining && value < current);

        /// <summary>
        /// Health to write when a damage-over-time tick would take vanilla's health to
        /// <paramref name="current"/> - <paramref name="owed"/> - <paramref name="loss"/>, or null to
        /// defer the loss. Vanilla has already paid every earlier tick, so its health is lower than
        /// ours by what is still owed; when that would be lethal the whole debt lands now, so death
        /// comes at vanilla's instant from vanilla's hit. God / ghost mode keep 1 hp like
        /// Character.ApplyDamage (Character.cs:2459-2462).
        /// </summary>
        public static float? LethalDotHealth(float current, float owed, float loss, bool godMode)
        {
            var vanilla = current - owed - loss;
            if (vanilla > 0f) return null;
            return godMode ? 1f : vanilla;
        }


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
    /// Turns the fractional hp the buffers earn each frame into whole 1 hp steps, so a rate of
    /// R hp/s lands as R one-hp steps per second. The amount is signed: healing earned minus
    /// damage-over-time owed, so regen and DoT net out into one rate instead of the bar bouncing
    /// +1 / -1. What is earned but not yet a whole hp is carried, and flushed once nothing more is
    /// coming, so the total still equals what went in.
    /// </summary>
    public sealed class WholeHpPayout
    {
        // Float sums of per-frame shares land a hair under the integer they add up to.
        private const float Epsilon = 1e-4f;

        private float _carry;

        public float Carry => _carry;

        /// <summary>Damage earned but not yet paid (the negative part of the carry).</summary>
        public float Debt => _carry < 0f ? -_carry : 0f;

        /// <param name="earned">Net hp earned this frame: heal minus damage.</param>
        /// <param name="flush">True when every buffer is empty: pay the fractional rest too.</param>
        /// <returns>
        /// Hp to apply now: a whole number (positive = heal, negative = damage), or the final
        /// remainder on a flush.
        /// </returns>
        public float Pay(float earned, bool flush)
        {
            _carry += earned;
            if (Math.Abs(_carry) <= Epsilon)
            {
                if (flush) _carry = 0f;
                return 0f;
            }

            float paid;
            if (flush) paid = _carry;
            else if (_carry > 0f) paid = (float)Math.Floor(_carry + Epsilon);
            else paid = -(float)Math.Floor(-_carry + Epsilon);
            // Subtract rather than zero: a whole step taken from 0.99995 leaves -0.00005, which the
            // next share absorbs instead of being handed out twice.
            _carry -= paid;
            return paid;
        }

        /// <summary>Hand over the unpaid damage (to settle it at once) and keep any unpaid heal.</summary>
        public float TakeDebt()
        {
            var debt = Debt;
            if (debt > 0f) _carry = 0f;
            return debt;
        }

        /// <summary>Drop up to <paramref name="amount"/> of the unpaid damage; returns how much was dropped.</summary>
        public float ForgiveDebt(float amount)
        {
            var dropped = Math.Min(Debt, Math.Max(0f, amount));
            _carry += dropped;
            return dropped;
        }

        public void Clear() => _carry = 0f;
    }
}
