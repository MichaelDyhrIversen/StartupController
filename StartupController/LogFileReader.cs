using System.Globalization;
using System.Security;
using System.Text;

namespace StartupController
{
    // Result of one read of a log file. Error is set (and Lines empty) when the file exists but couldn't be read.
    // Truncated: Lines holds only the end of the file, because of the byte limit, the line limit (LinesCapped) or both.
    internal sealed record LogReadResult(
        bool Exists,
        IReadOnlyList<string> Lines,
        bool Truncated,
        long FileLength,
        DateTime LastWriteTimeUtc,
        string? Error,
        string? ErrorKind)
    {
        internal static LogReadResult Missing { get; } =
            new LogReadResult(false, Array.Empty<string>(), false, 0, DateTime.MinValue, null, null);

        internal static LogReadResult Failed(Exception ex) =>
            new LogReadResult(true, Array.Empty<string>(), false, 0, DateTime.MinValue, ex.Message, ex.GetType().Name);

        // Lines was cut to the line limit (the bytes read held more lines than that)
        internal bool LinesCapped { get; init; }
    }

    // Reads the end of a log file for the log viewer without getting in the way of the logger: the file is opened
    // with FileShare.ReadWrite | FileShare.Delete (so appends and rotation renames go ahead) and closed before
    // returning, so no handle is held between refreshes. Never creates the file or its folder. No WinForms here.
    internal static class LogFileReader
    {
        // Well above the 1 MiB rotation size (and the "doubled" backoff after a failed rotation), small enough for the UI
        internal const long MaxViewBytes = 4L * 1024 * 1024;

        // 4 MB of very short lines would be far more rows than the list and the filter should have to handle
        internal const int MaxViewLines = 50_000;

        private const int BufferSize = 64 * 1024;

        // Invalid bytes become U+FFFD instead of throwing
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        // The last maxBytes of the file as lines (empty lines dropped), at most the last maxLines of them. When the
        // read starts mid-file, everything up to and including the first \n is dropped (a partial line, possibly a
        // split UTF-8 sequence) and Truncated is set; so it is when lines are cut (LinesCapped). I/O and access
        // errors are returned in Error, never thrown.
        internal static LogReadResult ReadTail(string path, long maxBytes = MaxViewBytes, int maxLines = MaxViewLines)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return LogReadResult.Missing;
                var lastWrite = info.LastWriteTimeUtc;

                byte[] buffer;
                int count;
                long length;
                bool truncated;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan))
                {
                    length = stream.Length;
                    long start = Math.Max(0, length - maxBytes);
                    truncated = start > 0;
                    stream.Seek(start, SeekOrigin.Begin);
                    buffer = new byte[length - start];
                    count = ReadFully(stream, buffer);
                }

                int offset = 0;
                if (truncated)
                {
                    int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
                    offset = newline < 0 ? count : newline + 1;
                }
                else if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                {
                    offset = 3; // a BOM from an editor; the logger writes none
                }

                var text = Utf8.GetString(buffer, offset, count - offset);
                var lines = SplitLines(text);
                bool linesCapped = lines.Count > maxLines;
                if (linesCapped)
                    lines.RemoveRange(0, lines.Count - maxLines);
                return new LogReadResult(true, lines, truncated || linesCapped, length, lastWrite, null, null) { LinesCapped = linesCapped };
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return LogReadResult.Missing; // deleted or rotated away between the check and the open
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException)
            {
                return LogReadResult.Failed(ex);
            }
        }

        // Reads until the buffer is full or the stream ends (the file can shrink while it is read)
        private static int ReadFully(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        // Status-bar text for a cut-off read ("" when the whole file is shown). The line limit is named when it
        // applied, since then the lines shown cover less than the byte limit.
        internal static string TruncationText(LogReadResult result, long maxBytes = MaxViewBytes)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (!result.Truncated) return "";
            if (result.LinesCapped)
                return $"Showing the last {result.Lines.Count.ToString("N0", CultureInfo.CurrentCulture)} lines";
            return $"Showing the last {maxBytes / (1024 * 1024)} MB";
        }

        private static List<string> SplitLines(string text)
        {
            var lines = new List<string>();
            foreach (var part in text.Split('\n'))
            {
                var line = part.EndsWith('\r') ? part.Substring(0, part.Length - 1) : part;
                if (line.Length > 0) lines.Add(line);
            }
            return lines;
        }
    }
}
