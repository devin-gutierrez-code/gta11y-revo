using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
        private struct Job
        {
            public int Seq;
            public string Tag;
            public Bitmap Image;
            public string Suffix;     // "" for the failure frame, "-pre1" etc. for preroll
        }

        // Downscale target. 1280 wide at q60 lands around 100-150 KB, which is small
        // enough that a session's worth of failures is a few MB.
        private const int TARGET_WIDTH = 1280;
        private const long JPEG_QUALITY = 60L;

        // Hard cap per session. Announced in the log when it bites — a silently
        // truncated artifact set reads as "everything was captured" when it was not.
        private const int MAX_IMAGES = 120;

        private readonly object queueLock = new object();
        private readonly Queue<Job> queue = new Queue<Job>();
        private Thread worker;
        private volatile bool running;
        private volatile int written;
        private volatile bool capped;

        private string dir;
        private string selfTestStatus = "untested";
        private ImageCodecInfo jpegCodec;
        private EncoderParameters encoderParams;

        public bool IsRunning { get { return running; } }
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

        public static Bitmap GrabScreen()
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        /// <summary>Grabs the frame on the calling thread (it has to be — there is only
        /// one screen) and hands the encode to the background. The grab is a GDI blit
        /// of a few milliseconds; the JPEG encode, which is the expensive half, never
        /// touches the script thread.</summary>
        public void CaptureFor(int seq, string tag)
        {
            Capture(seq, tag, "");
        }

        public void Capture(int seq, string tag, string suffix)
        {
            if (!running) return;
            if (written >= MAX_IMAGES) { capped = true; return; }
            try
            {
                Bitmap raw = GrabScreen();
                if (raw == null) return;
                Enqueue(seq, tag, suffix, raw);
            }
            catch { /* a failed capture must never take the game down */ }
        }

        /// <summary>Queues an already-captured frame (used by the preroll ring, which
        /// grabs on its own schedule and only encodes when a failure fires).</summary>
        public void Enqueue(int seq, string tag, string suffix, Bitmap raw)
        {
            if (!running || raw == null) { if (raw != null) raw.Dispose(); return; }
            if (written >= MAX_IMAGES) { capped = true; raw.Dispose(); return; }
            lock (queueLock)
            {
                queue.Enqueue(new Job { Seq = seq, Tag = tag, Image = raw, Suffix = suffix ?? "" });
            }
        }

        private void WriterLoop()
        {
            while (running)
            {
                Job job;
                bool have = false;
                lock (queueLock)
                {
                    if (queue.Count > 0) { job = queue.Dequeue(); have = true; }
                    else job = default(Job);
                }
                if (!have) { Thread.Sleep(60); continue; }

                Bitmap scaled = null;
                try
                {
                    scaled = Downscale(job.Image);
                    string safeTag = SanitizeTag(job.Tag);
                    string name = string.Format("snap-{0:D4}-{1}{2}.jpg", job.Seq, safeTag, job.Suffix);
                    scaled.Save(Path.Combine(dir, name), jpegCodec, encoderParams);
                    written++;
                }
                catch { /* drop the frame rather than kill the thread */ }
                finally
                {
                    if (scaled != null) scaled.Dispose();
                    if (job.Image != null) job.Image.Dispose();
                }
            }

            // Drain whatever is left so a failure right before shutdown is not lost.
            lock (queueLock)
            {
                while (queue.Count > 0)
                {
                    var j = queue.Dequeue();
                    if (j.Image != null) j.Image.Dispose();
                }
            }
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
            try { if (worker != null && worker.IsAlive) worker.Join(1500); } catch { }
            worker = null;
        }

        /// <summary>One-line census for the drive-assist log.</summary>
        public string Census()
        {
            return "selfTest=" + selfTestStatus
                 + " running=" + running
                 + " written=" + written
                 + " capped=" + capped
                 + " dir=" + (dir == null ? "none" : Path.GetFileName(dir));
        }
    }
}
