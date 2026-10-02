using System.ComponentModel;
using System.Text;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Phase 4 review fixes: no arguments in the log when the parser keeps a command whole (security L2), lone
    // surrogates and format characters in log fields (L1, I2), and the redacted exception text. FakeProcessStarter only.
    public class Phase4FixTests
    {
        private const string Self = @"C:\Program Files\StartupController\StartupController.exe";

        private static ProgramLauncher Launcher(FakeProcessStarter starter) => new ProgramLauncher(starter, Self, p => p, (a, b) => false);

        private static LaunchRunner Runner(FakeProcessStarter starter, FakeNotifier? notifier = null) =>
            new LaunchRunner(Launcher(starter), notifier ?? new FakeNotifier(), new FakeDialog(), launch => Task.FromResult(launch()));

        // "x{D800}y" -> "x" + map(0xD800) + "y": builds strings with lone surrogates in code, because xUnit turns them
        // into U+FFFD when it serializes theory data
        internal static string FromTemplate(string template, Func<int, string> map)
        {
            var text = new StringBuilder();
            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] == '{')
                {
                    int close = template.IndexOf('}', i);
                    text.Append(map(Convert.ToInt32(template.Substring(i + 1, close - i - 1), 16)));
                    i = close;
                }
                else
                {
                    text.Append(template[i]);
                }
            }
            return text.ToString();
        }

        private static string Escaped(int code) => @"\u" + code.ToString("X4");

        // ---------- 1. LoggableExe: arguments never reach the log or the notification ----------

        // The two security-analyser scenarios: no executable token (whole command kept), and an "executable" token that
        // is really the end of an argument. Neither file exists.
        [Theory]
        [InlineData(@"C:\Tools\mytool --token={0}", @"C:\Tools\mytool")]
        [InlineData(@"C:\Tools\sync --key={0}.cmd", @"C:\Tools\sync")]
        public async Task CommandKeptWhole_LogAndFailureMessage_HaveNoSecret(string template, string firstToken)
        {
            var name = TestLog.Unique("Whole");
            var secret = TestLog.Unique("SECRET");
            var command = string.Format(template, secret);
            var notifier = new FakeNotifier();

            var result = await Runner(new FakeProcessStarter(), notifier).LaunchManualAsync(P(name, path: command));

            Assert.False(result.Success);
            Assert.True(result.NotFound);
            Assert.Equal($"{firstToken} <+{command.Length - firstToken.Length} chars>", result.ExePath);
            Assert.DoesNotContain(secret, result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, LaunchRunner.FailureMessage(P(name, path: command), result), StringComparison.Ordinal);
            Assert.All(notifier.Messages, m => Assert.DoesNotContain(secret, m, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
            Assert.Contains(TestLog.LinesContaining(name), l => l.Contains("\tLAUNCH\t") && l.Contains(firstToken + " <+"));
        }

        [Fact]
        public void UnquotedWithSpacesWarning_ShowsOnlyTheFirstTokenOfAMissingExe()
        {
            var name = TestLog.Unique("Warn");
            var secret = TestLog.Unique("SECRET");

            Launcher(new FakeProcessStarter()).Launch(P(name, path: $@"C:\Tools\sync --key={secret}.cmd"));

            var warning = Assert.Single(TestLog.LinesContaining(name), l => l.Contains("unquoted path with spaces"));
            Assert.Contains(@"C:\Tools\sync <+", warning, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
        }

        [Fact]
        public void LoggableExe_ExistingFileWithSpaces_IsShownInFull()
        {
            var exe = @"C:\Program Files\App\app.exe";
            var launcher = Launcher(new FakeProcessStarter(exe));

            Assert.Equal(exe, launcher.LoggableExe(exe));

            var result = launcher.Launch(P(TestLog.Unique("Full"), path: exe + " -y"));
            Assert.True(result.Success);
            Assert.Equal(exe, result.ExePath);
        }

        [Theory]
        [InlineData(@"C:\Apps\x.exe", @"C:\Apps\x.exe")]                      // no whitespace: unchanged
        [InlineData(@"C:\Tools v2\mytool --a", @"C:\Tools <+14 chars>")]     // missing, fully qualified
        [InlineData(@"sub\mytool --token=abc", @"sub\mytool <+12 chars>")]  // relative: never probed, never shown whole
        public void LoggableExe_KeepsOnlyTheFirstTokenOfAMissingExe(string exePath, string expected)
        {
            Assert.Equal(expected, Launcher(new FakeProcessStarter()).LoggableExe(exePath));
        }

        [Fact]
        public void LoggableExe_SplitsAtAnyWhitespace_NotJustSpaces()
        {
            var exePath = @"C:\Tools\mytool" + (char)9 + "--token=abc";

            Assert.Equal(@"C:\Tools\mytool <+12 chars>", Launcher(new FakeProcessStarter()).LoggableExe(exePath));
        }

        [Fact]
        public async Task StartException_EchoingTheCommand_IsRedacted()
        {
            var name = TestLog.Unique("Echo");
            var secret = TestLog.Unique("SECRET");
            var command = $@"C:\Apps\x.exe --token={secret}";
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe") { ThrowOnStart = new Win32Exception(5, $"Cannot start {command}") };
            var notifier = new FakeNotifier();

            var result = await Runner(starter, notifier).LaunchManualAsync(P(name, path: command));

            Assert.False(result.Success);
            Assert.Equal("Cannot start <command>", result.Error);
            Assert.All(notifier.Messages, m => Assert.DoesNotContain(secret, m, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, TestLog.Read(), StringComparison.Ordinal);
        }

        [Fact]
        public void StartException_EchoingTheArguments_IsRedacted_AndLongTextIsCapped()
        {
            var secret = TestLog.Unique("SECRET");
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe")
            {
                ThrowOnStart = new Win32Exception(5, $"bad argument --token={secret} " + new string('z', 2000))
            };

            var result = Launcher(starter).Launch(P(TestLog.Unique("Args"), path: $@"C:\Apps\x.exe --token={secret}"));

            Assert.StartsWith("bad argument <arguments> ", result.Error, StringComparison.Ordinal);
            Assert.Equal(ProgramLauncher.MaxErrorLength + 3, result.Error!.Length);
        }

        [Fact]
        public void SafeErrorText_ShortArguments_DoNotMangleTheMessage()
        {
            Assert.Equal("Access is denied - y", ProgramLauncher.SafeErrorText("Access is denied - y", @"C:\a.exe -y", @"C:\a.exe", @"C:\a.exe", "-y"));
        }

        // ---------- 2. Lone surrogates (security L1) ----------

        // Settles the review disagreement (.NET 8, unchanged on .NET 10): File.AppendAllText's default UTF-8 encoding
        // throws on a lone surrogate (so the whole log line was dropped); a non-throwing UTF8Encoding writes U+FFFD instead.
        [Fact]
        public void Verdict_DefaultAppendAllText_ThrowsOnALoneSurrogate_NonThrowingEncodingWritesReplacement()
        {
            var dir = Path.Combine(Path.GetTempPath(), "StartupController.Tests", "surrogate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "x.txt");
                var text = "a" + (char)0xD800 + "b";

                Assert.Throws<EncoderFallbackException>(() => File.AppendAllText(file, text));

                File.AppendAllText(file, text, new UTF8Encoding(false, throwOnInvalidBytes: false));
                Assert.Equal(new byte[] { (byte)'a', 0xEF, 0xBF, 0xBD, (byte)'b' }, File.ReadAllBytes(file));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
            }
        }

        [Fact]
        public async Task RunNameWithALoneSurrogate_StillProducesALaunchLine()
        {
            var marker = TestLog.Unique("SurName");
            var name = marker + (char)0xD800;

            var result = await Runner(new FakeProcessStarter(@"C:\Apps\x.exe")).LaunchManualAsync(P(name, path: @"C:\Apps\x.exe"));

            Assert.True(result.Success);
            var line = Assert.Single(TestLog.LinesContaining(marker), l => l.Contains("\tLAUNCH\t"));
            Assert.Contains("\tLAUNCH\tProgram\t" + marker + Escaped(0xD800) + "\t", line, StringComparison.Ordinal);
            Assert.Contains("SUCCESS", line, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("{D83D}{DE00}")]                 // grinning face
        [InlineData("a{D83E}{DDD1}{200D}{D83D}{DCBB}")] // ZWJ sequence: the joiner is kept
        [InlineData("{DBFF}{DFFF}")]                 // highest code point
        public void Escape_ValidPairsAndJoiners_AreUnchanged(string template)
        {
            var value = FromTemplate(template, code => ((char)code).ToString());

            Assert.Same(value, LoggingService.Escape(value));
        }

        [Fact]
        public void Escape_EveryLoneSurrogate_IsEscaped_AndLogged()
        {
            for (int code = 0xD800; code <= 0xDFFF; code += 0x7F)
                Assert.Equal("<" + Escaped(code) + ">", LoggingService.Escape("<" + (char)code + ">"));

            var marker = TestLog.Unique("SurAll");
            LoggingService.LogWarning(marker + (char)0xDFFF + (char)0xDFFF + marker);
            Assert.EndsWith(marker + Escaped(0xDFFF) + Escaped(0xDFFF) + marker, Assert.Single(TestLog.LinesContaining(marker)), StringComparison.Ordinal);
        }

        // ---------- 3. Format characters (security I2) ----------

        [Theory]
        [InlineData(0x202E)] // right-to-left override
        [InlineData(0x202A)]
        [InlineData(0x2066)] // left-to-right isolate
        [InlineData(0x2069)]
        [InlineData(0x200E)] // left-to-right mark
        [InlineData(0x061C)] // Arabic letter mark
        [InlineData(0xFEFF)] // zero-width no-break space / BOM
        [InlineData(0x00AD)] // soft hyphen
        public void Escape_FormatCharacters_AreEscaped(int code)
        {
            Assert.Equal("a" + Escaped(code) + "b", LoggingService.Escape("a" + (char)code + "b"));
        }

        [Theory]
        [InlineData(0x200C)] // zero-width non-joiner
        [InlineData(0x200D)] // zero-width joiner
        public void Escape_Joiners_AreKept(int code)
        {
            var value = "a" + (char)code + "b";

            Assert.Same(value, LoggingService.Escape(value));
        }

        [Fact]
        public void Escape_NoFormatCharacterLeaks_ExceptTheJoiners()
        {
            for (int c = 0; c <= 0xFFFF; c++)
            {
                if (char.IsSurrogate((char)c) || c == 0x200C || c == 0x200D) continue;
                foreach (var ch in LoggingService.Escape("<" + (char)c + ">"))
                    Assert.False(char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format, $"U+{c:X4} leaked");
            }
        }

        // ---------- 6. Help text characters ----------

        [Fact]
        public void HelpText_HasTheExactArrowAndDashCharacters()
        {
            foreach (var code in new[] { 0x2191, 0x2193, 0x21C8, 0x21CA })
                Assert.Contains(((char)code).ToString(), Form1.HelpText, StringComparison.Ordinal);

            var changed = new StartupProgram { Name = "X", Path = "x", Enabled = false, Description = "", Changed = true };
            Assert.Equal("Changed " + (char)0x2013 + " re-enable to launch", Form1.StatusText(changed));
            Assert.Contains("own entry is never listed", Form1.HelpText, StringComparison.Ordinal);
            Assert.Contains("Launch To Tray", Form1.HelpText, StringComparison.Ordinal);
        }

        // ---------- Test hygiene: string assertions are ordinal ----------

        // Root cause of the flaky Escape_LoneSurrogates_... theory: xUnit's string StartsWith/EndsWith/Contains default to
        // the current culture. In Danish collation "aa" is one letter (a contraction for the letter aa/å), so when the
        // random hex marker ended in 'a' and the logged value started with 'a', the expected suffix "a..." could not
        // match inside "...aa..." (about 1 run in 16 on a da-DK machine). Log text is compared ordinally everywhere.
        [Fact]
        public void RootCause_DanishCollation_SplitsNoAaContraction_SoLogAssertionsAreOrdinal()
        {
            var danish = new System.Globalization.CultureInfo("da-DK").CompareInfo;

            Assert.False(danish.IsSuffix("Sur1234dba" + "a-tail", "a-tail"));
            Assert.EndsWith("a-tail", "Sur1234dba" + "a-tail", StringComparison.Ordinal);
        }

        [Fact]
        public void TestSources_StartsWithAndEndsWithAssertions_AlwaysPassAComparison()
        {
            var call = new System.Text.RegularExpressions.Regex("Assert[.](StartsWith|EndsWith)[(]");
            var offenders = SourceScan.SourceFiles(SourceScan.TestSourceDirectory())
                .SelectMany(file => File.ReadAllLines(file).Select((line, i) => (file, line, i)))
                .Where(x => call.IsMatch(x.line) && !x.line.Contains("StringComparison", StringComparison.Ordinal))
                .Select(x => $"{Path.GetFileName(x.file)}:{x.i + 1}")
                .ToList();

            Assert.Empty(offenders);
        }

        // ---------- 7. IndexToReselect ----------

        [Fact]
        public void Reselect_AfterReload_ExactNameBeatsAnEarlierCaseInsensitiveMatch()
        {
            var model = new StartupListModel();
            model.Load(List("app", "App", "APP"));

            Assert.Equal(2, model.IndexToReselect(P("APP")));
            Assert.Equal(1, model.IndexToReselect(P("App")));
            Assert.Equal(0, model.IndexToReselect(P("aPP")));
            Assert.Equal(-1, model.IndexToReselect(P("other")));
        }
    }

    // Rotation backoff (code-inspector Should 1, security I4). Shares the logger redirect collection: no overlap with
    // other tests, and the clock seam is restored afterwards.
    [Collection(LoggerRedirectCollection.Name)]
    public sealed class Phase4FixRotationTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "StartupController.Tests", "backoff-" + Guid.NewGuid().ToString("N"));
        private DateTime _now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        public Phase4FixRotationTests()
        {
            LoggingService.UtcNow = () => _now;
            LoggingService.Initialize(_dir);
        }

        public void Dispose()
        {
            LoggingService.UtcNow = () => DateTime.UtcNow;
            LoggingService.Initialize(TestLogSetup.LogDirectory);
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        }

        private static string Log => LoggingService.LogFilePath;

        private static string Rotated(int index) => LoggingService.RotatedPath(Log, index);

        // A full log whose rotation fails while the second rotated file is locked
        private static void FailOneRotation(string line)
        {
            File.WriteAllBytes(Log, new byte[LoggingService.MaxLogBytes]);
            File.WriteAllText(Rotated(1), "one");
            File.WriteAllText(Rotated(2), "two");
            using (new FileStream(Rotated(2), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                LoggingService.LogInfo(line);
            }
            Assert.Equal("one", File.ReadAllText(Rotated(1)));
        }

        private static string Tail(string path) => File.ReadAllText(path, Encoding.UTF8).Substring((int)LoggingService.MaxLogBytes);

        [Fact]
        public void FailedRotation_IsNotRetriedOnTheNextLine_ButAfterTheDelay()
        {
            FailOneRotation("first");

            LoggingService.LogInfo("second"); // the lock is gone, but the backoff holds
            Assert.Equal("one", File.ReadAllText(Rotated(1)));
            Assert.Contains("second", Tail(Log), StringComparison.Ordinal);

            _now += LoggingService.RotationRetryDelay - TimeSpan.FromSeconds(1);
            LoggingService.LogInfo("third");
            Assert.Equal("one", File.ReadAllText(Rotated(1)));

            _now += TimeSpan.FromSeconds(2);
            LoggingService.LogInfo("fourth");
            Assert.Contains("third", Tail(Rotated(1)), StringComparison.Ordinal);    // the full log moved to .1
            Assert.Equal("one", File.ReadAllText(Rotated(2)));
            Assert.Contains("fourth", File.ReadAllText(Log), StringComparison.Ordinal);
            Assert.True(new FileInfo(Log).Length < 1000);
        }

        [Fact]
        public void FailedRotation_IsRetriedEarly_OnceTheLogHasDoubled()
        {
            FailOneRotation("first");

            using (var stream = new FileStream(Log, FileMode.Append, FileAccess.Write))
                stream.Write(new byte[LoggingService.MaxLogBytes]);
            LoggingService.LogInfo("doubled");

            Assert.True(new FileInfo(Rotated(1)).Length >= 2 * LoggingService.MaxLogBytes);
            Assert.Contains("doubled", File.ReadAllText(Log), StringComparison.Ordinal);
        }

        [Fact]
        public void SuccessfulRotation_ClearsTheBackoff()
        {
            FailOneRotation("first");
            _now += LoggingService.RotationRetryDelay;
            LoggingService.LogInfo("rotates");
            Assert.True(new FileInfo(Log).Length < 1000);

            // a new full log rotates on the next line again, without waiting
            File.WriteAllBytes(Log, new byte[LoggingService.MaxLogBytes]);
            LoggingService.LogInfo("again");
            Assert.Equal(LoggingService.MaxLogBytes, new FileInfo(Rotated(1)).Length);
            Assert.Contains("again", File.ReadAllText(Log), StringComparison.Ordinal);
        }

        [Fact]
        public void DeletedLogFolder_IsRecreated_AndLoggingContinues()
        {
            LoggingService.LogInfo("before");
            Directory.Delete(_dir, recursive: true);

            LoggingService.LogInfo("after one");
            LoggingService.LogInfo("after two");

            var text = File.ReadAllText(Log);
            Assert.Contains("after one", text, StringComparison.Ordinal);
            Assert.Contains("after two", text, StringComparison.Ordinal);
        }
    }
}
