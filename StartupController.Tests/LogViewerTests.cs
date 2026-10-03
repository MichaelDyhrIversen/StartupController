using System.ComponentModel;
using System.Text;
using StartupController.Tests.Infrastructure;

namespace StartupController.Tests
{
    // The log viewer's pure parts: LogFileReader, LogLine, LogFilter and LoggingService.OpenLogFolder.
    // Temp folders only; nothing here touches the real log folder or starts a process.
    public class LogViewerTests
    {
        private const string Ts = "2026-10-03 12:34:56.789";

        private sealed class TempDir : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StartupController.Tests", "viewer-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Path);

            public string File(string name) => System.IO.Path.Combine(Path, name);

            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
            }
        }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // ---------- LogFileReader ----------

        [Fact]
        public void ReadTail_MissingFile_ReportsMissing_AndCreatesNothing()
        {
            using var dir = new TempDir();
            var folder = System.IO.Path.Combine(dir.Path, "logs");
            var path = System.IO.Path.Combine(folder, "startupcontroller.log");

            var result = LogFileReader.ReadTail(path);

            Assert.False(result.Exists);
            Assert.Null(result.Error);
            Assert.Empty(result.Lines);
            Assert.False(Directory.Exists(folder));
        }

        [Fact]
        public void ReadTail_SmallFile_ReturnsAllLines()
        {
            using var dir = new TempDir();
            var path = dir.File("a.log");
            File.WriteAllText(path, "one\r\ntwo\r\nthree\r\n", Utf8NoBom);

            var result = LogFileReader.ReadTail(path);

            Assert.True(result.Exists);
            Assert.False(result.Truncated);
            Assert.Equal(new[] { "one", "two", "three" }, result.Lines);
            Assert.Equal(new FileInfo(path).Length, result.FileLength);
        }

        [Theory]
        [InlineData("", new string[0])]
        [InlineData("\r\n", new string[0])]
        [InlineData("no newline", new[] { "no newline" })]
        [InlineData("a\nb", new[] { "a", "b" })]
        public void ReadTail_EdgeShapes(string content, string[] expected)
        {
            using var dir = new TempDir();
            var path = dir.File("a.log");
            File.WriteAllText(path, content, Utf8NoBom);

            Assert.Equal(expected, LogFileReader.ReadTail(path).Lines);
        }

        [Fact]
        public void ReadTail_LargerThanMax_ReturnsTheTail_WithoutThePartialFirstLine()
        {
            using var dir = new TempDir();
            var path = dir.File("a.log");
            File.WriteAllText(path, "first line that is cut\nsecond\nthird\n", Utf8NoBom);

            var result = LogFileReader.ReadTail(path, maxBytes: 10); // starts inside "second"

            Assert.True(result.Truncated);
            Assert.Equal(new[] { "third" }, result.Lines);
        }

        [Fact]
        public void ReadTail_SplitMultiByteCharacterAtTheSeekPoint_IsDroppedWithThePartialLine()
        {
            using var dir = new TempDir();
            var path = dir.File("a.log");
            // "æ" is 2 bytes and the emoji 4: the read starts 1 byte into the emoji
            var content = "xx\u00E6\u00E6\U0001F600\nnext \u00E6 line\n";
            File.WriteAllText(path, content, Utf8NoBom);
            int maxBytes = Utf8NoBom.GetByteCount("\U0001F600\nnext \u00E6 line\n") - 1;

            var result = LogFileReader.ReadTail(path, maxBytes);

            Assert.True(result.Truncated);
            Assert.Equal(new[] { "next \u00E6 line" }, result.Lines);
        }

        [Fact]
        public void ReadTail_InvalidUtf8_IsReplaced_NotThrown()
        {
            using var dir = new TempDir();
            var path = dir.File("a.log");
            File.WriteAllBytes(path, new byte[] { (byte)'a', 0xFF, 0xFE, (byte)'b', (byte)'\n' });

            var line = Assert.Single(LogFileReader.ReadTail(path).Lines);

            Assert.StartsWith("a", line, StringComparison.Ordinal);
            Assert.EndsWith("b", line, StringComparison.Ordinal);
            Assert.Contains("\uFFFD", line, StringComparison.Ordinal);
        }

        [Fact]
        public void ReadTail_SkipsABom()
        {
            using var dir = new TempDir();
            var path = dir.File("a.log");
            File.WriteAllText(path, "line\n", new UTF8Encoding(true));

            Assert.Equal(new[] { "line" }, LogFileReader.ReadTail(path).Lines);
        }

        [Fact]
        public void ReadTail_WhileAWriterHoldsTheFile_LikeAppendAllText()
        {
            using var dir = new TempDir();
            var path = dir.File("a.log");
            File.WriteAllText(path, "existing\n", Utf8NoBom);

            using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                writer.Write(Utf8NoBom.GetBytes("appended\n"));
                writer.Flush();

                var result = LogFileReader.ReadTail(path);

                Assert.Null(result.Error);
                Assert.Equal(new[] { "existing", "appended" }, result.Lines);
            }
        }

        [Fact]
        public void ReadTail_HoldsNoHandle_SoAppendAndRotationGoAhead()
        {
            using var dir = new TempDir();
            var path = dir.File("startupcontroller.log");
            File.WriteAllText(path, new string('x', 200) + "\n", Utf8NoBom);

            LogFileReader.ReadTail(path);
            File.AppendAllText(path, "after\n", Utf8NoBom);
            Assert.True(LoggingService.RotateIfNeeded(path, 100, 3));

            Assert.True(File.Exists(LoggingService.RotatedPath(path, 1)));
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void ReadTail_ExclusivelyLocked_ReturnsAnError_NotThrown()
        {
            using var dir = new TempDir();
            var path = dir.File("a.log");
            File.WriteAllText(path, "x\n", Utf8NoBom);

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = LogFileReader.ReadTail(path);

                Assert.True(result.Exists);
                Assert.NotNull(result.Error);
                Assert.Equal(nameof(IOException), result.ErrorKind);
                Assert.Empty(result.Lines);
            }
        }

        [Fact]
        public void LogFileReader_OpensWithReadWriteAndDeleteSharing()
        {
            var code = SourceScan.ReadCode(System.IO.Path.Combine(SourceScan.ProductionSourceDirectory(), "LogFileReader.cs"));

            Assert.Contains("FileShare.ReadWrite | FileShare.Delete", code, StringComparison.Ordinal);
            Assert.Contains("FileMode.Open,", code, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"FileMode\s*\.\s*(OpenOrCreate|Create|Append)", code);
        }

        // ---------- LogLine ----------

        [Fact]
        public void Parse_InfoLine()
        {
            var line = LogLine.Parse($"{Ts}\tINFO\tApp\tWindow restored");

            Assert.Equal(Ts, line.Time);
            Assert.Equal(new DateTime(2026, 10, 3, 12, 34, 56, 789), line.Timestamp);
            Assert.Equal("INFO", line.Level);
            Assert.Equal("App", line.Category);
            Assert.Equal("Window restored", line.Message);
            Assert.False(line.LaunchFailed);
        }

        [Fact]
        public void Parse_LaunchLine_JoinsTheFields_AndFlagsFailure()
        {
            var line = LogLine.Parse($"{Ts}\tLAUNCH\tProgram\tApp\tC:\\a.exe\tFAILURE\tExecutable not found");

            Assert.Equal("App" + LogLine.FieldSeparator + "C:\\a.exe" + LogLine.FieldSeparator + "FAILURE" + LogLine.FieldSeparator + "Executable not found", line.Message);
            Assert.True(line.LaunchFailed);
            Assert.False(LogLine.Parse($"{Ts}\tLAUNCH\tProgram\tApp\tC:\\a.exe\tSUCCESS\t").LaunchFailed);
        }

        [Fact]
        public void Parse_SessionHeader()
        {
            var line = LogLine.Parse($"{Ts}\tSESSION\tApp\t=== New session started ===");

            Assert.Equal("SESSION", line.Level);
            Assert.Equal(LogLevelFilter.Session, LogFilter.LevelOf(line));
        }

        [Theory]
        [InlineData("just text")]
        [InlineData("2026-10-03 12:34:56.789\tINFO")]
        [InlineData("03-10-2026 12:34\tINFO\tApp\tx")]
        public void Parse_OtherShapes_AreRawLines(string raw)
        {
            var line = LogLine.Parse(raw);

            Assert.Equal("", line.Level);
            Assert.Null(line.Timestamp);
            Assert.Equal(LogEscape.Escape(raw), line.Message);
            Assert.Equal(LogLevelFilter.Other, LogFilter.LevelOf(line));
        }

        [Fact]
        public void Parse_TamperedLine_ShowsControlAndBidiCharactersEscaped_ButCopiesTheRawLine()
        {
            var raw = $"{Ts}\tINFO\tApp\tevil\u202Etxt.exe\u0007";

            var line = LogLine.Parse(raw);

            Assert.Equal(@"evil\u202Etxt.exe\u0007", line.Message);
            Assert.Equal(raw, line.Raw);
        }

        [Fact]
        public void Parse_AlreadyEscapedText_IsNotEscapedTwiceOrUnescaped()
        {
            var line = LogLine.Parse($"{Ts}\tINFO\tApp\tvalue\\twith\\nescapes");

            Assert.Equal(@"value\twith\nescapes", line.Message);
        }

        // ---------- LogFilter ----------

        private static IReadOnlyList<LogLine> Lines(params string[] raw) => raw.Select(LogLine.Parse).ToList();

        private static readonly IReadOnlyList<LogLine> Sample = Lines(
            $"{Ts}\tSESSION\tApp\tfirst session",
            $"{Ts}\tINFO\tApp\told info",
            $"{Ts}\tSESSION\tApp\tsecond session",
            $"{Ts}\tINFO\tApp\tHello World",
            $"{Ts}\tWARN\tApp\tcareful",
            $"{Ts}\tERROR\tApp\tbroken",
            $"{Ts}\tLAUNCH\tProgram\tGood\tC:\\g.exe\tSUCCESS\t",
            $"{Ts}\tLAUNCH\tProgram\tBad\tC:\\b.exe\tFAILURE\tExecutable not found",
            "raw text line");

        private static string[] Messages(IEnumerable<LogLine> lines) => lines.Select(l => l.Message).ToArray();

        [Theory]
        [InlineData((int)LogLevelFilter.Info, 2)]
        [InlineData((int)LogLevelFilter.Warn, 1)]
        [InlineData((int)LogLevelFilter.Error, 1)]
        [InlineData((int)LogLevelFilter.Launch, 2)]
        [InlineData((int)LogLevelFilter.Session, 2)]
        [InlineData((int)LogLevelFilter.Other, 1)]
        [InlineData((int)LogLevelFilter.LaunchFailure, 1)]
        [InlineData((int)LogLevelFilter.All, 9)]
        [InlineData((int)LogLevelFilter.Problems, 3)]
        [InlineData((int)LogLevelFilter.None, 0)]
        public void Apply_ByLevel(int levels, int expected)
        {
            Assert.Equal(expected, LogFilter.Apply(Sample, (LogLevelFilter)levels, "", false).Count);
        }

        [Fact]
        public void Apply_OtherCatchesRawLines()
        {
            Assert.Equal(new[] { "raw text line" }, Messages(LogFilter.Apply(Sample, LogLevelFilter.Other, null, false)));
        }

        [Fact]
        public void Apply_Search_IgnoresCase_AndMatchesInsideFields()
        {
            Assert.Equal(new[] { "Hello World" }, Messages(LogFilter.Apply(Sample, LogLevelFilter.All, "hello WORLD", false)));
            Assert.Single(LogFilter.Apply(Sample, LogLevelFilter.All, "b.EXE", false));
            Assert.Empty(LogFilter.Apply(Sample, LogLevelFilter.All, "no such text", false));
        }

        [Fact]
        public void Apply_CurrentSession_StartsAtTheLastSessionLine()
        {
            var shown = LogFilter.Apply(Sample, LogLevelFilter.All, "", true);

            Assert.Equal("second session", shown[0].Message);
            Assert.Equal(7, shown.Count);
        }

        [Fact]
        public void Apply_CurrentSession_WithoutASessionLine_ShowsEverything()
        {
            var lines = Lines($"{Ts}\tINFO\tApp\ta", $"{Ts}\tINFO\tApp\tb");

            Assert.Equal(2, LogFilter.Apply(lines, LogLevelFilter.All, "", true).Count);
        }

        [Fact]
        public void Apply_CurrentSession_WithOneSessionLine()
        {
            var lines = Lines($"{Ts}\tINFO\tApp\tbefore", $"{Ts}\tSESSION\tApp\ts", $"{Ts}\tINFO\tApp\tafter");

            Assert.Equal(new[] { "s", "after" }, Messages(LogFilter.Apply(lines, LogLevelFilter.All, "", true)));
        }

        [Fact]
        public void Apply_CombinedFilters()
        {
            var shown = LogFilter.Apply(Sample, LogLevelFilter.Problems, "not found", true);

            Assert.True(Assert.Single(shown).LaunchFailed);
        }

        // ---------- LoggingService.OpenLogFolder ----------

        [Fact]
        public void OpenLogFolder_StartsTheFolder_ThroughTheSeam_WithoutArguments()
        {
            using var dir = new TempDir();
            var starter = new FakeProcessStarter();

            var ok = LoggingService.OpenLogFolder(starter, dir.Path, out var error);

            Assert.True(ok);
            Assert.Null(error);
            var psi = Assert.Single(starter.Started);
            Assert.Equal(dir.Path, psi.FileName);
            Assert.True(psi.UseShellExecute);
            Assert.Equal("", psi.Arguments);
            Assert.True(starter.Handles.Single().Disposed);
        }

        [Fact]
        public void OpenLogFolder_MissingFolder_StartsNothing_AndDoesNotCreateIt()
        {
            using var dir = new TempDir();
            var missing = System.IO.Path.Combine(dir.Path, "logs");
            var starter = new FakeProcessStarter();

            var ok = LoggingService.OpenLogFolder(starter, missing, out var error);

            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Empty(starter.Started);
            Assert.False(Directory.Exists(missing));
        }

        [Fact]
        public void OpenLogFolder_StartFailure_ReturnsFalse_AndLogsIt()
        {
            using var dir = new TempDir();
            var message = TestLog.Unique("NoExplorer");
            var starter = new FakeProcessStarter { ThrowOnStart = new Win32Exception(2, message) };

            var ok = LoggingService.OpenLogFolder(starter, dir.Path, out var error);

            Assert.False(ok);
            Assert.Equal(message, error);
            Assert.Contains(TestLog.LinesContaining(message), l => l.Contains("\tERROR\t", StringComparison.Ordinal) && l.Contains("Could not open the log folder", StringComparison.Ordinal));
        }

        [Fact]
        public void OpenLogFolder_PublicOverload_UsesTheLoggersFolder()
        {
            var starter = new FakeProcessStarter();

            Assert.True(LoggingService.OpenLogFolder(starter, out _));

            Assert.Equal(LoggingService.LogDirectory, Assert.Single(starter.Started).FileName);
            Assert.Equal(System.IO.Path.GetDirectoryName(LoggingService.LogFilePath), LoggingService.LogDirectory);
        }
    }
}
