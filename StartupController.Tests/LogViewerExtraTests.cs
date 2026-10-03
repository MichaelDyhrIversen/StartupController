using StartupController.Tests.Infrastructure;

namespace StartupController.Tests
{
    // Extra coverage for the log viewer and help loading: real logger output parsed back, change signals,
    // vanishing/odd paths, filter edge cases. Temp folders only.
    public class LogViewerExtraTests
    {
        private const string Ts = "2026-10-03 12:34:56.789";

        private static string NewTempDir()
        {
            var p = Path.Combine(Path.GetTempPath(), "StartupController.Tests", "viewer2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(p);
            return p;
        }

        [Fact]
        public void LogLine_NonLaunchLine_WithFailureInSixthField_IsNotALaunchFailure()
        {
            var line = LogLine.Parse($"{Ts}\tINFO\tProgram\tApp\tC:\\a.exe\tFAILURE\tx");

            Assert.False(line.LaunchFailed);
            Assert.Empty(LogFilter.Apply(new[] { line }, LogLevelFilter.LaunchFailure, "", false));
        }

        [Fact]
        public void LogLine_LowercaseLaunch_IsAnUnknownLevel_NotALaunch()
        {
            var line = LogLine.Parse($"{Ts}\tlaunch\tProgram\tApp\tC:\\a.exe\tFAILURE\tx");

            Assert.False(line.LaunchFailed);
            Assert.Equal(LogLevelFilter.Other, LogFilter.LevelOf(line));
        }

        [Fact]
        public void LogLine_TimestampWithPaddingOrOtherFormat_IsARawLine()
        {
            Assert.Null(LogLine.Parse($" {Ts}\tINFO\tApp\tx").Timestamp);
            Assert.Null(LogLine.Parse($"{Ts} \tINFO\tApp\tx").Timestamp);
            Assert.Null(LogLine.Parse("2026-10-03 12:34:56\tINFO\tApp\tx").Timestamp);
        }

        [Fact]
        public void LogLine_NullAndEmpty_DoNotThrow()
        {
            Assert.Equal("", LogLine.Parse(null!).Message);
            Assert.Equal("", LogLine.Parse("").Message);
        }

        [Fact]
        public void LogLine_ParsesWhatTheRealLoggerWrites_IncludingHostileFields()
        {
            var name = TestLog.Unique("Prog") + "\tTAB\u202E";
            LoggingService.LogLaunchResult(name, @"C:\x y\a.exe", false, "Executable not found\r\nsecond");
            LoggingService.LogLaunchResult(name + "ok", @"C:\a.exe", true);

            var marker = name.Substring(0, name.IndexOf('\t'));
            var lines = TestLog.LinesContaining(marker).Select(LogLine.Parse).ToList();

            Assert.Equal(2, lines.Count);
            Assert.All(lines, l => Assert.Equal("LAUNCH", l.Level));
            Assert.True(lines[0].LaunchFailed);
            Assert.False(lines[1].LaunchFailed);
            Assert.DoesNotContain('\u202E', lines[0].Message);
            Assert.Contains(@"Executable not found", lines[0].Message, StringComparison.Ordinal);
            // The shown message is one line even though the details had a line break
            Assert.DoesNotContain('\n', lines[0].Message);
            Assert.DoesNotContain('\r', lines[0].Message);
        }

        [Fact]
        public void LogLine_ParsesTheRealSessionHeader()
        {
            LoggingService.StartSession(); // its own header, so the test doesn't depend on another test or on rotation
            var session = TestLog.Read().Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.Contains("\tSESSION\t", StringComparison.Ordinal));

            Assert.NotNull(session);
            var line = LogLine.Parse(session!);
            Assert.NotNull(line.Timestamp);
            Assert.Equal(LogLevelFilter.Session, LogFilter.LevelOf(line));
        }

        [Fact]
        public void Apply_Problems_IgnoresSessionAndInfoNoise_ButKeepsFailedLaunches()
        {
            var lines = new[]
            {
                LogLine.Parse($"{Ts}\tSESSION\tApp\ts"),
                LogLine.Parse($"{Ts}\tINFO\tApp\ti"),
                LogLine.Parse($"{Ts}\tLAUNCH\tProgram\tA\tC:\\a.exe\tSUCCESS\t"),
                LogLine.Parse($"{Ts}\tLAUNCH\tProgram\tB\tC:\\b.exe\tFAILURE\td"),
                LogLine.Parse($"{Ts}\tWARN\tApp\tw"),
                LogLine.Parse("raw"),
            };

            var shown = LogFilter.Apply(lines, LogLevelFilter.Problems, "", false);

            Assert.Equal(new[] { "B" + LogLine.FieldSeparator + "C:\\b.exe" + LogLine.FieldSeparator + "FAILURE" + LogLine.FieldSeparator + "d", "w" }, shown.Select(l => l.Message));
        }

        [Fact]
        public void Apply_Launch_IncludesFailedLaunchesOnce()
        {
            var failed = LogLine.Parse($"{Ts}\tLAUNCH\tProgram\tB\tC:\\b.exe\tFAILURE\td");

            Assert.Single(LogFilter.Apply(new[] { failed }, LogLevelFilter.Launch | LogLevelFilter.LaunchFailure, "", false));
        }

