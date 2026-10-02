using System.Diagnostics;
using System.Text;

namespace StartupController
{
    // Tab-separated log: timestamp, level, category, then one or more fields.
    // Every field is escaped (see Escape), so a registry value can't forge or split a log line.
    // Command-line arguments are never logged: they may hold tokens or passwords.
    public static class LoggingService
    {
        // Size-based rotation: startupcontroller.log -> startupcontroller.1.log -> startupcontroller.2.log
        internal const long MaxLogBytes = 1024 * 1024;
        internal const int KeptLogFiles = 3;

        // After a failed rotation (e.g. a rotated file is open in an editor) the next attempt waits this long, or
        // until the log has doubled in size, instead of being retried on every line
        internal static readonly TimeSpan RotationRetryDelay = TimeSpan.FromMinutes(1);

        // Never throws on invalid UTF-16. File.AppendAllText's default UTF-8 encoding throws EncoderFallbackException
        // on a lone surrogate, which dropped the whole line. Escape already writes those as \uXXXX; this is the backstop.
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        private static readonly object _lock = new object();
        private static string _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StartupController", "logs");
        private static string _logFile = Path.Combine(_logDir, "startupcontroller.log");
        private static bool _initialized;
        private static DateTime _rotationRetryAfterUtc = DateTime.MinValue;
        private static long _rotationRetryAtBytes;

        // Clock for the rotation backoff (tests move it forward)
        internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        // Point the logger at another directory (tests use a temp folder). Call before the first log line
        // so nothing is written to the default location.
        internal static void Initialize(string logDirectory)
        {
            lock (_lock)
            {
                _logDir = logDirectory;
                _logFile = Path.Combine(_logDir, "startupcontroller.log");
                _initialized = false;
                _rotationRetryAfterUtc = DateTime.MinValue;
                _rotationRetryAtBytes = 0;
            }
            EnsureInitialized();
        }

        internal static string LogFilePath
        {
            get { lock (_lock) { return _logFile; } }
        }

        // Create the log directory and write the header once, on first use.
        private static void EnsureInitialized()
        {
            // Monitor is re-entrant, so the AppendLine call below can take the same lock
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;

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
        }

        private static void AppendLine(string level, string category, params string?[] fields)
        {
            EnsureInitialized();
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var line = $"{timestamp}\t{level}\t{category}\t{string.Join("\t", fields.Select(Escape))}{Environment.NewLine}";
                lock (_lock)
                {
                    RotateWithBackoff();
                    try
                    {
                        File.AppendAllText(_logFile, line, Utf8);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // The logs folder was deleted while the app runs: recreate it and retry once
                        Directory.CreateDirectory(_logDir);
                        File.AppendAllText(_logFile, line, Utf8);
                    }
                }
            }
            catch
            {
                // swallow - logging must not crash the app
            }
        }

        // Keeps a field on one line and unambiguous (see LogEscape, shared with the uninstall helper's log)
        internal static string Escape(string? value) => LogEscape.Escape(value);

        // Rotation for AppendLine (call under _lock). A failed rotation is not retried on every line: the next attempt
        // waits RotationRetryDelay, or until the log has doubled since the failure. The write goes ahead either way.
        private static void RotateWithBackoff()
        {
            long length;
            try
            {
                var info = new FileInfo(_logFile);
                length = info.Exists ? info.Length : 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Log size check failed: " + ex.Message);
                return;
            }
            if (length < MaxLogBytes) return;

            var now = UtcNow();
            if (now < _rotationRetryAfterUtc && length < _rotationRetryAtBytes) return;

            if (RotateIfNeeded(_logFile, MaxLogBytes, KeptLogFiles))
            {
                _rotationRetryAfterUtc = DateTime.MinValue;
                _rotationRetryAtBytes = 0;
            }
            else
            {
                _rotationRetryAfterUtc = now + RotationRetryDelay;
                _rotationRetryAtBytes = length * 2;
            }
        }

        // When the log has reached maxBytes, shift it to .1, .1 to .2, ... and drop the oldest, so at most
        // `keep` files exist. Returns true when it rotated. Failures are swallowed (the write still goes ahead).
        internal static bool RotateIfNeeded(string logFile, long maxBytes, int keep)
        {
            try
            {
                var info = new FileInfo(logFile);
                if (!info.Exists || info.Length < maxBytes) return false;

                for (int i = keep - 1; i >= 1; i--)
                {
                    var source = i == 1 ? logFile : RotatedPath(logFile, i - 1);
                    if (File.Exists(source))
                        File.Move(source, RotatedPath(logFile, i), overwrite: true);
                }
                if (keep <= 1)
                    File.Delete(logFile);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Log rotation failed: " + ex.Message);
                return false;
            }
        }

        // "C:\logs\startupcontroller.log", 2 -> "C:\logs\startupcontroller.2.log"
        internal static string RotatedPath(string logFile, int index)
        {
            var directory = Path.GetDirectoryName(logFile) ?? "";
            return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(logFile)}.{index}{Path.GetExtension(logFile)}");
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

        // exePath is the parsed executable (LaunchResult.ExePath), never the Run command with its arguments
        public static void LogLaunchResult(string programName, string? exePath, bool success, string details = "")
        {
            AppendLine("LAUNCH", "Program", programName, exePath ?? "", success ? "SUCCESS" : "FAILURE", details);
        }

        // Logs only whether --launch was given; other arguments are not logged
        public static void StartSession(IEnumerable<string>? args = null)
        {
            var asmVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
            bool launch = args?.Contains("--launch") ?? false;
            var header = "=== New session started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                       + " | version: " + asmVersion
                       + " | --launch: " + (launch ? "yes" : "no") + " ===";
            AppendLine("SESSION", "App", header);
        }

        // Opens the log in the default editor through the process seam. Returns false (logged) when it can't.
        public static bool OpenLogFile(IProcessStarter starter, out string? error)
        {
            ArgumentNullException.ThrowIfNull(starter);
            EnsureInitialized();
            var logFile = LogFilePath;
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(logFile, "", Utf8); // creates the file if it is missing, keeps it otherwise
                }
                starter.Start(new ProcessStartInfo(logFile) { UseShellExecute = true })?.Dispose();
                LogInfo("Log file opened");
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not open the log file: " + ex);
                LogError("Could not open the log file", ex);
                error = ex.Message;
                return false;
            }
        }
    }
}
