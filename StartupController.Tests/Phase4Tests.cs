using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Phase 4 (Low / cleanup): logging (item 18, security L2 and I3), selection after refresh (20),
    // Program/Form1 cleanup (17), path helper and the linear parser scan (I-3).
    // FakeProcessStarter only; rotation runs in its own temp folder, never on the shared test log.
    public class Phase4Tests
    {
        private const string Self = @"C:\Program Files\StartupController\StartupController.exe";

        private static ProgramLauncher Launcher(FakeProcessStarter starter) => new ProgramLauncher(starter, Self, p => p, (a, b) => false);

        private static LaunchRunner Runner(FakeProcessStarter starter) =>
            new LaunchRunner(Launcher(starter), new FakeNotifier(), new FakeDialog(), launch => Task.FromResult(launch()));

        private static string LaunchLine(string name) =>
            Assert.Single(TestLog.LinesContaining(name), l => l.Contains("\tLAUNCH\t"));

        // ---------- 18a: no command-line arguments in the log ----------

        [Theory]
        [InlineData("\"C:\\Apps\\x.exe\" --token={0}", @"C:\Apps\x.exe", "SUCCESS")]
        [InlineData(@"C:\Apps\x.exe --token={0}", @"C:\Apps\x.exe", "SUCCESS")]
        [InlineData(@"C:\Missing\x.exe --token={0}", @"C:\Missing\x.exe", "FAILURE")]
        [InlineData("helper.exe --token={0}", "helper.exe", "SUCCESS")]
        public async Task LaunchLine_HasNameAndExe_ButNoArguments(string command, string exe, string outcome)
        {
            var name = TestLog.Unique("Args");
            var secret = TestLog.Unique("SECRET");
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe");

            await Runner(starter).LaunchManualAsync(P(name, path: string.Format(command, secret)));

            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
            var fields = LaunchLine(name).Split('\t');
            Assert.Equal(new[] { "LAUNCH", "Program", name, exe, outcome }, fields.Skip(1).Take(5));
        }

        [Fact]
        public async Task ArgumentsAreStillPassedToTheProgram()
        {
            var secret = TestLog.Unique("SECRET");
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe");

            await Runner(starter).LaunchManualAsync(P(TestLog.Unique("Pass"), path: $"\"C:\\Apps\\x.exe\" --token={secret}"));

            Assert.Equal("--token=" + secret, Assert.Single(starter.Started).Arguments);
        }

        [Fact]
        public async Task ProcessStartLine_LogsTheArgumentLengthOnly()
        {
            var name = TestLog.Unique("Len");
            var secret = TestLog.Unique("SECRET");
            var exe = $@"C:\Apps\{name}.exe";
            var starter = new FakeProcessStarter(exe);

            await Runner(starter).LaunchManualAsync(P(name, path: $"\"{exe}\" --token={secret}"));

            var line = Assert.Single(TestLog.LinesContaining(exe), l => l.Contains("Process start"));
            Assert.Contains($"Args=<{("--token=" + secret).Length} chars>", line, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
        }

        [Fact]
        public async Task StartFailure_IsLoggedWithoutArguments()
        {
            var name = TestLog.Unique("Throw");
            var secret = TestLog.Unique("SECRET");
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe") { ThrowOnStart = new Win32Exception(5) };

            var result = await Runner(starter).LaunchManualAsync(P(name, path: $@"C:\Apps\x.exe /p {secret}"));

            Assert.False(result.Success);
            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
            Assert.Contains(@"C:\Apps\x.exe", LaunchLine(name), StringComparison.Ordinal);
        }

        [Fact]
        public async Task BlockedEntry_IsLoggedByNameOnly()
        {
            var name = TestLog.Unique("Blocked");
            var secret = TestLog.Unique("SECRET");

            var result = await Runner(new FakeProcessStarter()).LaunchManualAsync(P(name, path: $@"C:\x\StartupController.exe --launch {secret}"));

            Assert.True(result.Blocked);
            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
            Assert.DoesNotContain(TestLog.LinesContaining(name), l => l.Contains("\tLAUNCH\t"));
        }

        [Fact]
        public async Task TimedOutLaunch_IsLoggedByNameOnly()
        {
            var name = TestLog.Unique("Slow");
            var secret = TestLog.Unique("SECRET");
            var never = new TaskCompletionSource<LaunchResult>();
            var runner = new LaunchRunner(Launcher(new FakeProcessStarter()), new FakeNotifier(), new FakeDialog(),
                launch => never.Task, TimeSpan.FromMilliseconds(100));

            var summary = await runner.LaunchSequenceAsync(new[] { P(name, path: $@"C:\Apps\slow.exe --token={secret}") });

            Assert.Equal(1, summary.Failed);
            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
            var fields = LaunchLine(name).Split('\t');
            Assert.Equal(new[] { name, "", "FAILURE" }, fields.Skip(3).Take(3));
        }

        [Fact]
        public void LaunchResult_CarriesTheParsedExe()
        {
            var result = Launcher(new FakeProcessStarter(@"C:\Apps\x.exe")).Launch(P("X", path: @"C:\Apps\x.exe -y"));

            Assert.Equal(@"C:\Apps\x.exe", result.ExePath);
        }

        // ---------- 18b: StartSession logs --launch yes/no only ----------

        [Fact]
        public void StartSession_LogsLaunchFlag_ButNoArguments()
        {
            var secret = TestLog.Unique("SECRET");

            LoggingService.StartSession(new[] { "--launch", "--token=" + secret });

            var log = TestLog.Read();
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
            Assert.Contains(log.Split('\n'), l => l.Contains("\tSESSION\t") && l.Contains("| --launch: yes ==="));
        }

        [Fact]
        public void StartSession_WithoutLaunch_SaysNo()
        {
            var secret = TestLog.Unique("SECRET");

            LoggingService.StartSession(new[] { secret });

            var log = TestLog.Read();
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
            Assert.Contains(log.Split('\n'), l => l.Contains("\tSESSION\t") && l.Contains("| --launch: no ==="));
        }

        // ---------- 18c: every field is escaped, one call is one line ----------

        [Fact]
        public void ControlCharacters_AreEscaped_OnOneLine()
        {
            var marker = TestLog.Unique("Esc");

            LoggingService.LogInfo($"{marker}\tx\r\n2026-01-01 00:00:00.000\tERROR\tApp\tforged");

            var line = Assert.Single(TestLog.LinesContaining(marker));
            Assert.EndsWith(marker + @"\tx\r\n2026-01-01 00:00:00.000\tERROR\tApp\tforged", line, StringComparison.Ordinal);
            Assert.DoesNotContain(TestLog.Read().Split('\n'), l => l.StartsWith("2026-01-01 00:00:00.000", StringComparison.Ordinal));
        }

        [Fact]
        public async Task LaunchLine_NameWithTabAndNewline_KeepsItsFields()
        {
            var marker = TestLog.Unique("Tab");
            var name = marker + "\tA\r\nB";

            await Runner(new FakeProcessStarter(@"C:\Apps\x.exe")).LaunchManualAsync(P(name, path: @"C:\Apps\x.exe"));

            var fields = LaunchLine(marker).Split('\t');
            Assert.Equal(7, fields.Length); // timestamp, LAUNCH, Program, name, exe, result, details
            Assert.Equal(marker + @"\tA\r\nB", fields[3]);
            Assert.Equal(@"C:\Apps\x.exe", fields[4]);
        }

        [Theory]
        [InlineData("a\u0001b", @"a\u0001b")]
        [InlineData("\u001B[31m", @"\u001B[31m")]
        [InlineData("x\u007Fy", @"x\u007Fy")]
        [InlineData("x\u0085y", @"x\u0085y")]
        [InlineData("x\u2028y\u2029", @"x\u2028y\u2029")]
        [InlineData("\r\n\t", @"\r\n\t")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void Escape_Rules(string? input, string expected)
        {
            Assert.Equal(expected, LoggingService.Escape(input));
        }

        [Fact]
        public void Escape_LeavesPlainTextAndPathsAlone()
        {
            var text = @"C:\Program Files\App\app.exe – ü";

            Assert.Same(text, LoggingService.Escape(text));
        }

        [Fact]
        public void ErrorDetails_AreEscaped()
        {
            var marker = TestLog.Unique("ErrEsc");

            LoggingService.LogError(marker, new IOException("line1\r\nline2"));

            Assert.Contains(@"line1\r\nline2", Assert.Single(TestLog.LinesContaining(marker)), StringComparison.Ordinal);
        }

        // ---------- 18d: rotation at 1 MB, three files kept ----------

        [Fact]
        public void Rotation_Limits()
        {
            Assert.Equal(1024 * 1024, LoggingService.MaxLogBytes);
            Assert.Equal(3, LoggingService.KeptLogFiles);
        }

        [Fact]
        public void RotatedPath_InsertsTheIndexBeforeTheExtension()
        {
            Assert.Equal(@"C:\logs\startupcontroller.2.log", LoggingService.RotatedPath(@"C:\logs\startupcontroller.log", 2));
        }

        [Fact]
        public void BelowTheThreshold_NothingRotates()
        {
            using var dir = new TempDir();
            var log = dir.File("startupcontroller.log");
            File.WriteAllText(log, new string('a', 99));

            Assert.False(LoggingService.RotateIfNeeded(log, 100, 3));
            Assert.Equal(new[] { "startupcontroller.log" }, dir.Names());
        }

        [Fact]
        public void MissingLog_NothingRotates()
        {
            using var dir = new TempDir();

            Assert.False(LoggingService.RotateIfNeeded(dir.File("startupcontroller.log"), 100, 3));
            Assert.Empty(dir.Names());
        }

        [Fact]
        public void AtTheThreshold_KeepsExactlyThreeFiles_NewestFirst()
        {
            using var dir = new TempDir();
            var log = dir.File("startupcontroller.log");

            for (int generation = 1; generation <= 5; generation++)
            {
                File.WriteAllText(log, Generation(generation));
                Assert.True(LoggingService.RotateIfNeeded(log, 100, 3));
            }
            File.WriteAllText(log, Generation(6)); // the next write starts a new file

            Assert.Equal(new[] { "startupcontroller.1.log", "startupcontroller.2.log", "startupcontroller.log" }, dir.Names());
            Assert.Equal(Generation(6), File.ReadAllText(log));
            Assert.Equal(Generation(5), File.ReadAllText(LoggingService.RotatedPath(log, 1)));
            Assert.Equal(Generation(4), File.ReadAllText(LoggingService.RotatedPath(log, 2)));
        }

        [Fact]
        public void RotationFailure_IsSwallowed()
        {
            using var dir = new TempDir();
            var log = dir.File("startupcontroller.log");
            File.WriteAllText(log, Generation(1));

            bool rotated;
            using (new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rotated = LoggingService.RotateIfNeeded(log, 100, 3);
            }

            Assert.False(rotated);
            Assert.Equal(Generation(1), File.ReadAllText(log));
        }

        [Fact]
        public void AppendLine_RotatesUnderTheLockBeforeWriting()
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "LoggingService.cs"));

            // Rotation (with its backoff) and the write both happen under the lock, rotation first
            Assert.Matches(@"lock\s*\(\s*_lock\s*\)\s*\{\s*RotateWithBackoff\(\s*\)\s*;\s*try\s*\{\s*File\.AppendAllText", code);
            Assert.Matches(@"RotateIfNeeded\(\s*_logFile\s*,\s*MaxLogBytes\s*,\s*KeptLogFiles\s*\)", code);
        }

        private static string Generation(int n) => $"gen{n}".PadRight(100, '.');

        // ---------- 18e: OpenLogFile goes through the seam ----------

        [Fact]
        public void OpenLogFile_TakesTheProcessSeam_AndDoesNotSwallowSilently()
        {
            var method = typeof(LoggingService).GetMethod(nameof(LoggingService.OpenLogFile))!;
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "LoggingService.cs"));

            Assert.Equal(typeof(IProcessStarter), method.GetParameters()[0].ParameterType);
            Assert.Equal(typeof(bool), method.ReturnType);
            Assert.Contains("LogError(\"Could not open the log file\", ex)", code, StringComparison.Ordinal);
        }

        // ---------- 20: selection survives a rebuild of the list ----------

        [Fact]
        public void Reselect_SameInstance_FollowsItsNewPosition()
        {
            var model = new StartupListModel();
            model.Load(List("A", "B", "C"));
            var c = model.Programs[2];

            model.MoveTop(c);

            Assert.Equal(0, model.IndexToReselect(c));
        }

        [Fact]
        public void Reselect_AfterReload_MatchesTheNameIgnoringCase()
        {
            var model = new StartupListModel();
            var old = P("Teams");
            model.Load(List("A", "TEAMS", "C"));

            Assert.Equal(1, model.IndexToReselect(old));
        }

        [Fact]
        public void Reselect_NothingOrMissing_IsMinusOne()
        {
            var model = new StartupListModel();
            model.Load(List("A"));

            Assert.Equal(-1, model.IndexToReselect(null));
            Assert.Equal(-1, model.IndexToReselect(P("Gone")));
        }

        [Fact]
        public void Reselect_AfterToggle_KeepsTheProgram()
        {
            var model = new StartupListModel();
            model.Load(List("A", "B"));
            var b = model.Programs[1];

            Assert.True(model.Toggle(b));

            Assert.Equal(1, model.IndexToReselect(b));
        }

        // ---------- 17: Program and Form1 cleanup ----------

        [Fact]
        public void Main_IsSynchronous_AndStillSta()
        {
            var main = typeof(Program).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;

            Assert.Equal(typeof(void), main.ReturnType);
            Assert.True(main.IsDefined(typeof(STAThreadAttribute)));
            Assert.Null(typeof(Program).GetMethod("Form_Load", BindingFlags.NonPublic | BindingFlags.Static));
        }

        [Fact]
        public void LaunchFromStartup_IsAnInternalProperty()
        {
            var property = typeof(Form1).GetProperty("LaunchFromStartup", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(property);
            Assert.Null(typeof(Form1).GetField("LaunchFromStartup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        }

        [Fact]
        public void Form1_HasNoNullableSuppressionsOrDeadCode()
        {
            var raw = File.ReadAllText(Path.Combine(SourceScan.ProductionSourceDirectory(), "Form1.cs"));

            Assert.DoesNotContain("#pragma warning disable CS8602", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Net.WebSockets", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("/*", raw, StringComparison.Ordinal);
        }

        // ---------- Path helper ----------

        [Fact]
        public void ProgramLauncher_NeedsNoWinFormsOrInterop()
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "ProgramLauncher.cs"));

            Assert.DoesNotContain("Application.", code, StringComparison.Ordinal);
            Assert.DoesNotContain("DllImport", code, StringComparison.Ordinal);
        }

        [Fact]
        public void PInvokes_LiveOnlyInNativeMethods()
        {
            var offenders = SourceScan.FilesMatching(SourceScan.ProductionSourceDirectory(), @"\[\s*(DllImport|LibraryImport)\b", "NativeMethods.cs");

            Assert.True(offenders.Count == 0, "P/Invoke outside NativeMethods: " + string.Join(", ", offenders));
        }

        [Fact]
        public void StripDevicePrefix_Unchanged()
        {
            Assert.Equal(@"C:\x", PathHelper.StripDevicePrefix(@"\\?\C:\x"));
            Assert.Equal(@"\\srv\share", PathHelper.StripDevicePrefix(@"\\?\UNC\srv\share"));
            Assert.Equal(@"C:\x", PathHelper.StripDevicePrefix(@"\\.\C:\x"));
            Assert.Equal(@"C:\x", PathHelper.StripDevicePrefix(@"C:\x"));
        }

        // ---------- I-3: linear shortest-first scan ----------

        [Fact]
        public void LongestAllowedCommand_ParsesQuickly()
        {
            // Worst case for a prefix-rebuilding scan: many split points, and trailing dots/spaces to trim at each
            var command = @"C:\a" + string.Concat(Enumerable.Repeat(". ", (ProgramLauncher.MaxCommandLength - 6) / 2)) + "x";
            Assert.True(command.Length <= ProgramLauncher.MaxCommandLength);

            var sw = Stopwatch.StartNew();
            var parsed = CommandLineParser.Parse(command, _ => false, expandEnvironment: false);

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.ElapsedMilliseconds} ms");
            Assert.Equal(command, parsed.ExePath);
        }

        [Fact]
        public void LongCommandWithExeAtTheEnd_ParsesQuickly()
        {
            var command = string.Concat(Enumerable.Repeat("a ", 16000)) + @"b.exe -y";

            var sw = Stopwatch.StartNew();
            var parsed = CommandLineParser.Parse(command, _ => false, expandEnvironment: false);

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.ElapsedMilliseconds} ms");
            Assert.EndsWith("b.exe", parsed.ExePath, StringComparison.Ordinal);
            Assert.Equal("-y", parsed.Arguments);
        }

        [Fact]
        public void LinearScan_MatchesThePreviousParser()
        {
            // Tokens, so executable extensions, trailing dots/spaces, tabs and quotes all show up often
            var tokens = new[] { "a", "x", ".exe", ".EXE", ".cmd", ".Lnk", ".com", ".bat", ".ex", "exe", ".", " ", "  ", "\t", @"\", "/", "C:", "\"", "-y" };
            var random = new Random(4242);
            int withExecutable = 0;
            for (int n = 0; n < 20000; n++)
            {
                var command = string.Concat(Enumerable.Range(0, random.Next(0, 10)).Select(_ => tokens[random.Next(tokens.Length)]));
                Func<string, bool> exists = p => p.Length % 3 == 0;

                var expected = ReferenceParse(command, exists);
                var actual = CommandLineParser.Parse(command, exists, expandEnvironment: false);

                Assert.True(expected == actual, $"'{command}': expected {expected}, got {actual}");
                if (CommandLineParser.HasExecutableExtension(actual.ExePath)) withExecutable++;
            }

            Assert.True(withExecutable > 2000, $"only {withExecutable} inputs reached an executable token");
        }

        // CommandLineParser.Parse before I-3 (prefix rebuilt with Substring at every space), without expansion
        internal static CommandLineParser.Parsed ReferenceParse(string command, Func<string, bool> fileExists)
        {
            if (string.IsNullOrWhiteSpace(command))
                return new CommandLineParser.Parsed("", "", false);

            int probes = 0;
            bool Probe(string path)
            {
                if (probes >= CommandLineParser.MaxProbes || !CommandLineParser.IsFullyQualified(path)) return false;
                probes++;
                return fileExists(path);
            }

            command = command.Trim();

            bool unmatchedQuote = false;
            if (command.StartsWith('"'))
            {
                var endQuote = command.IndexOf('"', 1);
                if (endQuote > 0)
                {
                    var exe = command.Substring(1, endQuote - 1).Trim();
                    var args = command.Substring(endQuote + 1).Trim();
                    return new CommandLineParser.Parsed(CommandLineParser.WithExeProbe(exe, Probe) ?? exe, args, false);
                }

                unmatchedQuote = true;
                command = command.Substring(1).Trim();
                if (command.Length == 0)
                    return new CommandLineParser.Parsed("", "", false);
            }

            int end = 0;
            while (end < command.Length)
            {
                int space = command.IndexOf(' ', end);
                end = space < 0 ? command.Length : space;
                var candidate = command.Substring(0, end).TrimEnd();
                if (candidate.Length > 0 && CommandLineParser.HasExecutableExtension(candidate))
                {
                    var args = command.Substring(end).Trim();
                    return new CommandLineParser.Parsed(candidate, args, candidate.Contains(' '));
                }
                end++;
            }

            var whole = CommandLineParser.WithExeProbe(command, Probe);
            if (whole != null && whole != command)
                return new CommandLineParser.Parsed(whole, "", command.Contains(' '));

            if (unmatchedQuote)
                return new CommandLineParser.Parsed(command, "", false);

            var firstSpace = command.IndexOf(' ');
            if (firstSpace > 0 && CommandLineParser.IsBareFileName(command.Substring(0, firstSpace)))
                return new CommandLineParser.Parsed(command.Substring(0, firstSpace), command.Substring(firstSpace + 1).Trim(), false);

            return new CommandLineParser.Parsed(command, "", false);
        }

        // ---------- Help text ----------

        [Fact]
        public void HelpText_CoversTheMainTopics()
        {
            var changed = new StartupProgram { Name = "X", Path = "x", Enabled = false, Description = "", Changed = true };

            var help = string.Join("\n", HelpContent.LoadEmbedded().Select(s => s.Title + "\n" + s.Body));

            Assert.Contains(Form1.StatusText(changed), help, StringComparison.Ordinal);
            foreach (var topic in new[] { "Task Manager", "Enabled means", "↑", "↓", "⇈", "⇊", "Save Order", "Autosave on change",
                                          "Executable not found", "in quotes", "View Logs" })
                Assert.Contains(topic, help, StringComparison.Ordinal);
        }

        private sealed class TempDir : IDisposable
        {
            private readonly string _path = Path.Combine(Path.GetTempPath(), "StartupController.Tests", "rotate-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(_path);

            public string File(string name) => Path.Combine(_path, name);

            public string[] Names() => Directory.GetFiles(_path).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            public void Dispose()
            {
                try { Directory.Delete(_path, recursive: true); } catch (IOException) { /* best effort */ }
            }
        }
    }
}
