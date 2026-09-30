using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace StartupController
{
    public interface IProgramLauncher
    {
        LaunchResult Launch(StartupProgram program);
    }

    // Outcome of a single launch attempt.
    // - NotFound: no executable could be determined, or it doesn't exist and may not be shell-started.
    // - Blocked: the entry starts StartupController itself and is never launched (3.1a).
    public sealed record LaunchResult(bool Success, bool NotFound = false, string? Error = null, bool Blocked = false)
    {
        public static readonly LaunchResult Ok = new(true);

        public static readonly LaunchResult BlockedSelf = new(false, Error: "This entry starts StartupController itself", Blocked: true);
    }

    // Parses, resolves and starts one Run command. Only this class decides what gets started:
    // - the parsed executable, never the raw command string;
    // - only fully qualified paths are checked for existence; a bare name is left to the shell with the system
    //   directory as working directory (Program.Main also makes it the current directory), so the directory the
    //   app was started from is never searched;
    // - an existing .exe/.com is started directly (no shell, no search, no file association); the shell is used
    //   only when Windows says it needs elevation, and for .bat/.cmd/.lnk;
    // - a missing executable is shell-started only when it is a bare file name without spaces
    //   (e.g. "rundll32.exe", resolved by the shell through PATH/App Paths). Anything else is NotFound;
    // - StartupController itself is never started, whatever the Run value name (Blocked).
    public sealed class ProgramLauncher : IProgramLauncher
    {
        internal const string SelfFileName = "StartupController.exe";
        internal const int MaxCommandLength = 32767; // CreateProcess limit
        private const int ERROR_ELEVATION_REQUIRED = 740;

        private readonly IProcessStarter _starter;
        private readonly string _selfExePath;
        private readonly Func<string, string> _normalizePath;
        private readonly Func<string, string, bool> _sameFile;

        public ProgramLauncher(IProcessStarter starter)
            : this(starter, Application.ExecutablePath)
        {
        }

        // selfExePath: this app's executable. normalizePath: full path with 8.3 short names expanded.
        // sameFile: true when two paths are the same file on disk (hardlinks, subst, UNC aliases). Tests fake both.
        public ProgramLauncher(IProcessStarter starter, string selfExePath, Func<string, string>? normalizePath = null, Func<string, string, bool>? sameFile = null)
        {
            _starter = starter ?? throw new ArgumentNullException(nameof(starter));
            _selfExePath = selfExePath ?? "";
            _normalizePath = normalizePath ?? NormalizePath;
            _sameFile = sameFile ?? IsSameFile;
        }

        public LaunchResult Launch(StartupProgram program)
        {
            try
            {
                if (program.Path.Length > MaxCommandLength)
                    return Reject(program, "command is too long", "Command is too long");

                // REG_EXPAND_SZ paths were expanded when read; expand REG_SZ ones here, never twice
                var parsed = CommandLineParser.Parse(program.Path, _starter.FileExists, expandEnvironment: !program.PathExpanded);
                var exePath = parsed.ExePath;

                if (string.IsNullOrEmpty(exePath))
                    return new LaunchResult(false, NotFound: true, Error: "Executable path could not be determined from entry.");

                if (exePath.Length + parsed.Arguments.Length > MaxCommandLength)
                    return Reject(program, "command is too long after expansion", "Command is too long");

                // Before any Start, including the shell fallback. Logs the name only, never the command.
                if (IsSelf(exePath))
                {
                    LoggingService.LogWarning($"Blocked '{program.Name}': it starts StartupController itself");
                    return LaunchResult.BlockedSelf;
                }

                if (!IsWellFormedExePath(exePath))
                    return Reject(program, "invalid executable path (stream name, or trailing dot or space)", "Invalid executable path");

                if (parsed.UnquotedWithSpaces)
                    LoggingService.LogWarning($"'{program.Name}': unquoted path with spaces, resolved to '{exePath}'");

                if (CommandLineParser.IsFullyQualified(exePath))
                {
                    if (!_starter.FileExists(exePath))
                        return new LaunchResult(false, NotFound: true, Error: $"Executable not found: {exePath}");

                    StartExisting(exePath, parsed.Arguments);
                }
                else if (CommandLineParser.IsBareFileName(exePath))
                {
                    // Let the shell resolve a bare name (PATH, App Paths); only the parsed exe and args are passed.
                    // The shell searches the working directory first, so make it the system directory.
                    var psi = new ProcessStartInfo(exePath)
                    {
                        UseShellExecute = true,
                        Arguments = parsed.Arguments,
                        WorkingDirectory = Environment.SystemDirectory
                    };
                    LoggingService.LogWarning($"'{program.Name}': '{exePath}' is a bare name; shell start");
                    Start(psi);
                }
                else
                {
                    // Relative paths (sub\app.exe, ..\x, C:app.exe, \app.exe) would resolve against the current directory
                    return new LaunchResult(false, NotFound: true, Error: $"Executable not found (not a full path): {exePath}");
                }

                return LaunchResult.Ok;
            }
            catch (FileNotFoundException fnf)
            {
                return new LaunchResult(false, NotFound: true, Error: fnf.Message);
            }
            catch (Exception ex)
            {
                return new LaunchResult(false, Error: ex.Message);
            }
        }

        private static LaunchResult Reject(StartupProgram program, string why, string error)
        {
            LoggingService.LogWarning($"Not launching '{program.Name}': {why}");
            return new LaunchResult(false, Error: error);
        }

        // .exe/.com: started directly by full path (CreateProcess, no search). If Windows needs elevation, the shell
        // is used so the UAC prompt appears. Other types (.bat, .cmd, .lnk, ...) go through the shell.
        private void StartExisting(string exePath, string arguments)
        {
            var extension = Path.GetExtension(exePath);
            bool direct = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".com", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = !direct,
                WorkingDirectory = Path.GetDirectoryName(exePath),
                Arguments = arguments
            };
            LoggingService.LogInfo($"Process start: Exe='{psi.FileName}' Args='{psi.Arguments}' WorkingDir='{psi.WorkingDirectory}' Shell={psi.UseShellExecute}");

            if (!direct)
            {
                Start(psi);
                return;
            }

            try
            {
                Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_ELEVATION_REQUIRED)
            {
                LoggingService.LogInfo($"'{exePath}' requires elevation; starting through the shell");
                psi.UseShellExecute = true;
                Start(psi);
            }
        }

        // The Process handle isn't needed after the start; dispose it right away
        private void Start(ProcessStartInfo psi) => _starter.Start(psi)?.Dispose();

        // True when the parsed executable is StartupController:
        // - any file named StartupController.exe, or an extensionless "StartupController" (trailing dots and spaces
        //   ignored, as Windows ignores them);
        // - the same path as this app after normalization (device prefixes, relative segments, 8.3 names);
        //   a bare name is also checked as if it sat next to this app;
        // - the same file on disk as this app (hardlinks, subst, UNC aliases), when both exist.
        // Only the executable is checked; arguments are not scanned.
        internal bool IsSelf(string exePath)
        {
            var trimmed = StripDevicePrefix(exePath.Trim()).TrimEnd('.', ' ');
            if (trimmed.Length == 0) return false;

            var name = SafeFileName(trimmed);
            bool noExtension = !CommandLineParser.HasExtension(trimmed);
            if (IsSelfName(name) || (noExtension && string.Equals(name, Path.GetFileNameWithoutExtension(SelfFileName), StringComparison.OrdinalIgnoreCase)))
                return true;

            var candidates = new List<string>();
            if (CommandLineParser.IsFullyQualified(trimmed))
            {
                candidates.Add(trimmed);
                if (noExtension && _starter.FileExists(trimmed + ".exe"))
                    candidates.Add(trimmed + ".exe");
            }
            else if (CommandLineParser.IsBareFileName(trimmed))
            {
                var nextToSelf = SafeCombine(AppContext.BaseDirectory, trimmed);
                if (nextToSelf != null)
                {
                    candidates.Add(nextToSelf);
                    if (noExtension) candidates.Add(nextToSelf + ".exe");
                }
            }

            var self = _selfExePath.Length == 0 ? "" : SafeNormalize(StripDevicePrefix(_selfExePath));
            foreach (var candidate in candidates)
            {
                var normalized = SafeNormalize(candidate);
                if (IsSelfName(SafeFileName(normalized)))
                    return true;
                if (self.Length > 0 && string.Equals(normalized, self, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (self.Length > 0 && SafeSameFile(candidate, self))
                    return true;
            }
            return false;
        }

        private static bool IsSelfName(string fileName) =>
            string.Equals(fileName.TrimEnd('.', ' '), SelfFileName, StringComparison.OrdinalIgnoreCase);

        // "\\?\C:\x" -> "C:\x", "\\?\UNC\srv\share" -> "\\srv\share", "\\.\C:\x" -> "C:\x"
        internal static string StripDevicePrefix(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                return path.Substring(4);
            return path;
        }

        // No alternate data stream (a ':' anywhere but after the drive letter) and no trailing dot or space
        internal static bool IsWellFormedExePath(string exePath)
        {
            var path = StripDevicePrefix(exePath);
            int start = path.Length >= 2 && path[1] == ':' ? 2 : 0;
            if (path.IndexOf(':', start) >= 0) return false;
            return !CommandLineParser.EndsWithDotOrSpace(path);
        }

        private string SafeNormalize(string path)
        {
            try
            {
                return _normalizePath(path);
            }
            catch (Exception)
            {
                return path; // an invalid path can't match; compare it as written
            }
        }

        // Fail safe: an identity check that throws counts as "not the same file"; the other checks still apply
        private bool SafeSameFile(string a, string b)
        {
            try
            {
                return _sameFile(a, b);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string? SafeCombine(string directory, string name)
        {
            try
            {
                return Path.Combine(directory, name);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static string SafeFileName(string path)
        {
            try
            {
                return Path.GetFileName(path);
            }
            catch (ArgumentException)
            {
                return path;
            }
        }

        // Full path with 8.3 short names expanded (GetLongPathNameW works only for paths that exist)
        internal static string NormalizePath(string path)
        {
            var full = Path.GetFullPath(path);
            var buffer = new StringBuilder(1024);
            uint length = GetLongPathNameW(full, buffer, (uint)buffer.Capacity);
            if (length > buffer.Capacity)
            {
                buffer = new StringBuilder((int)length);
                length = GetLongPathNameW(full, buffer, (uint)buffer.Capacity);
            }
            return length > 0 && length <= buffer.Capacity ? buffer.ToString() : full;
        }

        // Same volume serial number and file index: the same file, whatever path reaches it
        internal static bool IsSameFile(string a, string b)
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            if (!TryGetFileId(a, out var idA) || !TryGetFileId(b, out var idB)) return false;
            return idA == idB;
        }

        private static bool TryGetFileId(string path, out (uint Volume, uint High, uint Low) id)
        {
            id = default;
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info)) return false;
            id = (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
            return true;
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
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);
    }
}
