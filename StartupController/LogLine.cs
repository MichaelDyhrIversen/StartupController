using System.Globalization;

namespace StartupController
{
    // One log line as the log viewer shows it. Every displayed field is escaped again (LogEscape), so a tampered or
    // pre-Phase-4 log can't show control or bidi characters; Escape leaves backslashes alone, so text that is already
    // escaped is unchanged. Nothing is ever unescaped. Raw keeps the line as read, for copying.
    internal sealed class LogLine
    {
        internal const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff"; // as written by LoggingService
        internal const string FieldSeparator = "  |  ";

        private LogLine(string raw, DateTime? timestamp, string time, string level, string category, string message, bool launchFailed)
        {
            Raw = raw;
            Timestamp = timestamp;
            Time = time;
            Level = level;
            Category = category;
            Message = message;
            LaunchFailed = launchFailed;
        }

        internal string Raw { get; }
        internal DateTime? Timestamp { get; }
        internal string Time { get; }      // displayed timestamp, "" for a raw line
        internal string Level { get; }     // INFO, WARN, ERROR, LAUNCH, SESSION, ...; "" for a raw line
        internal string Category { get; }
        internal string Message { get; }   // the remaining fields joined with FieldSeparator
        internal bool LaunchFailed { get; } // a LAUNCH line whose result field is FAILURE

        // "timestamp<TAB>LEVEL<TAB>Category<TAB>field..." is split into columns. Anything else (fewer than 3 fields,
        // a timestamp in another format) is a raw line: no level, the whole line as the message.
        internal static LogLine Parse(string? raw)
        {
            raw ??= "";
            var parts = raw.Split('\t');
            if (parts.Length >= 3
                && DateTime.TryParseExact(parts[0], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            {
                var level = LogEscape.Escape(parts[1]);
                var message = string.Join(FieldSeparator, parts.Skip(3).Select(LogEscape.Escape));
                // LAUNCH fields: name, exe path, SUCCESS/FAILURE, details
                bool launchFailed = string.Equals(parts[1], "LAUNCH", StringComparison.Ordinal)
                    && parts.Length > 5 && string.Equals(parts[5], "FAILURE", StringComparison.Ordinal);
                return new LogLine(raw, timestamp, parts[0], level, LogEscape.Escape(parts[2]), message, launchFailed);
            }
            return new LogLine(raw, null, "", "", "", LogEscape.Escape(raw), false);
        }

        // What Copy puts on the clipboard: the raw line with every tab-separated field escaped again, so a
        // hand-edited log can't put control or bidi characters on the clipboard. Lines written by the logger are
        // already escaped, so they come out unchanged (the same instance when nothing needs escaping).
        internal string CopyText
        {
            get
            {
                var escaped = string.Join('\t', Raw.Split('\t').Select(LogEscape.Escape));
                return string.Equals(escaped, Raw, StringComparison.Ordinal) ? Raw : escaped;
            }
        }

        // text cut to at most maxLength chars plus an ellipsis when longer, never between the halves of a surrogate pair
        internal static string Shorten(string text, int maxLength)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
            if (text.Length <= maxLength) return text;
            int cut = maxLength;
            if (char.IsHighSurrogate(text[cut - 1]) && char.IsLowSurrogate(text[cut]))
                cut--;
            return string.Concat(text.AsSpan(0, cut), "\u2026");
        }
    }
}
