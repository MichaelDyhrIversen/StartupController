using System.Reflection;
using System.Text;

namespace StartupController
{
    // One section of the Help window: a title in the section list and its plain-text body
    internal sealed record HelpSection(string Title, string Body);

    // The in-app help text. Source: the embedded resource Help/Help.md, in a deliberately tiny format:
    // "## Title" starts a section, "- " starts a bullet (shown as "\u2022 "), a blank line is a paragraph break, lines
    // starting with "<!--" are skipped and text before the first "## " is ignored. Nothing else is interpreted, so
    // the body is shown exactly as written. No WinForms here, so it is unit-testable.
    internal static class HelpContent
    {
        internal const string ResourceName = "StartupController.Help.md";
        internal const string MissingText = "Help content is missing from this build.";
        private const string SectionPrefix = "## ";
        private const string BulletPrefix = "- ";
        private const string Bullet = "\u2022 "; // bullet, escaped so the file's encoding can't break it

        // Never throws. Empty input gives an empty list.
        internal static IReadOnlyList<HelpSection> Parse(string? markdown)
        {
            var sections = new List<HelpSection>();
            if (string.IsNullOrEmpty(markdown)) return sections;

            string? title = null;
            var body = new List<string>();
            foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (line.StartsWith(SectionPrefix, StringComparison.Ordinal))
                {
                    if (title != null) sections.Add(new HelpSection(title, JoinBody(body)));
                    title = line.Substring(SectionPrefix.Length).Trim();
                    body.Clear();
                    continue;
                }
                if (title == null) continue; // maintainer notes before the first section
                if (line.TrimStart().StartsWith("<!--", StringComparison.Ordinal)) continue;

                body.Add(line.StartsWith(BulletPrefix, StringComparison.Ordinal) ? Bullet + line.Substring(BulletPrefix.Length) : line);
            }
            if (title != null) sections.Add(new HelpSection(title, JoinBody(body)));
            return sections;
        }

        // Lines as written; leading and trailing blank lines dropped, runs of blank lines collapsed to one
        private static string JoinBody(List<string> lines)
        {
            var text = new StringBuilder();
            bool pendingBlank = false;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    pendingBlank = text.Length > 0;
                    continue;
                }
                if (text.Length > 0) text.Append(pendingBlank ? "\n\n" : "\n");
                pendingBlank = false;
                text.Append(line);
            }
            return text.ToString();
        }

        // The help shipped in this assembly. A missing resource gives one placeholder section (logged), never throws.
        internal static IReadOnlyList<HelpSection> LoadEmbedded() => Load(typeof(HelpContent).Assembly, ResourceName);

        internal static IReadOnlyList<HelpSection> Load(Assembly assembly, string resourceName)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    // Explicit UTF-8: the arrows and dashes must not depend on a code page
                    using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                    var sections = Parse(reader.ReadToEnd());
                    if (sections.Count > 0) return sections;
                }
                LoggingService.LogWarning("Help content is missing: " + resourceName);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Could not read the help content", ex);
            }
            return new[] { new HelpSection("Help", MissingText) };
        }

        // Built in code and shown last, so the version and log folder are always the real ones
        internal static HelpSection About(string version, string logFolder)
        {
            return new HelpSection("About",
                "StartupController version " + version + "\n\n" +
                "StartupController works per user: it changes only your own startup programs and needs no administrator rights.\n\n" +
                "The log is in this folder:\n" + logFolder);
        }

        // This build's version, e.g. "1.0.29"
        internal static string CurrentVersion()
        {
            var assembly = typeof(HelpContent).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
        }

        // Indices of the sections whose title or body contains the query (ordinal, ignoring case). Empty query: all.
        internal static IEnumerable<int> Search(IReadOnlyList<HelpSection> sections, string? query)
        {
            var q = query?.Trim() ?? "";
            for (int i = 0; i < sections.Count; i++)
            {
                if (q.Length == 0
                    || sections[i].Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || sections[i].Body.Contains(q, StringComparison.OrdinalIgnoreCase))
                    yield return i;
            }
        }
    }
}
