using System.Text;
using System.Text.RegularExpressions;

namespace StartupController.Tests.Infrastructure
{
    /// <summary>
    /// Roslyn-free source scanning for guard tests. Works on the test project and on production source.
    /// Comments are stripped before matching, so a pattern mentioned in a comment is not a violation.
    /// </summary>
    internal static class SourceScan
    {
        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "StartupController.sln")))
                dir = dir.Parent;
            if (dir == null)
                throw new InvalidOperationException("Could not locate the repository root (StartupController.sln)");
            return dir.FullName;
        }

        public static string TestSourceDirectory() => Path.Combine(RepoRoot(), "StartupController.Tests");

        public static string ProductionSourceDirectory() => Path.Combine(RepoRoot(), "StartupController");

        /// <summary>All .cs files below the directory, excluding bin/ and obj/.</summary>
        public static IEnumerable<string> SourceFiles(string directory)
        {
            return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var relative = Path.GetRelativePath(directory, f);
                    var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return !parts.Contains("bin", StringComparer.OrdinalIgnoreCase)
                        && !parts.Contains("obj", StringComparer.OrdinalIgnoreCase);
                });
        }

        /// <summary>Source text with // and /* */ comments removed (string and char literals are kept).</summary>
        public static string ReadCode(string file) => StripComments(File.ReadAllText(file));

        /// <summary>"file: why" for every rule that matches a file's code. Files are matched by file name.</summary>
        public static List<string> FindViolations(string directory, IEnumerable<(string Pattern, string Why)> rules, params string[] excludeFileNames)
        {
            var ruleList = rules.ToList();
            var violations = new List<string>();
            foreach (var file in Files(directory, excludeFileNames))
            {
                var code = ReadCode(file);
                foreach (var (pattern, why) in ruleList)
                {
                    if (Regex.IsMatch(code, pattern))
                        violations.Add($"{Path.GetFileName(file)}: {why}");
                }
            }
            return violations;
        }

        /// <summary>File names whose code matches the pattern.</summary>
        public static List<string> FilesMatching(string directory, string pattern, params string[] excludeFileNames)
        {
            return Files(directory, excludeFileNames)
                .Where(f => Regex.IsMatch(ReadCode(f), pattern))
                .Select(f => Path.GetFileName(f))
                .ToList();
        }

        private static IEnumerable<string> Files(string directory, string[] excludeFileNames)
        {
            return SourceFiles(directory)
                .Where(f => !excludeFileNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Removes comments while keeping string and char literals intact. Newlines inside block comments are
        /// kept so line structure survives. Handles "regular", @"verbatim" and $"interpolated" strings; raw
        /// string literals are treated as regular strings, which is good enough for this codebase.
        /// </summary>
        public static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            int i = 0;
            while (i < source.Length)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    while (i < source.Length && source[i] != '\n') i++;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    i += 2;
                    while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                    {
                        if (source[i] == '\n') sb.Append('\n');
                        i++;
                    }
                    i = Math.Min(i + 2, source.Length);
                    sb.Append(' ');
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    bool verbatim = c == '"' && IsVerbatimPrefix(source, i);
                    i = CopyLiteral(source, i, c, verbatim, sb);
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // @"..." or $@"..." / @$"..."
        private static bool IsVerbatimPrefix(string source, int quoteIndex)
        {
            if (quoteIndex >= 1 && source[quoteIndex - 1] == '@') return true;
            return quoteIndex >= 2 && source[quoteIndex - 1] == '$' && source[quoteIndex - 2] == '@';
        }

        // Copies a literal starting at the opening quote; returns the index after the closing quote
        private static int CopyLiteral(string source, int start, char quote, bool verbatim, StringBuilder sb)
        {
            sb.Append(quote);
            int i = start + 1;
            while (i < source.Length)
            {
                char c = source[i];
                if (verbatim)
                {
                    if (c == '"' && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        sb.Append("\"\"");
                        i += 2;
                        continue;
                    }
                }
                else
                {
                    if (c == '\\' && i + 1 < source.Length)
                    {
                        sb.Append(c).Append(source[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '\n') break; // unterminated regular literal: stop at end of line
                }

                sb.Append(c);
                i++;
                if (c == quote) return i;
            }
            return i;
        }
    }
}
