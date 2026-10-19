using System;

namespace HollowKnightTrial
{
    // Observes InControl edges only; never invokes gameplay methods or changes input.
    public sealed class JumpInputTiming
    {
        double pressedAt = double.NaN, fixedAtPress = double.NaN;
        bool held;

        public void Reset() { pressedAt = fixedAtPress = double.NaN; held = false; }

        public void Record(bool isHeld, bool wasPressed, double gameTime, double fixedTime)
        {
            held = isHeld;
            if (isHeld && wasPressed) { pressedAt = gameTime; fixedAtPress = fixedTime; }
        }

        public JumpInputSnapshot Capture(bool currentlyHeld, double gameTime, double fixedTime, double step)
        {
            bool known = held && currentlyHeld && !double.IsNaN(pressedAt) && step > 0
                && gameTime >= pressedAt && fixedTime >= fixedAtPress;
            return new JumpInputSnapshot {
                schema_version = 1,
                jump_held = currentlyHeld,
                jump_press_age_seconds = known ? (double?)Math.Round(gameTime - pressedAt, 4) : null,
                physics_steps_since_jump_press = known ? (int?)Math.Max(0, (int)Math.Round((fixedTime - fixedAtPress) / step)) : null
            };
        }
    }

    public sealed class JumpInputSnapshot
    {
        public int schema_version;
        public bool jump_held;
        public double? jump_press_age_seconds;
        public int? physics_steps_since_jump_press;
    }
}
