using System.Text.RegularExpressions;

namespace StartupController
{
    internal static class CommandLineParser
    {
        // Split a registry "run" command into executable path and arguments
        internal static (string exePath, string args) Split(string command) => Split(command, File.Exists);

        // fileExists is the single source of truth for "does this path exist" (ProgramLauncher passes
        // IProcessStarter.FileExists). The current rules don't consult it yet; 3.1 uses it to resolve
        // unquoted paths with spaces.
        internal static (string exePath, string args) Split(string command, Func<string, bool> fileExists)
        {
            ArgumentNullException.ThrowIfNull(fileExists);

            if (string.IsNullOrWhiteSpace(command))
                return ("", "");

            command = command.Trim();

            // If starts with a quote, take the quoted part as the exe path
            if (command.StartsWith("\""))
            {
                var endQuote = command.IndexOf('"', 1);
                if (endQuote > 0)
                {
                    var exe = command.Substring(1, endQuote - 1);
                    var args = command.Substring(endQuote + 1).Trim();
                    return (exe, args);
                }
            }

            // Try to find a common executable extension (.exe, .bat, .cmd, .com, .lnk)
            var m = Regex.Match(command, "^(.+?\\.(exe|bat|cmd|com|lnk))(\\s+.*)?$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var exe = m.Groups[1].Value;
                var args = m.Groups[3].Success ? m.Groups[3].Value.Trim() : string.Empty;
                return (exe, args);
            }

            // Fallback: split on first space
            var idx = command.IndexOf(' ');
            if (idx > 0)
            {
                var exe = command.Substring(0, idx);
                var args = command.Substring(idx + 1).Trim();
                return (exe, args);
            }

            return (command, string.Empty);
        }
    }
}
