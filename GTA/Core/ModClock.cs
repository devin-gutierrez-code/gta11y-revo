using System;

namespace GrandTheftAccessibility
{
    /// <summary>
    /// Monotonic mod-time clock. Advances only while the script is actually
    /// receiving frames, so durations measured on it cannot be inflated by
    /// pause menus, cutscenes, alt-tab or load screens the way
    /// DateTime.Now-based stamps are (a timer armed pre-pause instantly saw
    /// the whole wall-clock gap on resume and misfired stuck-escalation /
    /// watchdog-release / grind detection).
    ///
    /// Units are 100 ns ticks — the same scale as DateTime.Ticks — so every
    /// existing duration constant (10000000 = 1 s etc.) works unchanged.
    ///
    /// RULE: a stamp written from ModClock.Ticks must only ever be compared
    /// to ModClock.Ticks. Never mix clocks on one variable. Rate-limiters
    /// (log/speech throttles, scan cadence) intentionally stay on wall time —
    /// one early fire after a pause is harmless there.
    /// </summary>
    public static class ModClock
    {
        // Start at 1 s so a 0-valued field keeps working as the universal
        // "not stamped yet" sentinel (`stamp > 0` guards stay valid even for
        // stamps taken on the very first frame).
        private const long BASE_TICKS = 10000000L;

        // A single frame hitch/load can legitimately take a while; cap what a
        // frame may contribute so mod-time can never leap the way wall time
        // does. 250 ms is far above any real render frame.
        private const float MAX_FRAME_SECONDS = 0.25f;

        // Wall gaps above this are treated as a pause/alt-tab/load boundary.
        private const long GAP_THRESHOLD_TICKS = 10000000L; // 1 s

        private static long lastWallTicks = 0;

        /// <summary>Monotonic mod time in 100 ns ticks.</summary>
        public static long Ticks { get; private set; } = BASE_TICKS;

        /// <summary>Wall-clock gap (100 ns ticks) detected on the current
        /// frame — nonzero only on the first frame after a &gt;1 s stall.
        /// Consumers may use it to rebase or clear wall-time state.</summary>
        public static long LastGapTicks { get; private set; }

        /// <summary>Call exactly once at the top of onTick, before any
        /// consumer reads Ticks. frameSeconds is the frame delta (clamping to
        /// MAX_FRAME_SECONDS happens here); wallTicks is DateTime.Now.Ticks.</summary>
        public static void Tick(float frameSeconds, long wallTicks)
        {
            if (frameSeconds < 0f) frameSeconds = 0f;
            if (frameSeconds > MAX_FRAME_SECONDS) frameSeconds = MAX_FRAME_SECONDS;
            Ticks += (long)(frameSeconds * 10000000L);

            long wallDelta = lastWallTicks > 0 ? wallTicks - lastWallTicks : 0;
            lastWallTicks = wallTicks;
            LastGapTicks = wallDelta > GAP_THRESHOLD_TICKS ? wallDelta : 0;
        }
    }
}
