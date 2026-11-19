using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace GrandTheftAccessibility
{
    // Background-threaded screenshot writer for drive-assist failure snapshots.
    //
    // Why this exists: the whole triage pipeline is numbers. Twenty-four iterations of
    // drive-assist work have not separated "wedged on a kerb" from "nose-in to a wall"
    // from "boxed in by traffic", because the log cannot show what the car was touching.
    // One frame at the failure instant answers that in a glance.
    //
    // Deliberately a small copy of DriveAssistLogger's shape (background thread, short
    // lock, IsBackground) rather than a shared base class. MenuLogger.cs already made
    // that call for the same reason and its comment still holds: unifying three loggers
    // while two of them are still being tuned trades a real risk for a cosmetic win.
    //
    // Capture is System.Drawing.CopyFromScreen, which returns BLACK under exclusive
    // fullscreen. That is not detectable from a flag, so the writer takes one frame at
    // startup and inspects it — see SelfTestStatus. It refuses to write anything if
    // that frame is blank, because a directory of black JPEGs costs an analyst a whole
    // session before they work out the capture never worked.
    class SnapshotWriter
    {
        // A class, not a struct: iter-55 aliasing mutates a job that is already queued,
        // and a struct would mutate a copy.
        private sealed class Job
        {
            public int Seq;
            public string Tag;
            public Bitmap Image;
            public string Suffix;     // "" for the failure frame, "-pre1" etc. for preroll
            public long FrameNo = -1; // tick this was grabbed on; -1 opts out of dedupe
            public List<string> Aliases;  // extra file names for the same encoded bytes
            public bool Sealed;       // worker has taken Aliases; no more may be added
        }

        // Downscale target. 1280 wide at q60 lands around 100-150 KB, which is small
        // enough that a session's worth of failures is a few MB.
        private const int TARGET_WIDTH = 1280;
        private const long JPEG_QUALITY = 60L;

        // Hard cap per session. Announced in the log AND out loud when it bites — a
        // silently truncated artifact set reads as "everything was captured" when it
        // was not, and nobody in this loop can see the directory to know otherwise.
        //
        // iter-55: 120 -> 600. On 08-12 the cap bit 7m48s into a 28-minute session, so
        // 72% of it had no images at all — and the failures that survive to the end of
        // a session are the ones an earlier fix did not stop, which makes them the
        // interesting ones. At ~120 KB a frame this is roughly 70 MB per session.
        private const int MAX_IMAGES = 600;

        // Bound on the shutdown drain, so a queued burst cannot hang the game's exit.
        private const int DRAIN_MAX = 32;

        // iter-54 C5: how the budget is SPENT, not just how big it is.
        //
        // First-come-first-served spending was the real defect. At up to five frames per
        // marker a 120-image budget was gone around marker 24, and 08-11-182121 produced
        // 82 markers across 44 incidents — so the entire back half of a long session had
        // no images while the artifact set still looked complete. Worse, the failures
        // that survive to the end of a session are exactly the ones a earlier fix did not
        // stop, which makes them the interesting ones.
        //
        // Both rules are fractions of MAX_IMAGES, so they scaled with the iter-55 raise
        // on their own: the tag quota is now 200 and the drawdown threshold 360.
        //
        // Two rules, both cheap:
        //   1. Once the budget is PREROLL_DRAWDOWN_PCT spent, stop accepting preroll
        //      frames but keep accepting failure frames. Preroll is 4 of every 5 images,
        //      so this stretches coverage roughly fivefold for the rest of the session
        //      while never losing the one frame that actually shows the failure.
        //   2. No single tag may take more than TAG_QUOTA_DIVISOR-th of the budget.
        //      One tag firing in a loop (a stuck ladder re-arming, say) otherwise
        //      crowds out every other kind of failure in the session.
        private const int PREROLL_DRAWDOWN_PCT = 60;
        private const int TAG_QUOTA_DIVISOR = 3;

        private readonly object queueLock = new object();
        private readonly Queue<Job> queue = new Queue<Job>();
        private readonly Dictionary<string, int> acceptedByTag = new Dictionary<string, int>();
        private Thread worker;
        private volatile bool running;
        private volatile int written;
        private volatile bool capped;
        private volatile bool capNoticed;

        // Budget is spent at ACCEPT time, not at write time. `written` is incremented by
        // the background thread after the JPEG lands, so during a burst it lags the queue
        // by however many frames are in flight — gating on it let a burst overshoot
        // MAX_IMAGES. `accepted` is the real ledger; `written` stays as the honest
        // report of what reached disk.
        private int accepted;
        private int droppedCap;
        private int droppedTagQuota;
        private int droppedPreroll;

        // iter-55 same-tick dedupe. Emitters that fire on one tick see one screen: on
        // 08-12, 11 of 120 images were byte-identical because DAMAGE-DRAWDOWN fires with
        // AUTO-COLLISION and ENGINE-STUCK with STUCK-ARM and STUCK-LAYER. The extra
        // markers now alias onto the frame already queued — one grab, one encode, one
        // budget slot — and the worker saves the same bytes under each marker's own seq
        // so triage's join by seq still finds an image for every one of them.
        private Job lastFailureJob;
        private long lastFailureFrameNo = -1;
        private int aliased;

        private string dir;
        private string selfTestStatus = "untested";
        private ImageCodecInfo jpegCodec;
        private EncoderParameters encoderParams;

        public bool IsRunning { get { return running; } }

        /// <summary>iter-53 D4: true exactly once, the first time the session cap bites.
        /// The cap comment above always claimed it was "announced in the log when it
        /// bites" — it was not, and a truncated image set is indistinguishable from a
        /// complete one. The caller polls this and writes EVENT capture-cap-hit.</summary>
        public bool ConsumeCapNotice()
        {
            if (!capped || capNoticed) return false;
            capNoticed = true;
            return true;
        }
        public string SelfTestStatus { get { return selfTestStatus; } }
        public string Directory { get { return dir; } }
        public int Written { get { return written; } }
        public bool HitCap { get { return capped; } }

        /// <summary>Starts the writer alongside a drive-assist log. `logPath` is the
        /// log's own file path — the image directory is named from its stem so
        /// triage-log.py can find the images for a log without being told where they
        /// are.</summary>
        public void Start(string logPath)
        {
            if (running) return;
            try
            {
                string stem = Path.GetFileNameWithoutExtension(logPath);
                string parent = Path.GetDirectoryName(logPath);
                dir = Path.Combine(parent, stem + "-snaps");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

                jpegCodec = GetJpegCodec();
                if (jpegCodec == null)
                {
                    selfTestStatus = "noJpegCodec";
                    return;
                }
                encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, JPEG_QUALITY);

                selfTestStatus = RunSelfTest();
                if (selfTestStatus != "ok") return;

                running = true;
                worker = new Thread(WriterLoop) { IsBackground = true, Name = "GTA11YSnapshotWriter" };
                worker.Start();
            }
            catch (Exception ex)
            {
                selfTestStatus = "startFailed:" + ex.GetType().Name;
                running = false;
            }
        }

        // ---- iter-56 C1: the self-test RETRIES instead of latching ----
        //
        // The old Start() ran the test exactly once and, on anything but "ok", returned
        // without ever setting `running`. Nothing re-tested, so one bad frame at F0 killed
        // capture for the whole session. On 08-12 that happened twice in three minutes and
        // cost both evening sessions every image they would have had.
        //
        // F0 is the WORST moment to sample: the mod starts while the game is still on a
        // loading or fade screen, and the world is already streamed in (EVENT test-anchor
        // reports a valid position) behind a screen that is not yet showing it. The 11:17
        // session passed only because the player happened to be sitting in the mod's own
        // menu — a bright, high-variance screen.
        //
        // Note what this means about the OLD test: before iter-55 the capture rect was the
        // whole desktop, so Windows icons and the taskbar guaranteed pixel variation and
        // IsEffectivelyBlank could not fail no matter what the game was doing. It passed
        // for the wrong reason for its entire life. iter-55 cropped to the game window and
        // the test became honest for the first time — the black frames are the test finally
        // working, not the capture breaking.
        private long lastRetryTicks;
        private int retryCount;
        private const long RETRY_INTERVAL_TICKS = 300000000L;  // 30 s

        /// <summary>Re-attempts a failed start. Cheap: one GDI blit per interval, and only
        /// while the writer is down. Returns true exactly once, on the attempt that
        /// succeeds, so the caller can announce the recovery.</summary>
        public bool RetryStartIfDown(string logPath)
        {
            if (running || dir == null) return false;
            if (selfTestStatus == "noJpegCodec") return false;   // never going to fix itself
            long now = DateTime.UtcNow.Ticks;
            if (lastRetryTicks != 0 && now - lastRetryTicks < RETRY_INTERVAL_TICKS) return false;
            lastRetryTicks = now;
            retryCount++;
            try
            {
                selfTestStatus = RunSelfTest();
                if (selfTestStatus != "ok") return false;
                running = true;
                worker = new Thread(WriterLoop) { IsBackground = true, Name = "GTA11YSnapshotWriter" };
                worker.Start();
                return true;
            }
            catch (Exception ex)
            {
                selfTestStatus = "retryFailed:" + ex.GetType().Name;
                running = false;
                return false;
            }
        }

        public int RetryCount { get { return retryCount; } }

        /// <summary>Captures requested while the writer was down. One `capture-unavailable`
        /// line stood in for ~66 lost images on 08-12; a count is the difference between
        /// "one hiccup" and "the whole session".</summary>
        private int droppedWhileDown;
        public int DroppedWhileDown { get { return droppedWhileDown; } }
        public void NoteDroppedWhileDown() { droppedWhileDown++; }

        /// <summary>Grabs one frame and decides whether capture works at all.
        /// Returns "ok", "black" (exclusive fullscreen — switch to borderless), or an
        /// error token.</summary>
        private string RunSelfTest()
        {
            Bitmap bmp = null;
            try
            {
                bmp = GrabScreen();
                if (bmp == null) return "grabFailed";
                return IsEffectivelyBlank(bmp) ? "black" : "ok";
            }
            catch (Exception ex) { return "selfTestFailed:" + ex.GetType().Name; }
            finally { if (bmp != null) bmp.Dispose(); }
        }

        /// <summary>Samples a coarse grid rather than every pixel — a real game frame
        /// has variation almost everywhere, and a letterboxed or loading frame would
        /// otherwise read as blank.</summary>
        private static bool IsEffectivelyBlank(Bitmap bmp)
        {
            int stepX = Math.Max(1, bmp.Width / 24);
            int stepY = Math.Max(1, bmp.Height / 24);
            int min = 255, max = 0;
            for (int y = 0; y < bmp.Height; y += stepY)
            {
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    Color c = bmp.GetPixel(x, y);
                    int lum = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
                    if (lum < min) min = lum;
                    if (lum > max) max = lum;
                }
            }
            return (max - min) < 8;
        }

        // ================================================================
        // Capture region (iter-55)
        // ================================================================
        //
        // This used to blit the whole primary screen. With the game in a window that put
        // roughly 40% Windows desktop — icons, title bar, taskbar — into every frame, and
        // spent the 1280 px downscale budget on it: 08-12-111758 captured the game at
        // 1015 px wide inside a 1280 px image. The blank-frame self-test cannot catch
        // that, because a desktop has variation everywhere; it was built for the
        // exclusive-fullscreen black frame and is blind to capturing the wrong thing.
        //
        // GetClientRect, ClientToScreen and CopyFromScreen all work in the CALLING
        // process's coordinate space, so DPI virtualisation is consistent across them and
        // no scaling correction is needed. Do not mix in a rectangle from anywhere else —
        // settings.xml reports 1920x1080 on this machine while a DPI-unaware out-of-process
        // probe reports 1536x864 for the same display.
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        // A window smaller than this is not a game being played — it is minimised, being
        // dragged, or mid-mode-switch. Fall back rather than write a useless frame.
        private const int MIN_CAPTURE_DIM = 320;
        private const long RECT_RESOLVE_INTERVAL_TICKS = 30000000L;  // 3 s

        private static readonly object rectLock = new object();
        private static IntPtr cachedHwnd = IntPtr.Zero;
        private static string cachedHwndSource;
        private static Rectangle cachedRect;
        private static string cachedRectSource = "unresolved";
        private static long cachedRectStamp;

        /// <summary>The screen rectangle GrabScreen will blit, and how it was found
        /// ("mainWindow", "findWindow" or "screenFallback"). Re-resolved every few
        /// seconds so a window moved or resized mid-session is tracked — the resolve is
        /// two user32 calls, and everything else is served from the cache.</summary>
        public static Rectangle ResolveCaptureRect(out string source)
        {
            lock (rectLock)
            {
                long now = DateTime.UtcNow.Ticks;
                if (cachedRectSource != "unresolved"
                    && now - cachedRectStamp < RECT_RESOLVE_INTERVAL_TICKS)
                {
                    source = cachedRectSource;
                    return cachedRect;
                }

                Rectangle rect;
                string src;
                if (!TryResolveWindowRect(out rect, out src))
                {
                    rect = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                    src = "screenFallback";
                }
                cachedRect = rect;
                cachedRectSource = src;
                cachedRectStamp = now;
                source = src;
                return rect;
            }
        }

        private static bool TryResolveWindowRect(out Rectangle rect, out string source)
        {
            rect = Rectangle.Empty;
            IntPtr hwnd = ResolveHwnd(out source);
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                if (IsIconic(hwnd)) return false;   // minimised: the client rect is 0x0

                RECT rc;
                if (!GetClientRect(hwnd, out rc))
                {
                    // Stale handle (the window was recreated). Drop it so the next
                    // resolve looks the game up again instead of falling back forever.
                    cachedHwnd = IntPtr.Zero;
                    cachedHwndSource = null;
                    return false;
                }
                int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
                if (w < MIN_CAPTURE_DIM || h < MIN_CAPTURE_DIM) return false;

                POINT tl; tl.X = rc.Left; tl.Y = rc.Top;
                if (!ClientToScreen(hwnd, ref tl)) return false;

                var candidate = new Rectangle(tl.X, tl.Y, w, h);
                if (!System.Windows.Forms.SystemInformation.VirtualScreen.IntersectsWith(candidate))
                    return false;   // off-screen coordinates would blit nothing useful

                rect = candidate;
                return true;
            }
            catch { return false; }
        }

        /// <summary>The game's window. The script runs in-process in GTA5.exe, so the
        /// current process's main window IS the game — but it can be Zero early in
        /// startup, so a miss is re-resolved rather than latched.</summary>
        private static IntPtr ResolveHwnd(out string source)
        {
            if (cachedHwnd != IntPtr.Zero) { source = cachedHwndSource; return cachedHwnd; }
            try
            {
                IntPtr h;
                using (var p = Process.GetCurrentProcess()) h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    cachedHwnd = h; cachedHwndSource = "mainWindow";
                    source = cachedHwndSource; return h;
                }
            }
            catch { }
            try
            {
                IntPtr h = FindWindow(null, "Grand Theft Auto V");
                if (h != IntPtr.Zero)
                {
                    cachedHwnd = h; cachedHwndSource = "findWindow";
                    source = cachedHwndSource; return h;
                }
            }
            catch { }
            source = null;
            return IntPtr.Zero;
        }

        /// <summary>"true", "false", or "?" when the game's window is not known. With a
        /// windowed game an alt-tab means CopyFromScreen grabs whatever is on top, so
        /// triage needs to be able to tell an occluded frame from a real one.
        /// PrintWindow is not an alternative — it returns black on a D3D game window.</summary>
        private static string ForegroundState()
        {
            try
            {
                if (cachedHwnd == IntPtr.Zero) return "?";
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return "?";
                return fg == cachedHwnd ? "true" : "false";
            }
            catch { return "?"; }
        }

        /// <summary>Share of the primary screen the capture region covers, 0-100.</summary>
        private static int ClientFractionPct(Rectangle rect)
        {
            try
            {
                Rectangle screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                long area = (long)screen.Width * screen.Height;
                if (area <= 0) return 0;
                return (int)Math.Round(100.0 * rect.Width * rect.Height / area);
            }
            catch { return 0; }
        }

        /// <summary>Measured capture geometry for the log. This is GROUND TRUTH about
        /// what the writer will blit; settings.xml is not. On 08-12 that file was three
        /// weeks stale and claimed Windowed=0 at 1920x1080 while the game was running in
        /// a window covering about 60% of the screen.</summary>
        public static string GeometryReport()
        {
            try
            {
                string src;
                Rectangle r = ResolveCaptureRect(out src);
                Rectangle screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                return "captureRect=" + r.X + "," + r.Y + "," + r.Width + "x" + r.Height
                     + " src=" + src
                     + " screen=" + screen.Width + "x" + screen.Height
                     + " clientFrac=" + ClientFractionPct(r) + "%"
                     + " foreground=" + ForegroundState();
            }
            catch (Exception ex) { return "captureRect=FAILED:" + ex.GetType().Name; }
        }

        /// <summary>True when the frames about to be written are not a clean full-screen
        /// view of the game, with a sentence saying why. The caller speaks it: this
        /// audience cannot see the artifacts, so a degraded capture is invisible to
        /// everyone until an agent opens an image days later.</summary>
        public static bool IsCaptureDegraded(out string why)
        {
            why = null;
            try
            {
                string src;
                Rectangle r = ResolveCaptureRect(out src);
                if (src == "screenFallback")
                {
                    why = "the game window could not be found, so screenshots will "
                        + "include the whole desktop";
                    return true;
                }
                int frac = ClientFractionPct(r);
                if (frac < 90)
                {
                    why = "the game window covers only " + frac
                        + " percent of the screen, so screenshot detail will be reduced";
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        public static Bitmap GrabScreen()
        {
            string src;
            Rectangle r = ResolveCaptureRect(out src);
            if (r.Width <= 0 || r.Height <= 0) return null;
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        /// <summary>Grabs the frame on the calling thread (it has to be — there is only
        /// one screen) and hands the encode to the background. The grab is a GDI blit
        /// of a few milliseconds; the JPEG encode, which is the expensive half, never
        /// touches the script thread.</summary>
        public void CaptureFor(int seq, string tag)
        {
            Capture(seq, tag, "", -1);
        }

        /// <summary>As CaptureFor, but tells the writer which tick this is. Markers that
        /// share a tick share a screen, so the second and later ones alias onto the
        /// first's frame instead of grabbing and encoding it again.</summary>
        public void CaptureFor(int seq, string tag, long frameNo)
        {
            Capture(seq, tag, "", frameNo);
        }

        public void Capture(int seq, string tag, string suffix)
        {
            Capture(seq, tag, suffix, -1);
        }

        public void Capture(int seq, string tag, string suffix, long frameNo)
        {
            if (!running) return;
            if (suffix == null) suffix = "";

            // Same-tick alias. Checked before the cap, because an alias costs no budget:
            // the frame it points at has already been paid for.
            if (frameNo >= 0 && suffix.Length == 0 && TryAliasSameFrame(seq, tag, frameNo))
                return;

            // Cheap pre-check so a fully-spent budget does not pay for a GDI blit it is
            // only going to throw away. Enqueue re-checks under the lock — this one is an
            // optimisation, not the decision.
            if (accepted >= MAX_IMAGES) { capped = true; return; }
            try
            {
                Bitmap raw = GrabScreen();
                if (raw == null) return;
                Enqueue(seq, tag, suffix, raw, frameNo);
            }
            catch { /* a failed capture must never take the game down */ }
        }

        /// <summary>Points another (seq, tag) at the failure frame already queued for
        /// this tick. Returns false — meaning "capture normally" — when there is no such
        /// frame, or when the worker has already sealed it. That race costs one extra
        /// frame, which is exactly the old behaviour, so it can only ever be a wash.</summary>
        private bool TryAliasSameFrame(int seq, string tag, long frameNo)
        {
            lock (queueLock)
            {
                if (lastFailureJob == null || lastFailureFrameNo != frameNo) return false;
                if (lastFailureJob.Sealed) return false;
                if (lastFailureJob.Aliases == null) lastFailureJob.Aliases = new List<string>();
                lastFailureJob.Aliases.Add(FileNameFor(seq, tag, ""));
                aliased++;
                return true;
            }
        }

        /// <summary>Queues an already-captured frame (used by the preroll ring, which
        /// grabs on its own schedule and only encodes when a failure fires).
        ///
        /// Takes ownership of <paramref name="raw"/> unconditionally — every refusal path
        /// below disposes it. A caller that has handed a bitmap here must not touch it
        /// again.</summary>
        public void Enqueue(int seq, string tag, string suffix, Bitmap raw)
        {
            Enqueue(seq, tag, suffix, raw, -1);
        }

        public void Enqueue(int seq, string tag, string suffix, Bitmap raw, long frameNo)
        {
            if (!running || raw == null) { if (raw != null) raw.Dispose(); return; }
            if (suffix == null) suffix = "";

            lock (queueLock)
            {
                if (accepted >= MAX_IMAGES)
                {
                    capped = true; droppedCap++; raw.Dispose(); return;
                }
                // Preroll drawdown: the run-up is a luxury, the failure frame is not.
                // Preroll is 4 of every 5 images, so refusing it past the threshold
                // stretches the remaining budget roughly fivefold without ever losing the
                // one frame that shows the failure itself.
                if (suffix.Length > 0 && accepted * 100 >= MAX_IMAGES * PREROLL_DRAWDOWN_PCT)
                {
                    droppedPreroll++; raw.Dispose(); return;
                }
                // Per-tag quota, so one failure kind firing in a loop cannot own the
                // session's whole artifact set.
                string key = string.IsNullOrEmpty(tag) ? "UNTAGGED" : tag;
                int forTag;
                acceptedByTag.TryGetValue(key, out forTag);
                if (forTag >= MAX_IMAGES / TAG_QUOTA_DIVISOR)
                {
                    droppedTagQuota++; raw.Dispose(); return;
                }

                acceptedByTag[key] = forTag + 1;
                accepted++;
                var job = new Job
                {
                    Seq = seq, Tag = tag, Image = raw, Suffix = suffix, FrameNo = frameNo
                };
                queue.Enqueue(job);

                // Only a failure frame is aliasable. Preroll already dedupes itself:
                // FlushPrerollFor empties the ring, so a second marker on the same tick
                // finds nothing to flush.
                if (suffix.Length == 0 && frameNo >= 0)
                {
                    lastFailureJob = job;
                    lastFailureFrameNo = frameNo;
                }
            }
        }

        private void WriterLoop()
        {
            while (running)
            {
                Job job = Dequeue();
                if (job == null) { Thread.Sleep(60); continue; }
                WriteJob(job);
            }

            // Drain whatever is left so a failure right before shutdown is not lost.
            //
            // iter-55: this block used to dequeue each remaining job and DISPOSE it,
            // discarding exactly the frames the comment above promised to keep — and a
            // failure in the last seconds before the log stops is not a rare case, it is
            // the one you stopped the log to look at. Bounded so a queued burst cannot
            // hold up the game's exit; anything past the bound is still freed below.
            for (int i = 0; i < DRAIN_MAX; i++)
            {
                Job job = Dequeue();
                if (job == null) break;
                WriteJob(job);
            }
            lock (queueLock)
            {
                while (queue.Count > 0)
                {
                    var j = queue.Dequeue();
                    if (j.Image != null) j.Image.Dispose();
                }
            }
        }

        private Job Dequeue()
        {
            lock (queueLock)
            {
                return queue.Count > 0 ? queue.Dequeue() : null;
            }
        }

        private void WriteJob(Job job)
        {
            Bitmap scaled = null;
            try
            {
                scaled = Downscale(job.Image);
                string primary = Path.Combine(dir, FileNameFor(job.Seq, job.Tag, job.Suffix));
                scaled.Save(primary, jpegCodec, encoderParams);
                written++;

                // Seal under the lock and take the alias list in the same breath, so a
                // marker firing right now either gets into this list or is told to
                // capture for itself — never silently dropped between the two.
                List<string> aliases = null;
                lock (queueLock)
                {
                    job.Sealed = true;
                    if (job.Aliases != null && job.Aliases.Count > 0)
                    {
                        aliases = job.Aliases;
                        job.Aliases = null;
                    }
                    if (ReferenceEquals(lastFailureJob, job)) lastFailureJob = null;
                }
                if (aliases != null)
                {
                    // Copy rather than re-encode: same bytes, no second JPEG pass, and
                    // triage's join by seq finds an image under every marker.
                    foreach (string alias in aliases)
                    {
                        try
                        {
                            File.Copy(primary, Path.Combine(dir, alias), true);
                            written++;
                        }
                        catch { }
                    }
                }
            }
            catch { /* drop the frame rather than kill the thread */ }
            finally
            {
                if (scaled != null) scaled.Dispose();
                if (job.Image != null) job.Image.Dispose();
            }
        }

        private static string FileNameFor(int seq, string tag, string suffix)
        {
            return string.Format("snap-{0:D4}-{1}{2}.jpg",
                                 seq, SanitizeTag(tag), suffix ?? "");
        }

        private static Bitmap Downscale(Bitmap src)
        {
            if (src.Width <= TARGET_WIDTH) return new Bitmap(src);
            int w = TARGET_WIDTH;
            int h = (int)Math.Round(src.Height * (TARGET_WIDTH / (double)src.Width));
            var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, 0, 0, w, h);
            }
            return dst;
        }

        private static string SanitizeTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "UNTAGGED";
            var sb = new System.Text.StringBuilder(tag.Length);
            foreach (char c in tag)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            return sb.ToString();
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            return null;
        }

        public void Stop()
        {
            running = false;
            // iter-55: 1500 -> 3000 ms. The drain now encodes what it dequeues instead of
            // throwing it away, and a few JPEGs at ~10-20 ms each need the headroom.
            // DRAIN_MAX is what actually bounds it; this is the belt to that braces.
            try { if (worker != null && worker.IsAlive) worker.Join(3000); } catch { }
            worker = null;
        }

        /// <summary>One-line census for the drive-assist log.
        ///
        /// The drop counters are broken out by REASON, not summed. "120 written, 60
        /// dropped" cannot be acted on; "dropped 12 to the tag quota" says one failure
        /// kind is dominating the session, and "dropped 48 preroll" says the budget held
        /// for failure frames exactly as intended. Triage reads these to decide whether
        /// a missing image is a bug or a deliberate economy.
        ///
        /// iter-55 adds `aliased` (markers that shared a tick with an earlier one and so
        /// cost no budget) and the measured geometry block. The geometry is the part
        /// worth reading first: `src=screenFallback` or a low `clientFrac` means the
        /// images contain desktop, which no other field in this line — and no amount of
        /// staring at the numbers — would ever reveal.</summary>
        public string Census()
        {
            return "selfTest=" + selfTestStatus
                 + " running=" + running
                 + " accepted=" + accepted
                 + " written=" + written
                 + " aliased=" + aliased
                 + " cap=" + MAX_IMAGES
                 + " capped=" + capped
                 + " dropCap=" + droppedCap
                 + " dropPreroll=" + droppedPreroll
                 + " dropTagQuota=" + droppedTagQuota
                 + " dropWhileDown=" + droppedWhileDown   // iter-56 C3
                 + " retries=" + retryCount
                 + " tags=" + TagBreakdown()
                 + " dir=" + (dir == null ? "none" : Path.GetFileName(dir))
                 + " " + GeometryReport();
        }

        /// <summary>Per-tag accepted counts, e.g. "STUCK-LAYER:40,AUTO-COLLISION:18".
        /// Sorted descending so the tag that is eating the budget reads first.</summary>
        private string TagBreakdown()
        {
            var pairs = new List<KeyValuePair<string, int>>();
            lock (queueLock)
            {
                foreach (var kv in acceptedByTag) pairs.Add(kv);
            }
            if (pairs.Count == 0) return "none";
            pairs.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });
            var sb = new System.Text.StringBuilder();
            foreach (var kv in pairs)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(SanitizeTag(kv.Key)).Append(':').Append(kv.Value);
            }
            return sb.ToString();
        }
    }
}
