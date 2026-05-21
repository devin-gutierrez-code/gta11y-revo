using System;
using System.IO;
using System.Text;
using System.Threading;

namespace GrandTheftAccessibility
{
    // Background-threaded, double-buffered debug logger for the drive-assist feature.
    // Write() only appends to an in-memory buffer under a short lock, so it is cheap
    // enough to call every frame from onTick. A background thread does the disk I/O,
    // which is why this does not freeze the game the way the old synchronous
    // navAssistDebug logger did.
    class DriveAssistLogger
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

                filePath = dir + "/driveassist-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".log";
                writer = new StreamWriter(filePath, false) { AutoFlush = false };
                writer.WriteLine("# GTA11Y Drive Assist Debug Log");
                writer.WriteLine("# Session started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                writer.WriteLine("# One FRAME block per logged tick; EVENT lines mark discrete decisions.");
                writer.WriteLine("# ----------------------------------------------------------------");
                writer.Flush();

                running = true;
                writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "DriveAssistLogWriter"
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