        [Fact]
        public void Apply_Search_WhitespaceOnly_ShowsEverything_AndEmptyInputIsEmpty()
        {
            var lines = new[] { LogLine.Parse($"{Ts}\tINFO\tApp\ta"), LogLine.Parse("raw") };

            Assert.Equal(2, LogFilter.Apply(lines, LogLevelFilter.All, "   ", false).Count);
            Assert.Empty(LogFilter.Apply(Array.Empty<LogLine>(), LogLevelFilter.All, "x", true));
        }

        [Fact]
        public void Apply_Search_MatchesTheRawLine_NotOnlyTheDisplayedMessage()
        {
            // The escaped display differs from the raw text for a control char; searching the escaped form finds it too
            var line = LogLine.Parse($"{Ts}\tINFO\tApp\ta\u0007b");

            Assert.Single(LogFilter.Apply(new[] { line }, LogLevelFilter.All, "\\u0007", false));
            Assert.Single(LogFilter.Apply(new[] { line }, LogLevelFilter.All, "a\u0007b", false));
        }

        [Fact]
        public void ReadTail_ChangeSignals_FileLengthAndLastWriteMove_WhenTheFileGrows()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "a.log");
                File.WriteAllText(path, "one\n");
                File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                var first = LogFileReader.ReadTail(path);

                File.AppendAllText(path, "two\n");
                File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
                var second = LogFileReader.ReadTail(path);

                Assert.True(second.FileLength > first.FileLength);
                Assert.True(second.LastWriteTimeUtc > first.LastWriteTimeUtc);
                Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), first.LastWriteTimeUtc);
                Assert.Equal(new[] { "one", "two" }, second.Lines);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void ReadTail_PathIsADirectory_IsMissing_NotAnException()
        {
            var dir = NewTempDir();
            try
            {
                var result = LogFileReader.ReadTail(dir);

                Assert.False(result.Exists);
                Assert.Null(result.Error);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Theory]
        [InlineData("bad|name?.log")]
        [InlineData("con:")]
        public void ReadTail_InvalidPathCharacters_DoNotThrow(string name)
        {
            var result = LogFileReader.ReadTail(Path.Combine(Path.GetTempPath(), "StartupController.Tests", name));

            Assert.Empty(result.Lines);
        }

        [Fact]
        public void ReadTail_ArgumentValidation()
        {
            Assert.Throws<ArgumentException>(() => LogFileReader.ReadTail(""));
            Assert.Throws<ArgumentOutOfRangeException>(() => LogFileReader.ReadTail("x.log", 0));
        }

        [Fact]
        public void ReadTail_ExactlyMaxBytes_IsNotTruncated()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "a.log");
                File.WriteAllText(path, "abc\ndef\n");

                var result = LogFileReader.ReadTail(path, maxBytes: 8);

                Assert.False(result.Truncated);
                Assert.Equal(new[] { "abc", "def" }, result.Lines);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void ReadTail_TruncatedInsideTheLastLine_WithNoNewline_ReturnsNothing()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "a.log");
                File.WriteAllText(path, "first\nsecond-without-newline");

                var result = LogFileReader.ReadTail(path, maxBytes: 5);

                Assert.True(result.Truncated);
                Assert.Empty(result.Lines);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void ReadTail_RealMaxSize_LargeFile_ReturnsOnlyTheTail()
        {
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "a.log");
                using (var w = new StreamWriter(path))
                {
                    for (int i = 0; i < 500_000; i++) w.Write("line " + i + "\n");
                }

                var result = LogFileReader.ReadTail(path);

                Assert.True(result.Truncated);
                Assert.Equal("line 499999", result.Lines[^1]);
                Assert.True(result.Lines.Count < 500_000);
                Assert.StartsWith("line ", result.Lines[0], StringComparison.Ordinal);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void LoadEmbedded_MissingResource_LogsAWarning()
        {
            var name = "StartupController." + TestLog.Unique("NoSuchHelp") + ".md";

            HelpContent.Load(typeof(HelpContent).Assembly, name);

            Assert.Contains(TestLog.LinesContaining(name), l => l.Contains("\tWARN\t", StringComparison.Ordinal));
        }

        [Fact]
        public void Load_UnreadableOrEmptyResource_GivesPlaceholder()
        {
            // The log folder README-like resource doesn't exist; an assembly with no resources behaves the same
            var sections = HelpContent.Load(typeof(string).Assembly, "No.Such.Resource");

            Assert.Equal(HelpContent.MissingText, Assert.Single(sections).Body);
        }

        [Fact]
        public void Form1_ChildWindowsAreGuardedInLaunchMode_AndNeverOpenedByLaunchPath()
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "Form1.cs"));

            foreach (var method in new[] { "ShowHelp", "ShowLogViewer" })
            {
                int i = code.IndexOf("private void " + method + "()", StringComparison.Ordinal);
                Assert.True(i >= 0, method);
                var head = code.Substring(i, Math.Min(250, code.Length - i));
                Assert.Contains("if (IsLaunchMode) return;", head, StringComparison.Ordinal);
            }
        }
    }
}
