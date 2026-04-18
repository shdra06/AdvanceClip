using System;
using System.IO;

namespace AdvanceClip.Classes
{
    public static class Logger
    {
        private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvanceClip", "Logs");
        private static readonly string LogFile = Path.Combine(LogDirectory, "activity_log.txt");

        static Logger()
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
        }

        public static void LogAction(string actionType, string details)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logEntry = $"[{timestamp}] [{actionType.ToUpper()}] {details}{Environment.NewLine}";
                File.AppendAllText(LogFile, logEntry);

                // Also push to the in-memory live monitor
                NetworkActivityLog.Instance.Log(actionType.ToUpper(), details);
            }
            catch 
            {
                // Failsafe so logging doesn't crash the app
            }
        }
    }
}
