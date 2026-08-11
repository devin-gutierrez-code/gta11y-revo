using System;
using System.Collections.Generic;
using DavyKager;

namespace GrandTheftAccessibility
{
    public enum SpeechPriority
    {
        Ambient = 0,        // status chatter, heading/zone changes — only voiced when the reader is idle
        Informational = 1,  // normal announcements
        Critical = 2        // collision/brake warnings, arrivals, target-lost — always interrupts
    }

    /// <summary>
    /// Priority speech arbiter that wraps <see cref="Tolk"/>.
    ///
    /// Historically, many onTick subsystems called <c>Tolk.Speak</c> directly in the
    /// same frame. Because each call carries its own <c>interrupt</c> flag, the last
    /// <c>interrupt=true</c> caller in a tick cut off everything queued before it — so a
    /// brake warning and a zone-change announcement firing on the same tick meant the
    /// blind player heard only a garbled fragment, or the wrong one.
    ///
    /// Callers now <see cref="Request"/> speech during the tick; <see cref="Drain"/> is
    /// called once at the end of onTick and arbitrates:
    ///   * If anything Critical was requested this tick, Critical items are spoken first
    ///     (the very first interrupts, the rest queue behind it), then Informational
    ///     items; Ambient items are dropped for that tick.
    ///   * If only Informational/Ambient were requested, Informational interrupts+queues.
    ///   * Pure Ambient is voiced only when the screen reader is otherwise idle, so it
    ///     never steps on a current announcement.
    ///
    /// Single-threaded: only touched from the script's onTick thread.
    /// </summary>
    public static class Speech
    {
        private struct Item { public string Text; public SpeechPriority Priority; }
        private static readonly List<Item> pending = new List<Item>();

        /// <summary>
        /// Optional log hook invoked for every line actually emitted to the reader
        /// (post-arbitration — dropped Ambient lines do not fire). The debug log is
        /// the only record of what a blind tester heard, so the drive-assist logger
        /// installs itself here at startup. Never throws into Drain().
        /// </summary>
        public static Action<string, SpeechPriority> LogSink = null;

        private static long lastHealthCheckTicks = 0;
        private static bool reloadAttempted = false;

        /// <summary>
        /// Wall-clock (<c>DateTime.Now.Ticks</c>) stamp of the last line actually emitted
        /// to the reader. A cheap "is the channel busy?" test for low-priority subsystems
        /// that must not talk over navigation or collision cues: an Ambient request is
        /// still arbitrated per tick, but a subsystem whose whole point is periodic
        /// chatter (the Civil Unrest sitrep) should skip its slot entirely rather than
        /// queue behind something the player is still listening to. 0 until first speech.
        /// </summary>
        public static long LastEmittedWallTicks { get; private set; }

        public static void Request(string text, SpeechPriority priority)
        {
            if (string.IsNullOrEmpty(text)) return;
            pending.Add(new Item { Text = text, Priority = priority });
        }

        // Convenience entry points.
        public static void Critical(string text) { Request(text, SpeechPriority.Critical); }
        public static void Info(string text) { Request(text, SpeechPriority.Informational); }
        public static void Ambient(string text) { Request(text, SpeechPriority.Ambient); }

        /// <summary>
        /// Arbitrate and emit everything requested this tick. Call once per onTick.
        ///
        /// Designed so the common case — a single request in a tick — is byte-identical
        /// to the old direct Tolk calls: a lone Info speaks with interrupt=true (like the
        /// old <c>Tolk.Speak(x, true)</c>), a lone Ambient speaks with interrupt=false
        /// (like the old <c>Tolk.Speak(x)</c>). Arbitration only changes multi-request
        /// ticks: items are emitted in priority order (Critical, then Informational, then
        /// Ambient); the first emitted leads with an interrupt (unless it is Ambient,
        /// which never cuts off current speech), and the rest append behind it. Ambient
        /// is dropped entirely when something Critical fired the same tick, so an
        /// emergency is never delayed by status chatter.
        /// </summary>
        public static void Drain()
        {
            if (pending.Count == 0) return;
            try
            {
                bool anyCritical = false;
                for (int i = 0; i < pending.Count; i++)
                    if (pending[i].Priority == SpeechPriority.Critical) { anyCritical = true; break; }

                bool firstSpoken = false;
                for (int pr = (int)SpeechPriority.Critical; pr >= (int)SpeechPriority.Ambient; pr--)
                {
                    // Drop ambient chatter when an emergency was requested this tick.
                    if (pr == (int)SpeechPriority.Ambient && anyCritical) continue;

                    for (int i = 0; i < pending.Count; i++)
                    {
                        if ((int)pending[i].Priority != pr) continue;
                        // The first thing spoken interrupts (matching the old interrupt=true
                        // semantics of Info/Critical); Ambient never interrupts; everything
                        // after the first appends.
                        bool interrupt = !firstSpoken && pr != (int)SpeechPriority.Ambient;
                        SpeakSafe(pending[i].Text, interrupt);
                        try { LogSink?.Invoke(pending[i].Text, pending[i].Priority); } catch { }
                        LastEmittedWallTicks = DateTime.Now.Ticks;
                        firstSpoken = true;
                    }
                }
            }
            finally
            {
                pending.Clear();
            }
        }

        /// <summary>
        /// Periodic screen-reader liveness check. If Tolk reports it is no longer loaded
        /// (reader crashed/restarted), attempt a single reload so speech can recover. A
        /// dead reader can't be announced to, so the recovery is the fallback.
        /// </summary>
        public static void HealthTick(long nowTicks)
        {
            if (nowTicks - lastHealthCheckTicks < 50000000L) return; // every 5s
            lastHealthCheckTicks = nowTicks;
            try
            {
                if (!Tolk.IsLoaded())
                {
                    if (!reloadAttempted)
                    {
                        reloadAttempted = true;
                        try { Tolk.Load(); } catch { }
                        try { if (!Tolk.IsLoaded()) Tolk.TrySAPI(true); } catch { }
                    }
                }
                else
                {
                    reloadAttempted = false; // healthy again; allow a future recovery attempt
                }
            }
            catch { }
        }

        private static void SpeakSafe(string text, bool interrupt)
        {
            try { Tolk.Speak(text, interrupt); } catch { }
        }
    }
}
