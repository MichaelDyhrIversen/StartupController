using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StartupController.Uninstall
{
    // Where the helper reports what it did. Names only: never commands, paths or byte values.
    internal interface IUninstallLog
    {
        void Info(string text);
        void Warning(string text);
        void Error(string text);
    }

    // Writes nothing (elevated or SYSTEM token, see UninstallLog.For)
    internal sealed class NullUninstallLog : IUninstallLog
    {
        internal static readonly NullUninstallLog Instance = new NullUninstallLog();

        public void Info(string text) { }
        public void Warning(string text) { }
        public void Error(string text) { }
    }

    // Appends to %LOCALAPPDATA%\StartupController\logs\uninstall.log (the custom action impersonates the uninstalling
    // user, so this is that user's folder). A separate file, so the helper doesn't depend on LoggingService.
    // Format: "yyyy-MM-dd HH:mm:ss.fff [LEVEL] text", text escaped like LoggingService (LogEscape).
    // The helper may run with an elevated token in a folder the user controls, so a line is written only when no
    // folder on the path and not the file itself is a reparse point (junction, symlink), and the opened file has
    // exactly one link (no hardlink to another file). Opened for append, shared for reading only.
    // A log that can't be written (or fails these checks) is ignored.
    internal sealed class UninstallLog : IUninstallLog
    {
        // Never throws on invalid UTF-16 (LogEscape already escapes lone surrogates)
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        private readonly string _path;

        internal UninstallLog(string path)
        {
            _path = path;
        }

        // No file log at all while the token is elevated or SYSTEM: an elevated append into a folder the user controls
        // could be redirected (link races the checks in Append can't fully close). Uninstall non-elevated to get a log.
        internal static IUninstallLog For(IHelperEnvironment environment, string path) =>
            environment.IsElevated || environment.IsSystem ? NullUninstallLog.Instance : new UninstallLog(path);

        internal static string DefaultPath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "StartupController", "logs", "uninstall.log");
        }

        public void Info(string text) => Append("INFO", text);

        public void Warning(string text) => Append("WARN", text);

        public void Error(string text) => Append("ERROR", text);

        private void Append(string level, string text)
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
                if (string.IsNullOrEmpty(directory) || HasReparsePoint(directory!)) return;
                Directory.CreateDirectory(directory);
                if (HasReparsePoint(directory!)) return;
                if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0) return;

                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                    + " [" + level + "] " + LogEscape.Escape(text) + Environment.NewLine;
                var bytes = Utf8.GetBytes(line);
                using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    if (LinkCount(stream.SafeFileHandle) != 1) return;
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch
            {
                // ignored: logging must never fail the uninstall
            }
        }

        // True when the folder or any existing parent up to the drive root is a reparse point
        internal static bool HasReparsePoint(string directory)
        {
            for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION information);

        // 0 when it can't be determined, which refuses the write
        private static uint LinkCount(SafeFileHandle handle) =>
            GetFileInformationByHandle(handle, out var information) ? information.NumberOfLinks : 0;
    }
}
