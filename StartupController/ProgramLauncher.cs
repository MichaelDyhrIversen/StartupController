using System.ComponentModel;
using System.Diagnostics;

namespace StartupController
{
    public interface IProgramLauncher
    {
        LaunchResult Launch(StartupProgram program);

        // --launch at logon: like Launch, but a program that needs elevation is not started (no UAC prompt at logon,
        // as Explorer does for Run entries). No default implementation: every launcher must decide (fail closed).
        LaunchResult LaunchAtLogon(StartupProgram program);
    }

    // Outcome of a single launch attempt.
    // - NotFound: no executable could be determined, or it doesn't exist and may not be shell-started.
    // - Blocked: the entry starts StartupController itself and is never launched (3.1a).
    // - ExePath: the parsed executable as ProgramLauncher.LoggableExe shows it (never the arguments), when one was
    //   determined. It is what gets logged.
    public sealed record LaunchResult(bool Success, bool NotFound = false, string? Error = null, bool Blocked = false, string? ExePath = null)
    {
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
        internal const int MaxErrorLength = 512;     // exception text kept in LaunchResult.Error
        private const int MinRedactedLength = 4;      // shorter values ("-y", "/s") would mangle ordinary text
        private const int ERROR_ELEVATION_REQUIRED = 740;

        private readonly IProcessStarter _starter;
        private readonly string _selfExePath;
        private readonly Func<string, string> _normalizePath;
        private readonly Func<string, string, bool> _sameFile;
        private readonly Func<string, bool> _shortcutRunsAsAdmin;

        // selfExePath: this app's executable. normalizePath: full path with 8.3 short names expanded.
        // sameFile: true when two paths are the same file on disk (hardlinks, subst, UNC aliases). Tests fake both.
        // shortcutRunsAsAdmin: true when a .lnk has "Run as administrator" set (default: ShortcutRunsAsAdmin).
        public ProgramLauncher(IProcessStarter starter, string selfExePath, Func<string, string>? normalizePath = null, Func<string, string, bool>? sameFile = null,
            Func<string, bool>? shortcutRunsAsAdmin = null)
        {
            _starter = starter ?? throw new ArgumentNullException(nameof(starter));
            _selfExePath = selfExePath ?? "";
            _normalizePath = normalizePath ?? PathHelper.NormalizePath;
            _sameFile = sameFile ?? PathHelper.IsSameFile;
            _shortcutRunsAsAdmin = shortcutRunsAsAdmin ?? ShortcutRunsAsAdmin;
        }

        public LaunchResult Launch(StartupProgram program) => Launch(program, allowElevation: true);

        public LaunchResult LaunchAtLogon(StartupProgram program) => Launch(program, allowElevation: false);

        private LaunchResult Launch(StartupProgram program, bool allowElevation)
        {
            string? exePath = null;
            string? shownExe = null;  // what may be logged or shown for exePath (see LoggableExe)
            string arguments = "";
            try
            {
                if (program.Path.Length > MaxCommandLength)
                    return Reject(program, "command is too long", "Command is too long");

                // REG_EXPAND_SZ paths were expanded when read; expand REG_SZ ones here, never twice
                var parsed = CommandLineParser.Parse(program.Path, _starter.FileExists, expandEnvironment: !program.PathExpanded);
                exePath = parsed.ExePath;
                arguments = parsed.Arguments;

                if (string.IsNullOrEmpty(exePath))
                    return new LaunchResult(false, NotFound: true, Error: "Executable path could not be determined from entry.");

                shownExe = LoggableExe(exePath);

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
                    LoggingService.LogWarning($"'{program.Name}': unquoted path with spaces, resolved to '{shownExe}'");

                if (CommandLineParser.IsFullyQualified(exePath))
                {
                    if (!_starter.FileExists(exePath))
                        return new LaunchResult(false, NotFound: true, Error: $"Executable not found: {shownExe}", ExePath: shownExe);

                    if (!StartExisting(program, exePath, parsed.Arguments, allowElevation))
                        return new LaunchResult(false, Error: "Requires elevation, not started at logon", ExePath: shownExe);
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
                    return new LaunchResult(false, NotFound: true, Error: $"Executable not found (not a full path): {shownExe}", ExePath: shownExe);
                }

                return new LaunchResult(true, ExePath: shownExe);
            }
            catch (FileNotFoundException fnf)
            {
                return new LaunchResult(false, NotFound: true, Error: SafeErrorText(fnf.Message, program.Path, exePath, shownExe, arguments), ExePath: shownExe);
            }
            catch (Exception ex)
            {
                return new LaunchResult(false, Error: SafeErrorText(ex.Message, program.Path, exePath, shownExe, arguments), ExePath: shownExe);
            }
        }

