using System.Globalization;
using StartupController.Tests.Infrastructure;

namespace StartupController.Tests
{
    // Review fixes for the log viewer: line cap, selection kept only on a pure append, clipboard escaping, tooltip
    // cut, refresh-failure handling, the injected log path. Temp folders and FakeProcessStarter only.
    public class LogViewerFixTests
    {
        private const string Ts = "2026-10-03 12:34:56.789";

        private static string NewTempDir()
        {
            var p = Path.Combine(Path.GetTempPath(), "StartupController.Tests", "viewer3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(p);
            return p;
        }

        private static void DeleteDir(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }

        private static string ViewerCode() =>
            SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "LogViewerForm.cs"));

        // Body of a method in the scanned code: from its signature to the next "private " member
        private static string Method(string code, string signature)
        {
            int start = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature);
            int end = code.IndexOf("        private ", start + signature.Length, StringComparison.Ordinal);
            return end < 0 ? code.Substring(start) : code.Substring(start, end - start);
        }

        // ---------- line cap ----------

        [Fact]
        public void ReadTail_MoreLinesThanTheCap_KeepsTheLastLines_AndIsTruncated()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "a.log");
                File.WriteAllText(path, string.Concat(Enumerable.Range(1, 10).Select(i => "line" + i + "\n")));

                var result = LogFileReader.ReadTail(path, maxLines: 3);

                Assert.Equal(new[] { "line8", "line9", "line10" }, result.Lines);
                Assert.True(result.Truncated);
                Assert.True(result.LinesCapped);
            }
            finally { DeleteDir(dir); }
        }

        [Fact]
        public void ReadTail_ExactlyTheCap_IsNotTruncated()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "a.log");
                File.WriteAllText(path, "a\nb\nc\n");

                var result = LogFileReader.ReadTail(path, maxLines: 3);

                Assert.Equal(new[] { "a", "b", "c" }, result.Lines);
                Assert.False(result.Truncated);
                Assert.False(result.LinesCapped);
            }
            finally { DeleteDir(dir); }
        }

        [Fact]
        public void ReadTail_ByteLimitOnly_IsTruncated_ButNotLineCapped()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "a.log");
                File.WriteAllText(path, "first\nsecond\nthird\n");

                var result = LogFileReader.ReadTail(path, maxBytes: 10);

                Assert.True(result.Truncated);
                Assert.False(result.LinesCapped);
            }
            finally { DeleteDir(dir); }
        }

        [Fact]
        public void ReadTail_RealCap_ManyShortLinesUnderTheByteLimit_AreCappedAtMaxViewLines()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "a.log");
                int total = LogFileReader.MaxViewLines + 10_000;
                using (var w = new StreamWriter(path))
                {
                    for (int i = 0; i < total; i++) w.Write(i.ToString(CultureInfo.InvariantCulture) + "\n");
                }
                Assert.True(new FileInfo(path).Length < LogFileReader.MaxViewBytes);

                var result = LogFileReader.ReadTail(path);

                Assert.Equal(LogFileReader.MaxViewLines, result.Lines.Count);
                Assert.Equal((total - 1).ToString(CultureInfo.InvariantCulture), result.Lines[^1]);
                Assert.Equal("10000", result.Lines[0]);
                Assert.True(result.Truncated);
                Assert.True(result.LinesCapped);
            }
            finally { DeleteDir(dir); }
        }

        [Fact]
        public void ReadTail_ZeroLineCap_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LogFileReader.ReadTail("x.log", maxLines: 0));
        }

        [Fact]
        public void TruncationText_CoversNoCut_ByteCut_AndLineCut()
        {
            var whole = new LogReadResult(true, new[] { "a" }, false, 1, DateTime.MinValue, null, null);
            var bytes = whole with { Truncated = true };
            var lines = new LogReadResult(true, new[] { "a", "b" }, true, 1, DateTime.MinValue, null, null) { LinesCapped = true };

            Assert.Equal("", LogFileReader.TruncationText(whole));
            Assert.Equal("Showing the last 4 MB", LogFileReader.TruncationText(bytes));
            Assert.Equal("Showing the last 2 lines", LogFileReader.TruncationText(lines));
            Assert.Equal("", LogFileReader.TruncationText(LogReadResult.Missing));
        }

        // ---------- selection only survives a pure append ----------

        private static LogLine[] Lines(params string[] raw) => raw.Select(LogLine.Parse).ToArray();

        [Fact]
        public void IsAppendOf_SameLinesPlusNewOnes_IsAnAppend()
        {
            Assert.True(LogFilter.IsAppendOf(Lines("a", "b"), Lines("a", "b", "c")));
            Assert.True(LogFilter.IsAppendOf(Lines("a", "b"), Lines("a", "b")));
            Assert.True(LogFilter.IsAppendOf(Array.Empty<LogLine>(), Lines("a")));
        }

        [Fact]
        public void IsAppendOf_SlidWindow_Shorter_Rotated_OrChanged_IsNot()
        {
            Assert.False(LogFilter.IsAppendOf(Lines("a", "b"), Lines("b", "c")));      // first line dropped by the limits
            Assert.False(LogFilter.IsAppendOf(Lines("a", "b"), Lines("a")));           // shorter
            Assert.False(LogFilter.IsAppendOf(Lines("a", "b"), Lines("x")));           // rotated or another file
            Assert.False(LogFilter.IsAppendOf(Lines("a", "b"), Lines("a", "B", "c"))); // a line changed (ordinal)
        }

        [Fact]
        public void IsAppendOf_NewSessionWithCurrentSessionOnly_IsNot()
        {
            var before = Lines($"{Ts}\tSESSION\tApp\ts1", $"{Ts}\tINFO\tApp\ta");
            var after = Lines($"{Ts}\tSESSION\tApp\ts1", $"{Ts}\tINFO\tApp\ta", $"{Ts}\tSESSION\tApp\ts2", $"{Ts}\tINFO\tApp\tb", $"{Ts}\tINFO\tApp\tc");

            var shownBefore = LogFilter.Apply(before, LogLevelFilter.All, "", currentSessionOnly: true);
            var shownAfter = LogFilter.Apply(after, LogLevelFilter.All, "", currentSessionOnly: true);

            Assert.True(LogFilter.IsAppendOf(before, after));
            Assert.False(LogFilter.IsAppendOf(shownBefore, shownAfter)); // same count or more rows, but other lines
        }

        [Fact]
        public void Viewer_ClearsTheSelection_UnlessTheReadAndTheShownRowsAreAPureAppend()
        {
            var code = ViewerCode();

            Assert.Contains("LogFilter.IsAppendOf(_all, lines)", code, StringComparison.Ordinal);
            Assert.Contains("result.FileLength >= _lastRead.FileLength", code, StringComparison.Ordinal);
            Assert.Contains("ApplyFilter(resetSelection: !appendOnly", code, StringComparison.Ordinal);
            Assert.Contains("if (resetSelection || !LogFilter.IsAppendOf(_shown, filtered))", code, StringComparison.Ordinal);
        }

        // ---------- clipboard ----------

        [Fact]
        public void CopyText_LoggerWrittenLine_IsUnchanged()
        {
            var name = TestLog.Unique("Copy") + "\t\u202E\u0007";
            LoggingService.LogLaunchResult(name, @"C:\x y\a.exe", false, "line1\r\nline2");
            var raw = Assert.Single(TestLog.LinesContaining(name.Substring(0, name.IndexOf('\t'))));

            var line = LogLine.Parse(raw);

            Assert.Same(line.Raw, line.CopyText);
            Assert.Equal(raw, line.CopyText);
        }

        [Fact]
        public void CopyText_HandEditedLine_EscapesControlAndBidiCharacters_ButKeepsTheTabs()
        {
            var raw = $"{Ts}\tINFO\tApp\tevil\u202Etext\u0007\tmore\u2028x";

            var copy = LogLine.Parse(raw).CopyText;

            Assert.Equal($"{Ts}\tINFO\tApp\tevil\\u202Etext\\u0007\tmore\\u2028x", copy);
            Assert.DoesNotContain('\u202E', copy);
            Assert.DoesNotContain('\u0007', copy);
            Assert.Equal(5, copy.Split('\t').Length);
        }

        [Fact]
        public void CopyText_RawLine_IsEscapedToo()
        {
            Assert.Equal("a\\u202Eb", LogLine.Parse("a\u202Eb").CopyText);
        }

        [Fact]
        public void Viewer_CopiesCopyText_NotRaw_AndLogsOnlyTheLineCount()
        {
            var copy = Method(ViewerCode(), "private void CopyLines(");

            Assert.Contains("l.CopyText", copy, StringComparison.Ordinal);
            Assert.DoesNotContain(".Raw", copy, StringComparison.Ordinal);
            Assert.Contains("LoggingService.LogInfo(", copy, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"LogInfo\([^;]*\btext\b", copy);
        }

        // ---------- tooltip ----------

        [Fact]
        public void Shorten_DoesNotSplitASurrogatePair()
        {
            var text = new string('a', 999) + "\U0001F600" + "tail"; // the emoji takes chars 999 and 1000

            var cut = LogLine.Shorten(text, 1000);

            Assert.Equal(new string('a', 999) + "\u2026", cut);
            Assert.False(char.IsHighSurrogate(cut[^2]));
        }

        [Fact]
        public void Shorten_ShortOrExactText_IsUnchanged_AndLongTextIsCut()
        {
            Assert.Equal("abc", LogLine.Shorten("abc", 3));
            Assert.Equal("ab\u2026", LogLine.Shorten("abc", 2));
            Assert.Equal("a\U0001F600\u2026", LogLine.Shorten("a\U0001F600b", 3)); // pair ends exactly at the cut
        }

        // ---------- refresh failures ----------

        [Fact]
        public void Viewer_RefreshFailure_IsLoggedOncePerKind_AndTurnsAutoRefreshOff()
        {
            var code = ViewerCode();
            var run = Method(code, "private async void RunRefresh(");
            var failed = Method(code, "private void OnRefreshFailed(");

            Assert.Contains("OnRefreshFailed(ex)", run, StringComparison.Ordinal);
            Assert.DoesNotContain("LoggingService", run, StringComparison.Ordinal);
            Assert.Matches(@"if\s*\(\s*_loggedErrorKinds\.Add\(.*\)\s*\)\s*LoggingService\.LogError", failed);
            Assert.Contains("_autoRefresh.Checked = false", failed, StringComparison.Ordinal);
            Assert.Contains("_fileLabel.Text", failed, StringComparison.Ordinal);
        }

        // ---------- Ctrl+A ----------

        [Fact]
        public void Viewer_SelectAll_IsOneMessage_NotOneCallPerRow()
        {
            var select = Method(ViewerCode(), "private void SelectAll(");

            Assert.Contains("NativeMethods.LVM_SETITEMSTATE, new IntPtr(-1)", select, StringComparison.Ordinal);
            Assert.DoesNotContain("SelectedIndices.Add", select, StringComparison.Ordinal);
            Assert.Equal(0x102B, NativeMethods.LVM_SETITEMSTATE);
            Assert.Equal(2u, NativeMethods.LVIS_SELECTED);
        }

        // ---------- the injected log path ----------

        [Fact]
        public void Viewer_OpensTheLogItWasGiven_NotTheLoggersPath()
        {
            var code = ViewerCode();

            Assert.Contains("LoggingService.OpenLogFile(_starter, _logFilePath,", code, StringComparison.Ordinal);
            Assert.Contains("Path.GetDirectoryName(_logFilePath)", code, StringComparison.Ordinal);
            Assert.DoesNotContain("LoggingService.LogFilePath", code, StringComparison.Ordinal);
            Assert.DoesNotContain("LoggingService.LogDirectory", code, StringComparison.Ordinal);
        }

        [Fact]
        public void OpenLogFile_GivenPath_StartsThatFileThroughTheSeam()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "other.log");
                var starter = new FakeProcessStarter();

                var ok = LoggingService.OpenLogFile(starter, path, out var error);

                Assert.True(ok);
                Assert.Null(error);
                var psi = Assert.Single(starter.Started);
                Assert.Equal(path, psi.FileName);
                Assert.True(psi.UseShellExecute);
                Assert.True(File.Exists(path));
            }
            finally { DeleteDir(dir); }
        }

        [Fact]
        public void OpenLogFile_GivenPathInAMissingFolder_ReturnsFalse_AndStartsNothing()
        {
            var dir = NewTempDir();
            DeleteDir(dir);
            var starter = new FakeProcessStarter();

            var ok = LoggingService.OpenLogFile(starter, Path.Combine(dir, "other.log"), out var error);

            Assert.False(ok);
            Assert.False(string.IsNullOrEmpty(error));
            Assert.Empty(starter.Started);
            Assert.False(Directory.Exists(dir));
        }

        // ---------- user actions are logged ----------

        [Fact]
        public void Viewer_LogsSwitchingTheFile_ButNotRefreshes()
        {
            var code = ViewerCode();

            Assert.Contains("LoggingService.LogInfo(\"Log viewer switched to: \"", code, StringComparison.Ordinal);
            Assert.DoesNotContain("LoggingService", Method(code, "private async Task RefreshAsync("), StringComparison.Ordinal);
            Assert.DoesNotContain("LoggingService", Method(code, "private void AutoRefreshTick("), StringComparison.Ordinal);
        }

        // ---------- shared child-window setup ----------

        [Theory]
        [InlineData("HelpForm.cs")]
        [InlineData("LogViewerForm.cs")]
        public void ChildWindows_UseTheSharedSetup(string fileName)
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), fileName));

            Assert.Contains("ChildWindow.Setup(this,", code, StringComparison.Ordinal);
            Assert.Contains("ChildWindow.CreateButtonRow(this", code, StringComparison.Ordinal);
            Assert.DoesNotContain("AutoScaleDimensions", code, StringComparison.Ordinal);
            Assert.DoesNotContain("KeyPreview", code, StringComparison.Ordinal);
        }
    }
}
