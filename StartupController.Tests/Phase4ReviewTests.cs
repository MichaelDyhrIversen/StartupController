using System.ComponentModel;
using System.Text.RegularExpressions;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Tester review of Phase 4: log secrecy on every outcome, Escape, concurrent writers, parser equivalence,
    // reselection and OpenLogFile through the fake starter. FakeProcessStarter only.
    public class Phase4ReviewTests
    {
        private const string Self = @"C:\Program Files\StartupController\StartupController.exe";

        private static ProgramLauncher Launcher(FakeProcessStarter starter) => new ProgramLauncher(starter, Self, p => p, (a, b) => false);

        private static LaunchRunner Runner(FakeProcessStarter starter) =>
            new LaunchRunner(Launcher(starter), new FakeNotifier(), new FakeDialog(), launch => Task.FromResult(launch()));

        // ---------- 1. Secrets never reach the log ----------

        // The parser keeps a command whole when no token has an executable extension. That whole command used to be
        // reported as the "exe", so the NotFound error and the LAUNCH line carried the arguments.
        [Theory]
        [InlineData(@"C:\Tools\mytool --token={0}")]               // fully qualified, no extension, not found
        [InlineData(@"C:\Tools v2\mytool --token={0}")]            // unquoted path with spaces, no extension
        [InlineData("\"C:\\Tools\\mytool --token={0}")]            // unmatched leading quote
        [InlineData(@"sub\mytool --token={0}")]                    // relative path
        [InlineData(@".\mytool --token={0}")]
        [InlineData(@"\\server\share\mytool --token={0}")]
        public async Task ExtensionlessCommand_NeverLogsItsArguments(string template)
        {
            var name = TestLog.Unique("NoExt");
            var secret = TestLog.Unique("SECRET");

            var result = await Runner(new FakeProcessStarter()).LaunchManualAsync(P(name, path: string.Format(template, secret)));

            Assert.False(result.Success);
            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task StartFailure_WhoseMessageEchoesTheArguments_IsTheKnownResidualRisk()
        {
            // Documents the boundary: ex.Message is logged verbatim. .NET's own Win32Exception text names the exe and
            // working directory, not the arguments, so this only matters for a hypothetical message that includes them.
            var name = TestLog.Unique("Echo"); var secret = TestLog.Unique("SECRET");
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe") { ThrowOnStart = new Win32Exception(2, "An error occurred trying to start process 'C:\\Apps\\x.exe' with working directory 'C:\\Apps'. The system cannot find the file specified.") };

            await Runner(starter).LaunchManualAsync(P(name, path: @"C:\Apps\x.exe --token=abc"));

            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task LateCompletionAfterTimeout_LogsNameOnly()
        {
            var name = TestLog.Unique("Late");
            var secret = TestLog.Unique("SECRET");
            var gate = new TaskCompletionSource<LaunchResult>();
            var runner = new LaunchRunner(Launcher(new FakeProcessStarter()), new FakeNotifier(), new FakeDialog(),
                launch => gate.Task, TimeSpan.FromMilliseconds(50));

            await runner.LaunchSequenceAsync(new[] { P(name, path: $@"C:\Apps\slow.exe --token={secret}") });
            gate.SetResult(new LaunchResult(false, Error: $"boom --token={secret}", ExePath: $@"C:\Apps\slow.exe --token={secret}"));

            // the continuation runs on the thread pool; wait for its line
            for (int i = 0; i < 100 && !TestLog.LinesContaining(name).Any(l => l.Contains("after the launch timeout")); i++)
                await Task.Delay(20);

            Assert.Contains(TestLog.LinesContaining(name), l => l.Contains("after the launch timeout"));
            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task LaunchSequence_WithMixedOutcomes_LogsNoArguments()
        {
            var secret = TestLog.Unique("SECRET");
            var starter = new FakeProcessStarter(@"C:\Apps\ok.exe") { };
            var programs = new[]
            {
                P(TestLog.Unique("Mix1"), path: $@"C:\Apps\ok.exe --token={secret}"),
                P(TestLog.Unique("Mix2"), path: $@"C:\Apps\gone.exe --token={secret}"),
                P(TestLog.Unique("Mix3"), path: $@"C:\x\StartupController.exe --token={secret}"),
                P(TestLog.Unique("Mix4"), path: $@"C:\Apps\bad.exe:stream --token={secret}"),
                P(TestLog.Unique("Mix5"), path: new string('a', ProgramLauncher.MaxCommandLength) + " --token=" + secret),
                P(TestLog.Unique("Mix6"), path: $"\"\" --token={secret}"),
            };

            await Runner(starter).LaunchSequenceAsync(programs);

            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("--launch=SECRETX")]
        [InlineData("--launch-extra")]
        [InlineData("--LAUNCH")]
        public void StartSession_OnlyAnExactLaunchFlagCountsAndNothingElseIsLogged(string arg)
        {
            LoggingService.StartSession(new[] { arg });

            var log = TestLog.Read();
            Assert.DoesNotContain("SECRETX", log, StringComparison.Ordinal);
            Assert.DoesNotContain(arg, log, StringComparison.Ordinal);
        }

        [Fact]
        public void StartSession_NullArguments_DoesNotThrow()
        {
            LoggingService.StartSession(null);
            LoggingService.StartSession(Array.Empty<string>());
        }

        // ---------- 2. Escape ----------

        [Fact]
        public void Escape_KeepsSurrogatePairs()
        {
            var text = "a😀b\U0001F9D1\u200D\U0001F4BB";

            Assert.Same(text, LoggingService.Escape(text));
        }

        // Lone surrogates are built in code from "{XXXX}" templates. xUnit serializes string theory data at discovery
        // and turns a lone surrogate into U+FFFD, so the previous InlineData strings never reached Escape as written
        // (the two single-surrogate cases even collapsed into one "duplicate ID"). Phase 4 fix: a lone surrogate is
        // escaped as backslash-uXXXX (File.AppendAllText would otherwise throw and drop the line, see Phase4FixTests).
        [Theory]
        [InlineData("{D800}")]
        [InlineData("{DC00}")]
        [InlineData("x{DBFF}y{DC00}z")] // a high and a low surrogate that are not adjacent
        [InlineData("{DC00}{D800}")]    // reversed pair
        [InlineData("a{D83D}")]          // high surrogate at the end
        public void Escape_LoneSurrogates_AreEscapedAndLoggingDoesNotThrowOrSplit(string template)
        {
            var value = Phase4FixTests.FromTemplate(template, code => ((char)code).ToString());
            var expected = Phase4FixTests.FromTemplate(template, code => @"\u" + code.ToString("X4"));
            var marker = TestLog.Unique("Sur");

            Assert.Equal(expected, LoggingService.Escape(value));
            LoggingService.LogInfo(marker + value + marker);

            var line = Assert.Single(TestLog.LinesContaining(marker));
            Assert.EndsWith(expected + marker, line, StringComparison.Ordinal); // the line is written whole, with the escaped value
        }

        [Fact]
        public void Escape_BackslashN_IsNotEscaped_SoItLooksLikeAnEscapedNewline()
        {
            // Known and accepted: a literal backslash-n and a real newline both read as \n. The log is not
            // machine-parsed, and a real newline can never split a line, which is what matters for forging.
            Assert.Equal(@"\n", LoggingService.Escape(@"\n"));
            Assert.Equal(@"\n", LoggingService.Escape("\n"));
        }

        [Fact]
        public void Escape_ControlHeavyValue_Is6xAtMost_AndFast()
        {
            var value = new string('\u0001', 1_000_000);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var escaped = LoggingService.Escape(value);

            Assert.Equal(6_000_000, escaped.Length);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.ElapsedMilliseconds} ms");
            Assert.DoesNotContain(escaped, ch => char.IsControl(ch));
        }

        [Fact]
        public void Escape_EveryCharacter_NeverLeavesAControlOrSeparator()
        {
            for (int c = 0; c <= 0xFFFF; c++)
            {
                var escaped = LoggingService.Escape("<" + (char)c + ">");
                if (char.IsSurrogate((char)c)) continue;
                foreach (var ch in escaped)
                    Assert.False(char.IsControl(ch) || ch == '\u2028' || ch == '\u2029', $"U+{c:X4} leaked");
            }
        }

        [Fact]
        public void Escape_Idempotent_ForAlreadyEscapedText_DoesNotDoubleEscapeBackslashes()
        {
            Assert.Equal(@"C:\new\table", LoggingService.Escape(@"C:\new\table"));
        }

        // ---------- 3. Concurrent writers on the shared test log ----------

        [Fact]
        public async Task ManyThreads_EveryLine_IsWrittenWhole()
        {
            var marker = TestLog.Unique("Conc");
            const int threads = 8, perThread = 100;

            await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                    LoggingService.LogInfo($"{marker} t{t} i{i} {new string('x', 50)}\tend");
            })));

            var lines = TestLog.LinesContaining(marker);
            Assert.Equal(threads * perThread, lines.Count);
            var shape = new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}\tINFO\tApp\t" + marker + @" t\d+ i\d+ x{50}\\tend$");
            Assert.All(lines, l => Assert.Matches(shape, l));
            Assert.Equal(lines.Count, lines.Distinct().Count());
        }

        // ---------- 5. IndexToReselect ----------

        [Fact]
        public void Reselect_CaseOnlyDuplicates_PrefersTheSameInstance()
        {
            var model = new StartupListModel();
            model.Load(List("Teams", "TEAMS"));

            Assert.Equal(0, model.IndexToReselect(model.Programs[0]));
            Assert.Equal(1, model.IndexToReselect(model.Programs[1]));
        }

        [Fact]
        public void Reselect_CaseOnlyDuplicates_AfterReload_PrefersTheExactName()
        {
            // Registry value names are case-insensitive and the registry service de-duplicates them, so the model
            // never holds such a pair in the app. If it did, an exact (ordinal) name match wins over the first
            // case-insensitive one (Phase 4 review fix; this used to land on "Teams").
            var model = new StartupListModel();
            var old = P("TEAMS");
            model.Load(List("Teams", "TEAMS"));

            Assert.Equal(1, model.IndexToReselect(old));
            Assert.Equal(0, model.IndexToReselect(P("teams"))); // no exact match: first case-insensitive one
        }

        [Fact]
        public void Reselect_EmptyModel_And_EmptyName()
        {
            var model = new StartupListModel();
            Assert.Equal(-1, model.IndexToReselect(P("A")));

            model.Load(new[] { P("") });
            Assert.Equal(0, model.IndexToReselect(P("")));
        }

        // ---------- 6. OpenLogFile through FakeProcessStarter ----------

        [Fact]
        public void OpenLogFile_StartsTheLogThroughTheSeam()
        {
            var starter = new FakeProcessStarter();

            var ok = LoggingService.OpenLogFile(starter, out var error);

            Assert.True(ok);
            Assert.Null(error);
            var psi = Assert.Single(starter.Started);
            Assert.Equal(LoggingService.LogFilePath, psi.FileName);
            Assert.True(psi.UseShellExecute);
            Assert.True(starter.Handles.Single().Disposed);
            Assert.True(File.Exists(LoggingService.LogFilePath));
        }

        [Fact]
        public void OpenLogFile_StartFailure_ReturnsFalseWithTheMessage_AndLogsIt()
        {
            var message = TestLog.Unique("NoEditor");
            var starter = new FakeProcessStarter { ThrowOnStart = new Win32Exception(1155, message) };

            var ok = LoggingService.OpenLogFile(starter, out var error);

            Assert.False(ok);
            Assert.Equal(message, error);
            Assert.Contains(TestLog.LinesContaining(message), l => l.Contains("\tERROR\t") && l.Contains("Could not open the log file"));
        }

        [Fact]
        public void OpenLogFile_NullStarter_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => LoggingService.OpenLogFile(null!, out _));
        }

        // ---------- 4. Parser equivalence ----------

        [Fact]
        public void LinearScan_MatchesThePreviousParser_OnEdgeCases()
        {
            var long32767 = ProgramLauncher.MaxCommandLength;
            var cases = new List<string>
            {
                "", " ", "   ", "\t", "\"", "\"\"", "\"\" x", "\" \"", "\"a.exe", "\"a.exe\" -y", "\"a b.exe\" -y", "\"a b\"",
                "a.exe", "a.exe ", " a.exe", "a.exe.", "a.exe..", "a.exe. ", "a.exe . .", "a. exe", "a .exe", ".exe", " .exe", "..exe",
                "a.exe -y", "a.exe\t-y", "a.exe\t", "\ta.exe", "a\t.exe", "a.exe\u00A0-y", "a.exe\u2003 b", "a\u00A0.exe b", "\u00A0",
                "é.exe -y", "日本語.exe arg", "😀.exe x", "a.ex\u0065 -y", "a.ln\u212A b", "A.EXE b", "a.ExE. b",
                @"C:\Program Files\App\app.exe -y", @"C:\Program Files\App\app -y", @"C:\Program Files\App\app", @"C:\a b\c d\e.cmd /x",
                @"C:\a.exe\b c", @"C:\a.exe.\b", @"C:\.exe", @"C:\x.exe:stream y", "a.exe.exe.exe b", "x .com .bat .lnk",
                "helper /x", "helper", "helper.", "helper . ", "a  .exe", "a.\texe b",
                new string('a', long32767),
                new string(' ', long32767),
                new string('.', long32767),
                new string('a', long32767 - 4) + ".exe",
                new string('a', long32767 - 8) + ".exe -y",
                "\"" + new string('a', long32767 - 1),
                string.Concat(Enumerable.Repeat("a ", long32767 / 2)),
                string.Concat(Enumerable.Repeat(". ", long32767 / 2)),
                string.Concat(Enumerable.Repeat("a.exe ", long32767 / 6)),
                @"C:\" + string.Concat(Enumerable.Repeat("d ", 16000)) + "e.exe -y",
            };
            var existence = new Func<string, bool>[] { _ => false, _ => true, p => p.Length % 3 == 0, p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) };

            foreach (var command in cases)
            {
                foreach (var exists in existence)
                {
                    var expected = Phase4Tests.ReferenceParse(command, exists);
                    var actual = CommandLineParser.Parse(command, exists, expandEnvironment: false);
                    Assert.True(expected == actual, $"'{Preview(command)}': expected {Preview(expected.ToString())}, got {Preview(actual.ToString())}");
                }
            }
        }

        [Fact]
        public void LinearScan_MatchesThePreviousParser_RandomWithUnicodeAndWhitespace()
        {
            var tokens = new[]
            {
                "a", "é", "日", "😀", "\uD800", ".exe", ".EXE", ".cmd", ".Lnk", ".com", ".bat", ".ex", ".exee", "exe", ".ln\u212A",
                ".", "..", " ", "  ", "\t", "\u00A0", "\u2003", "\u3000", "\r", "\n", "\0", @"\", "/", ":", "C:\\", "\"", "-y", "%", "x.exe ",
            };
            var random = new Random(20261001);
            int withExecutable = 0;
            for (int n = 0; n < 60000; n++)
            {
                var command = string.Concat(Enumerable.Range(0, random.Next(0, 14)).Select(_ => tokens[random.Next(tokens.Length)]));
                Func<string, bool> exists = (n % 3) switch { 0 => p => p.Length % 3 == 0, 1 => _ => true, _ => _ => false };

                var expected = Phase4Tests.ReferenceParse(command, exists);
                var actual = CommandLineParser.Parse(command, exists, expandEnvironment: false);

                Assert.True(expected == actual, $"'{Preview(command)}': expected {Preview(expected.ToString())}, got {Preview(actual.ToString())}");
                if (CommandLineParser.HasExecutableExtension(actual.ExePath)) withExecutable++;
            }

            Assert.True(withExecutable > 5000, $"only {withExecutable} inputs reached an executable token");
        }

        private static string Preview(string text) => text.Length > 80 ? text.Substring(0, 80) + $"...({text.Length})" : text.Replace("\t", "\\t").Replace("\0", "\\0");
    }

    // The logger is process-wide. These tests point it at their own folder, so they must not overlap with any other
    // test; xunit runs this collection after the parallel ones.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class LoggerRedirectCollection
    {
        public const string Name = "LoggerRedirect";
    }

    [Collection(LoggerRedirectCollection.Name)]
    public sealed class Phase4RotationTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "StartupController.Tests", "redirect-" + Guid.NewGuid().ToString("N"));

        public Phase4RotationTests()
        {
            LoggingService.Initialize(_dir);
        }

        public void Dispose()
        {
            LoggingService.Initialize(TestLogSetup.LogDirectory);
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        }

        private static string[] LogFiles() =>
            Directory.Exists(Path.GetDirectoryName(LoggingService.LogFilePath)!)
                ? Directory.GetFiles(Path.GetDirectoryName(LoggingService.LogFilePath)!).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).Select(n => n!).ToArray()
                : Array.Empty<string>();

        [Fact]
        public void ExactlyAtTheLimit_Rotates_OneByteBelow_DoesNot()
        {
            var log = LoggingService.LogFilePath;
            File.WriteAllBytes(log, new byte[LoggingService.MaxLogBytes - 1]);
            Assert.False(LoggingService.RotateIfNeeded(log, LoggingService.MaxLogBytes, LoggingService.KeptLogFiles));

            File.WriteAllBytes(log, new byte[LoggingService.MaxLogBytes]);
            Assert.True(LoggingService.RotateIfNeeded(log, LoggingService.MaxLogBytes, LoggingService.KeptLogFiles));
            Assert.False(File.Exists(log));
            Assert.Equal(LoggingService.MaxLogBytes, new FileInfo(LoggingService.RotatedPath(log, 1)).Length);
        }

        [Fact]
        public void NextAppendAfterTheLimit_StartsAFreshFile()
        {
            var log = LoggingService.LogFilePath;
            File.WriteAllBytes(log, new byte[LoggingService.MaxLogBytes]);

            LoggingService.LogInfo("after rotation");

            Assert.True(new FileInfo(log).Length < 1000);
            Assert.Equal(LoggingService.MaxLogBytes, new FileInfo(LoggingService.RotatedPath(log, 1)).Length);
        }

        [Fact]
        public async Task ManyThreadsAcrossSeveralRotations_KeepThreeFiles_AndWholeLines_AndNoDuplicates()
        {
            const int threads = 4, perThread = 100;
            var filler = new string('x', 8000);

            await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                    LoggingService.LogInfo($"T{t} I{i} {filler}");
            })));

            var names = LogFiles();
            Assert.Contains("startupcontroller.log", names);
            Assert.Contains("startupcontroller.1.log", names);
            Assert.True(names.Length <= LoggingService.KeptLogFiles, string.Join(", ", names));
            var dir = Path.GetDirectoryName(LoggingService.LogFilePath)!;
            foreach (var name in names)
                Assert.True(new FileInfo(Path.Combine(dir, name)).Length < LoggingService.MaxLogBytes + 9000, name + " grew past the limit");

            // oldest first: .2, .1, current
            var text = new System.Text.StringBuilder();
            for (int i = LoggingService.KeptLogFiles - 1; i >= 1; i--)
                if (File.Exists(LoggingService.RotatedPath(LoggingService.LogFilePath, i))) text.Append(File.ReadAllText(LoggingService.RotatedPath(LoggingService.LogFilePath, i)));
            text.Append(File.ReadAllText(LoggingService.LogFilePath));

            var shape = new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}\t(INFO)\t(App|Logger)\t(Logger initialized|T(\d+) I(\d+) x{8000})$");
            var perThreadIndices = new Dictionary<int, List<int>>();
            foreach (var line in text.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var m = shape.Match(line);
                Assert.True(m.Success, "malformed line: " + line);
                if (m.Groups[4].Success)
                {
                    var t = int.Parse(m.Groups[4].Value);
                    if (!perThreadIndices.TryGetValue(t, out var list)) perThreadIndices[t] = list = new List<int>();
                    list.Add(int.Parse(m.Groups[5].Value));
                }
            }

            foreach (var (t, indices) in perThreadIndices)
            {
                Assert.Equal(indices.Count, indices.Distinct().Count());
                Assert.Equal(indices.OrderBy(i => i), indices); // never reordered
                Assert.Equal(perThread - 1, indices[^1]);       // the newest lines survive
                Assert.Equal(indices.Count, indices[^1] - indices[0] + 1); // a contiguous tail: only the oldest were dropped
            }
        }

        [Fact]
        public void LockedFirstRotatedFile_RotationIsSkipped_NothingIsLost_AndLoggingContinues()
        {
            var log = LoggingService.LogFilePath;
            File.WriteAllText(log, "current");
            File.WriteAllText(LoggingService.RotatedPath(log, 1), "one");
            File.WriteAllText(LoggingService.RotatedPath(log, 2), "two");

            bool rotated;
            using (new FileStream(LoggingService.RotatedPath(log, 1), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rotated = LoggingService.RotateIfNeeded(log, 1, 3);
            }

            Assert.False(rotated);
            Assert.Equal("current", File.ReadAllText(log));
            Assert.Equal("one", File.ReadAllText(LoggingService.RotatedPath(log, 1)));
            Assert.Equal("two", File.ReadAllText(LoggingService.RotatedPath(log, 2)));
        }

        [Fact]
        public void LockedSecondRotatedFile_RotationIsSkipped_NothingIsLost()
        {
            var log = LoggingService.LogFilePath;
            File.WriteAllText(log, "current");
            File.WriteAllText(LoggingService.RotatedPath(log, 1), "one");
            File.WriteAllText(LoggingService.RotatedPath(log, 2), "two");

            bool rotated;
            using (new FileStream(LoggingService.RotatedPath(log, 2), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rotated = LoggingService.RotateIfNeeded(log, 1, 3);
            }

            Assert.False(rotated);
            Assert.Equal("current", File.ReadAllText(log));
            Assert.Equal("one", File.ReadAllText(LoggingService.RotatedPath(log, 1)));
        }

        [Fact]
        public void AppendWithALockedRotatedFile_DoesNotThrow_AndStillWrites()
        {
            var log = LoggingService.LogFilePath;
            File.WriteAllBytes(log, new byte[LoggingService.MaxLogBytes]);
            File.WriteAllText(LoggingService.RotatedPath(log, 1), "one");
            File.WriteAllText(LoggingService.RotatedPath(log, 2), "two");

            using (new FileStream(LoggingService.RotatedPath(log, 2), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                LoggingService.LogInfo("still written");
            }

            Assert.Contains("still written", File.ReadAllText(log, System.Text.Encoding.UTF8).Substring((int)LoggingService.MaxLogBytes), StringComparison.Ordinal);
        }

        [Fact]
        public void MissingDirectory_RotationAndLoggingDoNotThrow()
        {
            Directory.Delete(_dir, recursive: true);

            Assert.False(LoggingService.RotateIfNeeded(LoggingService.LogFilePath, 1, 3));
            LoggingService.LogInfo("into the void");
            LoggingService.LogError("also void", new IOException("x"));
        }

        [Fact]
        public void MissingDirectory_LoggingRecoversOnTheNextLine()
        {
            Directory.Delete(_dir, recursive: true);

            LoggingService.LogInfo("recovered");

            Assert.Contains("recovered", File.ReadAllText(LoggingService.LogFilePath), StringComparison.Ordinal);
        }

        [Fact]
        public void OpenLogFile_MissingDirectory_ReturnsFalse_AndNeverStarts()
        {
            Directory.Delete(_dir, recursive: true);
            var starter = new FakeProcessStarter();

            var ok = LoggingService.OpenLogFile(starter, out var error);

            Assert.False(ok);
            Assert.False(string.IsNullOrEmpty(error));
            Assert.Empty(starter.Started);
        }
    }
}