        // The executable as it may be logged or shown. The parser keeps a command whole when it finds no executable
        // token ("C:\Tools\mytool --token=x", "C:\Tools\sync --key=x.cmd"), so a parsed exe with whitespace that
        // doesn't exist may hold arguments: only its first token is kept, plus the length of the rest.
        // An existing file is shown in full (its name is a real path, not arguments).
        internal string LoggableExe(string exePath)
        {
            int whitespace = IndexOfWhitespace(exePath);
            if (whitespace < 0) return exePath;
            if (CommandLineParser.IsFullyQualified(exePath) && SafeExists(exePath)) return exePath;
            return $"{exePath.Substring(0, whitespace)} <+{exePath.Length - whitespace} chars>";
        }

        // Defence in depth for exception text: .NET's start errors name the exe and working directory, not the
        // arguments, but a message that echoes the command, the unshown part of the exe or the arguments is redacted.
        // Also capped in length.
        internal static string SafeErrorText(string message, string command, string? exePath, string? shownExe, string arguments)
        {
            var text = message ?? "";
            text = Redact(text, command, "<command>");
            if (exePath != null && shownExe != null && exePath != shownExe)
                text = Redact(text, exePath, shownExe);
            text = Redact(text, arguments, "<arguments>");
            return text.Length > MaxErrorLength ? text.Substring(0, MaxErrorLength) + "..." : text;
        }

        private static string Redact(string text, string secret, string replacement) =>
            secret.Trim().Length < MinRedactedLength ? text : text.Replace(secret, replacement, StringComparison.OrdinalIgnoreCase);

        private static int IndexOfWhitespace(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i])) return i;
            }
            return -1;
        }

        private bool SafeExists(string path)
        {
            try
            {
                return _starter.FileExists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static LaunchResult Reject(StartupProgram program, string why, string error)
        {
            LoggingService.LogWarning($"Not launching '{program.Name}': {why}");
            return new LaunchResult(false, Error: error);
        }

        // .exe/.com: started directly by full path (CreateProcess, no search). If Windows needs elevation, the shell
        // is used so the UAC prompt appears, unless allowElevation is false (--launch at logon): then the program is
        // not started and false is returned. Other types (.bat, .cmd, .lnk, ...) go through the shell.
        private bool StartExisting(StartupProgram program, string exePath, string arguments, bool allowElevation)
        {
            var extension = Path.GetExtension(exePath);
            bool direct = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".com", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = !direct,
                WorkingDirectory = Path.GetDirectoryName(exePath),
                Arguments = arguments
            };
            // Arguments may hold secrets (tokens, passwords): log their length only
            LoggingService.LogInfo($"Process start: Exe='{psi.FileName}' Args=<{psi.Arguments.Length} chars> WorkingDir='{psi.WorkingDirectory}' Shell={psi.UseShellExecute}");

            if (!direct && !allowElevation && extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && _shortcutRunsAsAdmin(exePath))
            {
                // The shell would show a UAC prompt for it at logon
                LoggingService.LogWarning($"'{program.Name}' requires elevation, not started");
                return false;
            }

            if (!direct)
            {
                Start(psi);
                return true;
            }

            try
            {
                Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_ELEVATION_REQUIRED)
            {
                if (!allowElevation)
                {
                    LoggingService.LogWarning($"'{program.Name}' requires elevation, not started");
                    return false;
                }
                LoggingService.LogInfo($"'{exePath}' requires elevation; starting through the shell");
                psi.UseShellExecute = true;
                Start(psi);
            }
            return true;
        }

        // MS-SHLLINK header: HeaderSize 0x4C, LinkCLSID 00021401-0000-0000-C000-000000000046, then LinkFlags (little-endian
        // UInt32 at offset 20). RunAsUser (0x2000, SLDF_RUNAS_USER) is "Run as administrator" in the shortcut's
        // properties. Only the 24 header bytes are read; a file that can't be read or isn't a shell link gives false
        // (the shell then decides, as before). A target exe that requires elevation by its manifest is not detected.
        private const uint SLDF_RUNAS_USER = 0x00002000;
        private static readonly Guid ShellLinkClsid = new Guid("00021401-0000-0000-C000-000000000046");

        internal static bool ShortcutRunsAsAdmin(string lnkPath)
        {
            try
            {
                var header = new byte[24];
                using (var stream = new FileStream(lnkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int read = 0;
                    while (read < header.Length)
                    {
                        int n = stream.Read(header, read, header.Length - read);
                        if (n == 0) return false;
                        read += n;
                    }
                }
                if (BitConverter.ToUInt32(header, 0) != 0x4C) return false;
                if (new Guid(new ReadOnlySpan<byte>(header, 4, 16)) != ShellLinkClsid) return false;
                return (BitConverter.ToUInt32(header, 20) & SLDF_RUNAS_USER) != 0;
            }
            catch (Exception)
            {
                return false;
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
            var trimmed = PathHelper.StripDevicePrefix(exePath.Trim()).TrimEnd('.', ' ');
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

            var self = _selfExePath.Length == 0 ? "" : SafeNormalize(PathHelper.StripDevicePrefix(_selfExePath));
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

        // No alternate data stream (a ':' anywhere but after the drive letter) and no trailing dot or space
        internal static bool IsWellFormedExePath(string exePath)
        {
            var path = PathHelper.StripDevicePrefix(exePath);
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
    }
}
