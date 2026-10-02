using System.Globalization;
using System.Text;

namespace StartupController
{
    // Escaping of log fields, shared by LoggingService and the uninstall helper's log (linked into
    // StartupController.ReturnToWindows.exe, net462), so a registry value can't forge or split a line in either log.
    internal static class LogEscape
    {
        // Keeps a field on one line and unambiguous: \r \n \t as written; as \uXXXX: other control characters, the
        // Unicode line/paragraph separators, format characters such as bidi overrides (except the zero-width
        // non-joiner/joiner that emoji and some scripts need) and lone surrogates. Valid surrogate pairs are kept.
        // Returns the same instance when nothing needs escaping.
        internal static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var text = value!;

            StringBuilder? escaped = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    escaped?.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }

                string? replacement = c switch
                {
                    '\r' => @"\r",
                    '\n' => @"\n",
                    '\t' => @"\t",
                    _ when NeedsCodeEscape(c) => $@"\u{(int)c:X4}",
                    _ => null
                };

                if (replacement == null)
                {
                    escaped?.Append(c);
                    continue;
                }

                escaped ??= new StringBuilder(text, 0, i, text.Length + 16);
                escaped.Append(replacement);
            }
            return escaped?.ToString() ?? text;
        }

        // A surrogate that gets here is a lone one (Escape keeps valid pairs before asking)
        private static bool NeedsCodeEscape(char c) =>
            char.IsControl(c) || char.IsSurrogate(c) || c == '\u2028' || c == '\u2029'
            || (char.GetUnicodeCategory(c) == UnicodeCategory.Format && c != '\u200C' && c != '\u200D');
    }
}
