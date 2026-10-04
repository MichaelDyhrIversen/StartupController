namespace StartupController
{
    // Which lines the log viewer shows, by level
    [Flags]
    internal enum LogLevelFilter
    {
        None = 0,
        Info = 1,
        Warn = 2,
        Error = 4,
        Launch = 8,          // every LAUNCH line
        Session = 16,
        Other = 32,          // unknown levels and lines that aren't in the log format
        LaunchFailure = 64,  // LAUNCH lines with FAILURE only (already included by Launch)
        // No LaunchFailure: Launch already covers every LAUNCH line, failed or not. LaunchFailure exists only so
        // Problems can show failed launches without the successful ones.
        All = Info | Warn | Error | Launch | Session | Other,
        Problems = Warn | Error | LaunchFailure,
    }

    // Filtering for the log viewer. Pure (no WinForms), so it is unit-testable.
    internal static class LogFilter
    {
        internal static LogLevelFilter LevelOf(LogLine line) => line.Level switch
        {
            "INFO" => LogLevelFilter.Info,
            "WARN" => LogLevelFilter.Warn,
            "ERROR" => LogLevelFilter.Error,
            "LAUNCH" => LogLevelFilter.Launch,
            "SESSION" => LogLevelFilter.Session,
            _ => LogLevelFilter.Other,
        };

        // The lines that match all filters, in file order. Search: ordinal, ignoring case, over the raw line and the
        // displayed message. Current session: from the last SESSION line on (all lines when there is none, e.g. the
        // header was cut off by the 4 MB limit).
        internal static IReadOnlyList<LogLine> Apply(IReadOnlyList<LogLine> lines, LogLevelFilter levels, string? search, bool currentSessionOnly)
        {
            int start = 0;
            if (currentSessionOnly)
            {
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    if (LevelOf(lines[i]) == LogLevelFilter.Session)
                    {
                        start = i;
                        break;
                    }
                }
            }

            var query = search?.Trim() ?? "";
            var result = new List<LogLine>();
            for (int i = start; i < lines.Count; i++)
            {
                var line = lines[i];
                if (!MatchesLevel(line, levels)) continue;
                if (query.Length > 0
                    && !line.Raw.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && !line.Message.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(line);
            }
            return result;
        }

        // True when after is before with lines added at the end (same raw text, in order). Only then do the list's
        // selected row numbers still point at the lines the user picked. A slid window (first line dropped by the
        // size limits), a rotation, a shorter file or a new SESSION with "current session only" all fail this.
        internal static bool IsAppendOf(IReadOnlyList<LogLine> before, IReadOnlyList<LogLine> after)
        {
            ArgumentNullException.ThrowIfNull(before);
            ArgumentNullException.ThrowIfNull(after);
            if (after.Count < before.Count) return false;
            for (int i = 0; i < before.Count; i++)
            {
                if (!string.Equals(before[i].Raw, after[i].Raw, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static bool MatchesLevel(LogLine line, LogLevelFilter levels)
        {
            var level = LevelOf(line);
            if ((levels & level) != 0) return true;
            return line.LaunchFailed && (levels & LogLevelFilter.LaunchFailure) != 0;
        }
    }
}
