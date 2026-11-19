using System;
using System.IO;
using System.Text;
using System.Threading;

namespace GrandTheftAccessibility
{
    // Background-threaded, double-buffered debug logger for the menu/phone
    // introspection probe (menulog spike). Deliberate spike-scoped copy of
    // DriveAssistLogger — do NOT refactor the drive logger into a shared base
    // while iter-37 verification is pending; unify later if menulog graduates
    // into a real menu-reader feature.
    class MenuLogger
    {
        private readonly object bufferLock = new object();
        private StringBuilder activeBuffer = new StringBuilder(64 * 1024);
        private StringBuilder backBuffer = new StringBuilder(64 * 1024);
        private StreamWriter writer;
        private Thread writerThread;
        private volatile bool running;
        private string filePath;

        public bool IsRunning { get { return running; } }
        public string FilePath { get { return filePath; } }

        public void Start()
        {
            if (running) return;
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                             + "/Rockstar Games/GTA V/ModSettings";
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                filePath = dir + "/menulog-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".log";
                writer = new StreamWriter(filePath, false) { AutoFlush = false };
                writer.WriteLine("# GTA11Y Menu/Phone Probe Debug Log");
                writer.WriteLine("# Session started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                writer.WriteLine("# EVENT lines mark state changes; FRAME lines are liveness heartbeats.");
                writer.WriteLine("# ----------------------------------------------------------------");
                writer.Flush();

                running = true;
                writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "MenuLogWriter"
                };
                writerThread.Start();
            }
            catch
            {
                running = false;
                try { if (writer != null) writer.Dispose(); } catch { }
                writer = null;
            }
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            try { if (writerThread != null) writerThread.Join(2000); } catch { }
            try
            {
                FlushBuffers();
                if (writer != null)
                {
                    writer.WriteLine("# Session ended: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    writer.Flush();
                }
            }
            catch { }
            try { if (writer != null) writer.Dispose(); } catch { }
            writer = null;
            writerThread = null;
        }

        public void Write(string text)
        {
            if (!running) return;
            lock (bufferLock)
            {
                activeBuffer.AppendLine(text);
            }
        }

        private void WriterLoop()
        {
            while (running)
            {
                Thread.Sleep(500);
                try { FlushBuffers(); } catch { }
            }
        }

        // Swaps the active and back buffers under the lock, then writes the now-idle
        // buffer to disk outside the lock so the game thread is never blocked on I/O.
        private void FlushBuffers()
        {
            StringBuilder toWrite;
            lock (bufferLock)
            {
                if (activeBuffer.Length == 0) return;
                toWrite = activeBuffer;
                activeBuffer = backBuffer;
                backBuffer = toWrite;
            }

            if (writer != null && toWrite.Length > 0)
            {
                writer.Write(toWrite.ToString());
                writer.Flush();
            }
            toWrite.Clear();
        }
    }
}
