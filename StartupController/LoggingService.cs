using System;
using System.Diagnostics;
using System.IO;

namespace StartupController
{
    public static class LoggingService
    {
        private static readonly object _lock = new object();
        private static readonly string _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StartupController", "logs");
        private static readonly string _logFile = Path.Combine(_logDir, "startupcontroller.log");

        static LoggingService()
        {
            try
            {
                Directory.CreateDirectory(_logDir);
                AppendLine("INFO", "Logger", "Logger initialized");
            }
            catch
            {
                // ignore logging initialization failures
            }
        }

        private static void AppendLine(string level, string category, string message)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var line = $"{timestamp}\t{level}\t{category}\t{message}{Environment.NewLine}";
                lock (_lock)
                {
                    File.AppendAllText(_logFile, line);
                }
            }
            catch
            {
                // swallow - logging must not crash the app
            }
        }

        public static void LogInfo(string message)
        {
            AppendLine("INFO", "App", message);
        }

        public static void LogWarning(string message)
        {
            AppendLine("WARN", "App", message);
        }

        public static void LogError(string message, Exception? ex = null)
        {
            var details = ex == null ? message : message + " | Exception: " + ex.GetType().Name + ": " + ex.Message;
            AppendLine("ERROR", "App", details);
        }

        public static void LogLaunchResult(string programName, string programPath, bool success, string details = "")
        {
            var result = success ? "SUCCESS" : "FAILURE";
            var msg = $"{programName}\t{programPath}\t{result}\t{details}";
            AppendLine("LAUNCH", "Program", msg);
        }

        public static void StartSession(string? args = null)
        {
            var header = "=== New session started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (string.IsNullOrEmpty(args) ? "" : " | args: " + args) + " ===";
            AppendLine("SESSION", "App", header);
        }

        public static void OpenLogFile()
        {
            try
            {
                if (!File.Exists(_logFile))
                {
                    File.WriteAllText(_logFile, "");
                }
                // Open with default associated editor
                var psi = new ProcessStartInfo(_logFile)
                {
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch
            {
                // ignore
            }
        }
    }
}
