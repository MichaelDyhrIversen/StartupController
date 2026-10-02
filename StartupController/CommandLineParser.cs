namespace StartupController
{
    // Splits a Run command into executable and arguments. Pure: every file check goes through fileExists
    // (ProgramLauncher passes IProcessStarter.FileExists), and only fully qualified paths are ever probed.
    internal static class CommandLineParser
    {
        // Extensions that end the executable part of an unquoted command
        internal static readonly string[] ExecutableExtensions = { ".exe", ".com", ".bat", ".cmd", ".lnk" };

        // Backstop against unbounded file probing (a UNC probe is a network round trip)
        internal const int MaxProbes = 32;

        internal readonly record struct Parsed(string ExePath, string Arguments, bool UnquotedWithSpaces);

        // Split a registry "run" command into executable path and arguments (environment variables expanded)
        internal static (string exePath, string args) Split(string command, Func<string, bool> fileExists)
        {
            var parsed = Parse(command, fileExists, expandEnvironment: true);
            return (parsed.ExePath, parsed.Arguments);
        }

        // Rules:
        // - %VAR% references are expanded first, unless the caller already expanded them (REG_EXPAND_SZ values are
        //   expanded once when the Run value is read, see RunFingerprint.FromRunValue).
        // - "quoted exe" args: the quoted part is the exe. When it has no extension and only "<exe>.exe" exists,
        //   that file is used. An unmatched leading quote is dropped and the rest is treated as unquoted, except
        //   that when no executable token is found the whole rest is the exe (it is never split at a space).
        // - Unquoted: split points are scanned SHORTEST first, and the first prefix whose extension is an
        //   executable one (.exe .com .bat .cmd .lnk) is the exe, whether it exists or not (the launcher reports
        //   NotFound). No other prefix is probed, so neither a planted "app.exe --load x.js" nor a planted
        //   "C:\Program.exe" can take over the command.
        // - Only when no token has an executable extension is the ".exe" probe applied, and only to the whole
        //   string. Otherwise the first token is the exe and the rest are arguments.
        internal static Parsed Parse(string command, Func<string, bool> fileExists, bool expandEnvironment)
        {
            ArgumentNullException.ThrowIfNull(fileExists);

            if (string.IsNullOrWhiteSpace(command))
                return new Parsed("", "", false);

            int probes = 0;
            bool Probe(string path)
            {
                if (probes >= MaxProbes || !IsFullyQualified(path)) return false;
                probes++;
                return fileExists(path);
            }

            if (expandEnvironment)
                command = Environment.ExpandEnvironmentVariables(command);
            command = command.Trim();

            bool unmatchedQuote = false;
            if (command.StartsWith('"'))
            {
                var endQuote = command.IndexOf('"', 1);
                if (endQuote > 0)
                {
                    var exe = command.Substring(1, endQuote - 1).Trim();
                    var args = command.Substring(endQuote + 1).Trim();
                    return new Parsed(WithExeProbe(exe, Probe) ?? exe, args, false);
                }

                unmatchedQuote = true;
                command = command.Substring(1).Trim();
                if (command.Length == 0)
                    return new Parsed("", "", false);
            }

            // Shortest first: stop at the first prefix that ends in an executable extension. A candidate is the
            // command up to a space (or the end) with trailing whitespace trimmed; its extension is checked after
            // also trimming dots and spaces, as HasExecutableExtension does. One pass, no allocation per candidate.
            int trimmedLength = 0;     // length of command[..i].TrimEnd()
            int dotTrimmedLength = 0;  // length of command[..trimmedLength].TrimEnd('.', ' ')
            int noDotSpaceLength = 0;  // length of command[..i].TrimEnd('.', ' ')
            for (int i = 0; i <= command.Length; i++)
            {
                if (i == command.Length || command[i] == ' ')
                {
                    if (trimmedLength > 0 && EndsWithExecutableExtension(command.AsSpan(0, dotTrimmedLength)))
                    {
                        var candidate = command.Substring(0, trimmedLength);
                        var args = command.Substring(i).Trim();
                        return new Parsed(candidate, args, candidate.Contains(' '));
                    }
                    if (i == command.Length) break;
                }

                char c = command[i];
                if (c != '.' && c != ' ')
                    noDotSpaceLength = i + 1;
                if (!char.IsWhiteSpace(c))
                {
                    trimmedLength = i + 1;
                    dotTrimmedLength = noDotSpaceLength;
                }
            }

            // No executable token: only the whole string may get ".exe" (as CreateProcess would add it)
            var whole = WithExeProbe(command, Probe);
            if (whole != null && whole != command)
                return new Parsed(whole, "", command.Contains(' '));

            if (unmatchedQuote)
                return new Parsed(command, "", false);

            // Split off arguments only after a bare name ("helper /x", resolved by the shell). A path is kept whole so
            // it ends up NotFound: "D:\Apps v2\tool" must not become a planted "D:\Apps".
            var firstSpace = command.IndexOf(' ');
            if (firstSpace > 0 && IsBareFileName(command.Substring(0, firstSpace)))
                return new Parsed(command.Substring(0, firstSpace), command.Substring(firstSpace + 1).Trim(), false);

            return new Parsed(command, "", false);
        }

        // For a path without an extension, path + ".exe" first and then the path itself (CreateProcess order);
        // otherwise the path itself when it exists; else null.
        // Never probes a path that ends in '.' or ' ' (Windows would strip those, so "x." + ".exe" is not "x.exe").
        internal static string? WithExeProbe(string path, Func<string, bool> fileExists)
        {
            if (path.Length == 0 || EndsWithDotOrSpace(path)) return null;
            if (!HasExtension(path) && fileExists(path + ".exe")) return path + ".exe";
            if (fileExists(path)) return path;
            return null;
        }

        // A plain file name the shell may resolve: no directory, drive, URL scheme, quote or space
        internal static bool IsBareFileName(string path)
        {
            return path.Length > 0 && path.IndexOfAny(new[] { '\\', '/', ':', ' ', '"' }) < 0;
        }

        // Extension check that ignores trailing dots and spaces (Windows strips them: "x.exe." runs x.exe)
        internal static bool HasExecutableExtension(string path)
        {
            string extension;
            try
            {
                extension = System.IO.Path.GetExtension(path.TrimEnd('.', ' '));
            }
            catch (ArgumentException)
            {
                return false;
            }
            return ExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        // Same answer as Path.GetExtension(path) being an executable extension, for a path already trimmed of
        // trailing dots and spaces: every executable extension is a dot plus letters, so it is the path's ending
        private static bool EndsWithExecutableExtension(ReadOnlySpan<char> path)
        {
            foreach (var extension in ExecutableExtensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static bool EndsWithDotOrSpace(string path) => path.EndsWith('.') || path.EndsWith(' ');

        internal static bool HasExtension(string path)
        {
            try
            {
                return System.IO.Path.HasExtension(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        internal static bool IsFullyQualified(string path)
        {
            try
            {
                return System.IO.Path.IsPathFullyQualified(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
