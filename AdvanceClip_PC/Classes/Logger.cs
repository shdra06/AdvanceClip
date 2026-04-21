using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace AdvanceClip.Classes
{
    public static class Logger
    {
        private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvanceClip", "Logs");
        private static readonly string LogFile = Path.Combine(LogDirectory, "activity_log.txt");
        
        // Async buffered logging — never blocks the UI thread
        private static readonly ConcurrentQueue<string> _buffer = new();
        private static Timer _flushTimer;
        private static readonly object _flushLock = new();

        static Logger()
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
            
            // Flush buffer to disk every 2 seconds on a background thread
            _flushTimer = new Timer(_ => FlushBuffer(), null, 2000, 2000);
        }

        public static void LogAction(string actionType, string details)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logEntry = $"[{timestamp}] [{actionType.ToUpper()}] {details}";
                
                // Enqueue — zero-allocation on the hot path, never blocks
                _buffer.Enqueue(logEntry);

                // Also push to the in-memory live monitor (lightweight, already on correct thread)
                NetworkActivityLog.Instance.Log(actionType.ToUpper(), details);
            }
            catch 
            {
                // Failsafe so logging doesn't crash the app
            }
        }

        private static void FlushBuffer()
        {
            if (_buffer.IsEmpty) return;
            
            // Drain all queued entries into a single write
            lock (_flushLock)
            {
                try
                {
                    using var writer = new StreamWriter(LogFile, append: true);
                    while (_buffer.TryDequeue(out string entry))
                    {
                        writer.WriteLine(entry);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Call on app shutdown to ensure all buffered logs are written.
        /// </summary>
        public static void Shutdown()
        {
            _flushTimer?.Dispose();
            FlushBuffer();
        }
    }
}
