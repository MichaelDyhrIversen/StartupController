extern alias Uninstall;

using System.Runtime.InteropServices;
using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using Uninstall::StartupController.Uninstall;
using static StartupController.Tests.Infrastructure.Programs;
using HelperProgram = Uninstall::StartupController.Uninstall.Program;
using HelperRecord = Uninstall::StartupController.TakeoverRecord;
using HelperHash = Uninstall::StartupController.FingerprintHash;

namespace StartupController.Tests
{
    // Uninstall helper (StartupController.ReturnToWindows.exe, D-T3/D-T4/D-T6): sandbox root, fake prompt, environment
    // and log. Never shows a real MessageBox, never starts a process.
    public sealed class ReturnToWindowsTests : IDisposable
    {
        private static readonly byte[] EnabledBytes = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        private static readonly string[] FullUi = { "--uilevel", "5", "--choice", "" };

        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        private sealed class FailingReturn : ReturnToWindows
        {
            public HashSet<string> FailNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool FailRecord { get; set; }

            internal override void WriteApprovedEnabled(RegistryKey approvedKey, string name)
            {
                if (FailNames.Contains(name)) throw new UnauthorizedAccessException("denied");
                base.WriteApprovedEnabled(approvedKey, name);
            }

            internal override void WriteRecord(RegistryKey appKey, string[] names)
            {
                if (FailRecord) throw new UnauthorizedAccessException("denied");
                base.WriteRecord(appKey, names);
            }
        }

        private int RunHelper(string[] args, FakePrompt prompt, IUninstallLog log, FakeHelperEnvironment? environment = null) =>
            HelperProgram.Run(args, _sandbox.Root, prompt, log, environment ?? new FakeHelperEnvironment());

        private void Record(params string[] names) =>
            _sandbox.SeedAppValue(RegistrySandbox.TakenOverValue, names, RegistryValueKind.MultiString);

        private string[]? RecordValue() => _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.TakenOverValue) as string[];

        private byte[]? ApprovedOf(string name) => _sandbox.ReadValue(RegistrySandbox.ApprovedPath, name) as byte[];

        private void SeedTakenOver(string name, byte[]? approved = null)
        {
            _sandbox.SeedRun(name, $@"C:\Apps\{name}.exe");
            _sandbox.SeedApproved(name, approved ?? Approved(0x03));
        }

        // ---------- decision ----------

        [Theory]
        [InlineData("5", "1", "Return")]
        [InlineData("5", "0", "Leave")]
        [InlineData("2", "0", "Leave")]
        [InlineData("2", "", "Return")]
        [InlineData("3", "", "Prompt")]
        [InlineData("4", "", "Prompt")]
        [InlineData("5", "", "Prompt")]
        [InlineData("5", " ", "Prompt")]
        [InlineData(null, "", "Return")]
        [InlineData("abc", "", "Return")]
        [InlineData("67", "", "Return")]
        [InlineData("5", null, "Prompt")]
        public void Decide(string? uiLevel, string? choice, string expected)
        {
            var log = new FakeUninstallLog();

            Assert.Equal(Enum.Parse<UninstallChoice>(expected), UninstallDecision.Decide(uiLevel, choice, log));
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("WARN", StringComparison.Ordinal));
        }

        [Fact]
        public void Decide_OtherChoice_IsTreatedAsEmpty_WithAWarning()
        {
            var log = new FakeUninstallLog();

            Assert.Equal(UninstallChoice.Prompt, UninstallDecision.Decide("5", "x", log));
            Assert.Equal(UninstallChoice.Return, UninstallDecision.Decide("2", "yes", log));
            Assert.Equal(2, log.Lines.Count(l => l.StartsWith("WARN", StringComparison.Ordinal)));
        }

        [Fact]
        public void PromptText_SaysAllNWillBeEnabledAndStartedByWindows_AndTheTimeout()
        {
            var text = UninstallDecision.PromptText(3);

            Assert.Contains("took over 3 startup program(s)", text, StringComparison.Ordinal);
            Assert.Contains("all of them to be enabled and started by Windows", text, StringComparison.Ordinal);
            Assert.Contains("Yes: all taken-over programs are enabled", text, StringComparison.Ordinal);
            Assert.Contains("No: they stay disabled", text, StringComparison.Ordinal);
            Assert.Contains("within 120 seconds", text, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("Yes", true, "INFO Prompted: Yes")]
        [InlineData("No", false, "INFO Prompted: No")]
        [InlineData("Failed", true, "INFO Prompt failed: silent default: Return")]
        [InlineData("TimedOut", true, "INFO Prompted: no answer within 120 s: Silent: Return")]
        public void Program_Prompt_Answers(string answer, bool returned, string logged)
        {
            SeedTakenOver("A");
            Record("A");
            var prompt = new FakePrompt { Answer = () => Enum.Parse<PromptAnswer>(answer) };
            var log = new FakeUninstallLog();

            Assert.Equal(0, RunHelper(FullUi, prompt, log));

            Assert.Equal(UninstallDecision.PromptText(1), Assert.Single(prompt.Texts));
            Assert.Equal(TimeSpan.FromSeconds(120), prompt.Timeout);
            Assert.Equal(returned ? EnabledBytes : Approved(0x03), ApprovedOf("A"));
            Assert.Contains(logged, log.Lines);
        }

        [Fact]
        public void Program_PromptThrows_AppliesTheSilentDefault()
        {
            SeedTakenOver("A");
            Record("A");
            var prompt = new FakePrompt { Answer = () => throw new InvalidOperationException("no desktop") };
            var log = new FakeUninstallLog();

            Assert.Equal(0, RunHelper(FullUi, prompt, log));

            Assert.Equal(EnabledBytes, ApprovedOf("A"));
            Assert.Contains("INFO Prompt failed: silent default: Return", log.Lines);
        }

        public static TheoryData<string, string> Blockers => new TheoryData<string, string>
        {
            { "system", "running as SYSTEM" },
            { "session0", "session 0" },
            { "noninteractive", "not an interactive process" },
        };

        [Theory]
        [MemberData(nameof(Blockers))]
        public void Program_PromptSkipped_ByEnvironment_ReturnsSilently_AndLogsTheRule(string rule, string logged)
        {
            SeedTakenOver("A");
            Record("A");
            var environment = new FakeHelperEnvironment
            {
                IsSystem = rule == "system",
                SessionId = rule == "session0" ? 0 : 1,
                UserInteractive = rule != "noninteractive"
            };
            var prompt = new FakePrompt();
            var log = new FakeUninstallLog();

            Assert.Equal(0, RunHelper(FullUi, prompt, log, environment));

            Assert.Empty(prompt.Texts);
            Assert.Equal(EnabledBytes, ApprovedOf("A"));
            Assert.Contains(log.Lines, l => l.StartsWith("INFO Prompt skipped (" + logged, StringComparison.Ordinal) && l.EndsWith("Silent: Return", StringComparison.Ordinal));
        }

        [Fact]
        public void Program_NothingRecorded_DoesNotPrompt_AndWarnsThatOtherProfilesWereNotHandled()
        {
            var prompt = new FakePrompt();
            var log = new FakeUninstallLog();
            var environment = new FakeHelperEnvironment { UserSid = "S-1-5-21-9-9-9-1234" };

            Assert.Equal(0, RunHelper(FullUi, prompt, log, environment));

            Assert.Empty(prompt.Texts);
            Assert.Contains(log.Lines, l => l.StartsWith("WARN No taken-over programs to return for user S-1-5-21-9-9-9-1234", StringComparison.Ordinal)
                && l.Contains("other user profiles on this machine were not handled", StringComparison.Ordinal));
        }

        [Fact]
        public void Program_AsSystem_WarnsThatNoUserWasHandled()
        {
            var log = new FakeUninstallLog();

            Assert.Equal(0, RunHelper(FullUi, new FakePrompt(), log, new FakeHelperEnvironment { IsSystem = true, UserSid = "S-1-5-18" }));

            Assert.Contains(log.Lines, l => l.StartsWith("WARN Running as SYSTEM", StringComparison.Ordinal));
            Assert.Contains(log.Lines, l => l.Contains("| user: S-1-5-18 |", StringComparison.Ordinal));
        }

        [Fact]
        public void Program_PromptNumber_CountsOnlyNamesStillInRunAsStrings()
        {
            SeedTakenOver("A");
            SeedTakenOver("B");
            _sandbox.SeedRunValue("G", 1, RegistryValueKind.DWord);
            Record("A", "B", "Gone", "G", "StartupController");
            var prompt = new FakePrompt { Answer = () => PromptAnswer.No };

            RunHelper(FullUi, prompt, new FakeUninstallLog());

            Assert.Equal(UninstallDecision.PromptText(2), Assert.Single(prompt.Texts));
            Assert.Equal(2, ReturnToWindows.ReturnableCount(_sandbox.Root, new[] { "A", "B", "Gone", "G", "StartupController" }));
        }

        [Fact]
        public void Program_Silent_Returns_WithoutPrompt()
        {
            SeedTakenOver("A");
            Record("A");
            var prompt = new FakePrompt();
            var log = new FakeUninstallLog();

            Assert.Equal(0, RunHelper(new[] { "--uilevel", "2", "--choice", "" }, prompt, log));

            Assert.Empty(prompt.Texts);
            Assert.Equal(EnabledBytes, ApprovedOf("A"));
            Assert.Contains("INFO Silent: Return", log.Lines);
            Assert.Contains("INFO Returned 1 of 1", log.Lines);
        }

        // N7: Settings > Apps uninstalls at basic or reduced UI; the user asked to be asked there
        [Theory]
        [InlineData("3")]
        [InlineData("4")]
        [InlineData("5")]
        public void Program_BasicReducedAndFullUi_Prompt(string uiLevel)
        {
            SeedTakenOver("A");
            Record("A");
            var prompt = new FakePrompt { Answer = () => PromptAnswer.No };

            RunHelper(new[] { "--uilevel", uiLevel, "--choice", "" }, prompt, new FakeUninstallLog());

            Assert.Single(prompt.Texts);
            Assert.Equal(Approved(0x03), ApprovedOf("A"));
        }

        [Theory]
        [InlineData("2")]
        [InlineData("67")] // basic UI + progress-only flag (/passive): stays silent
        public void Program_NoUiOrFlaggedUi_ReturnsWithoutPrompt(string uiLevel)
        {
            SeedTakenOver("A");
            Record("A");
            var prompt = new FakePrompt();

            RunHelper(new[] { "--uilevel", uiLevel, "--choice", "" }, prompt, new FakeUninstallLog());

            Assert.Empty(prompt.Texts);
            Assert.Equal(EnabledBytes, ApprovedOf("A"));
        }

        [Fact]
        public void Program_PropertyLeave_WritesNothing()
        {
            SeedTakenOver("A");
            Record("A");
            var log = new FakeUninstallLog();

            Assert.Equal(0, RunHelper(new[] { "--uilevel", "5", "--choice", "0" }, new FakePrompt(), log));

            Assert.Equal(Approved(0x03), ApprovedOf("A"));
            Assert.Equal(new[] { "A" }, RecordValue());
            Assert.Contains("INFO Property: Leave", log.Lines);
        }

        [Fact]
        public void Program_ChoiceGivenTwice_IsUnparsable_AndWarns()
        {
            SeedTakenOver("A");
            Record("A");
            var prompt = new FakePrompt { Answer = () => PromptAnswer.No };
            var log = new FakeUninstallLog();

            RunHelper(new[] { "--uilevel", "5", "--choice", "0", "--choice", "1" }, prompt, log);

            Assert.Single(prompt.Texts); // neither 0 nor 1 wins: the UI level decides
            Assert.Contains("WARN --choice is given more than once; treated as unparsable", log.Lines);
        }

        [Fact]
        public void Program_ReturnsZero_OnEveryPath_IncludingAnInjectedException()
        {
            var disposed = _sandbox.OpenReadOnlyRoot();
            disposed.Dispose();

            Assert.Equal(0, HelperProgram.Run(new[] { "--uilevel", "2" }, disposed, new FakePrompt(), new FakeUninstallLog(), new FakeHelperEnvironment()));
            Assert.Equal(0, RunHelper(Array.Empty<string>(), new FakePrompt(), new FakeUninstallLog()));
            Assert.Equal(0, RunHelper(new[] { "--choice" }, new FakePrompt(), new FakeUninstallLog()));
            Assert.Equal(0, RunHelper(new[] { "--uilevel", "2" }, new FakePrompt(), new ThrowingLog()));
        }

        private sealed class ThrowingLog : IUninstallLog
        {
            public void Info(string text) => throw new IOException("disk full");
            public void Warning(string text) => throw new IOException("disk full");
            public void Error(string text) => throw new IOException("disk full");
        }

        // ---------- return logic (D-T4) ----------

        [Fact]
        public void Return_EnablesEveryRecordedStringEntry_WithoutAnyStateCheck()
        {
            _sandbox.SeedRun("StartupController", "\"C:\\x\\StartupController.exe\" --launch");
            SeedTakenOver("A");                                                    // enabled in the app
            SeedTakenOver("E");                                                    // disabled in the app
            SeedTakenOver("F");                                                    // command changed (Changed, D7)
            SeedTakenOver("C", new byte[] { 0x03, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9 }); // re-disabled in Task Manager
            _sandbox.SeedRun("D", @"C:\Apps\D.exe");                              // approved value deleted
            SeedTakenOver("X", Approved(0x02));                                    // already enabled
            _sandbox.SeedRunValue("V", @"%ProgramFiles%\V\V.exe", RegistryValueKind.ExpandString);
            _sandbox.SeedApproved("V", Approved(0x03));
            _sandbox.SeedStoredOrder(new[] { "A", "E", "F", "C", "D", "X", "V" }, new[] { "A", "F" });
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue,
                new[] { "A|" + RunFingerprint.Compute(RegistryValueKind.String, @"C:\Apps\A.exe"), "F|" + new string('f', 64) }, RegistryValueKind.MultiString);
            Record("A", "E", "F", "C", "D", "X", "V");
            var run = _sandbox.Dump(RegistrySandbox.RunPath);
            var app = _sandbox.Dump(RegistrySandbox.AppPath).Where(l => !l.StartsWith(RegistrySandbox.TakenOverValue + "|", StringComparison.Ordinal)).ToList();
            var log = new FakeUninstallLog();

            var result = ReturnToWindows.Run(_sandbox.Root, log);

            Assert.Equal(7, result.Returned);
            Assert.Equal(7, result.Recorded);
            foreach (var name in new[] { "A", "E", "F", "C", "D", "X", "V" })
            {
                Assert.Equal(EnabledBytes, ApprovedOf(name));
                Assert.Equal(RegistryValueKind.Binary, _sandbox.ReadKind(RegistrySandbox.ApprovedPath, name));
            }
            Assert.Contains("INFO Returned 'A' to Windows", log.Lines);
            Assert.Contains("INFO Returned 'E' to Windows (it was Disabled in StartupController)", log.Lines);
            Assert.Contains("INFO Returned 'F' to Windows (it was Changed in StartupController)", log.Lines);
            Assert.Equal(Array.Empty<string>(), RecordValue());
            Assert.Equal(run, _sandbox.Dump(RegistrySandbox.RunPath));
            Assert.Equal(app, _sandbox.Dump(RegistrySandbox.AppPath).Where(l => !l.StartsWith(RegistrySandbox.TakenOverValue + "|", StringComparison.Ordinal)).ToList());
            Assert.Null(ApprovedOf("StartupController"));
        }

        [Fact]
        public void Return_KeepsNamesThatAreGoneOrNotStrings()
        {
            SeedTakenOver("A");
            _sandbox.SeedApproved("B", Approved(0x03)); // removed from Run
            _sandbox.SeedRunValue("G", 1, RegistryValueKind.DWord);
            _sandbox.SeedApproved("G", Approved(0x03));
            Record("A", "B", "G");
            var log = new FakeUninstallLog();

            var result = ReturnToWindows.Run(_sandbox.Root, log);

            Assert.Equal(1, result.Returned);
            Assert.Equal(Approved(0x03), ApprovedOf("B"));
            Assert.Equal(Approved(0x03), ApprovedOf("G"));
            Assert.Equal(new[] { "B", "G" }, RecordValue());
            Assert.Contains("INFO Kept 'B': not in Run", log.Lines);
            Assert.Contains("INFO Kept 'G': not a string", log.Lines);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PreTakeoverDisabledEntry_IsNeverTouched(bool withRecord)
        {
            var original = new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
            SeedTakenOver("P", original);
            if (withRecord)
            {
                SeedTakenOver("A");
                Record("A");
            }

            ReturnToWindows.Run(_sandbox.Root, new FakeUninstallLog());

            Assert.Equal(original, ApprovedOf("P"));
        }

        [Fact]
        public void MissingAppKey_NothingToReturn()
        {
            SeedTakenOver("A");

            var result = ReturnToWindows.Run(_sandbox.Root, new FakeUninstallLog());

            Assert.Equal(0, result.Returned);
            Assert.Equal(Approved(0x03), ApprovedOf("A"));
            Assert.False(_sandbox.KeyExists(RegistrySandbox.AppPath));
        }

        [Fact]
        public void MissingApprovedKey_NothingToReturn_AndNothingCreated()
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            Record("A");

            Assert.Equal(0, ReturnToWindows.Run(_sandbox.Root, new FakeUninstallLog()).Returned);

            Assert.False(_sandbox.KeyExists(RegistrySandbox.ApprovedPath));
            Assert.Equal(new[] { "A" }, RecordValue());
        }

        [Fact]
        public void WrongKindRecord_NothingToReturn_Logged()
        {
            SeedTakenOver("A");
            _sandbox.SeedAppValue(RegistrySandbox.TakenOverValue, "A", RegistryValueKind.String);
            var log = new FakeUninstallLog();

            Assert.Equal(0, ReturnToWindows.Run(_sandbox.Root, log).Returned);

            Assert.Equal(Approved(0x03), ApprovedOf("A"));
            Assert.Equal("A", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.TakenOverValue));
            Assert.Contains(log.Lines, l => l.StartsWith("WARN TakenOverPrograms is not REG_MULTI_SZ", StringComparison.Ordinal));
        }

        [Fact]
        public void EmptyAndOverLongNames_AreIgnored()
        {
            SeedTakenOver("A");
            var tooLong = new string('L', HelperRecord.MaxNameLength + 1);
            Record("", "  ", tooLong, "A", "a");
            var log = new FakeUninstallLog();

            var result = ReturnToWindows.Run(_sandbox.Root, log);

            Assert.Equal(1, result.Returned);
            Assert.Equal(EnabledBytes, ApprovedOf("A"));
            Assert.Null(ApprovedOf(tooLong));
            Assert.Contains(log.Lines, l => l.Contains("ignored 4 empty, over-long or duplicate name(s)", StringComparison.Ordinal));
        }

        [Fact]
        public void OwnEntry_InTheRecord_IsNeverWritten()
        {
            _sandbox.SeedRun("StartupController", "\"C:\\x\\StartupController.exe\" --launch");
            _sandbox.SeedApproved("StartupController", Approved(0x03));
            Record("startupcontroller");
            var prompt = new FakePrompt();
            var log = new FakeUninstallLog();

            Assert.Equal(0, RunHelper(FullUi, prompt, log));
            var direct = ReturnToWindows.Run(_sandbox.Root, log);

            Assert.Empty(prompt.Texts); // nothing returnable: no prompt
            Assert.Equal(0, direct.Returned);
            Assert.Equal(Approved(0x03), ApprovedOf("StartupController"));
            Assert.Contains("INFO Kept 'startupcontroller': StartupController's own entry", log.Lines);
        }

        [Fact]
        public void ApprovedWriteFailsForOneName_TheOthersAreReturned()
        {
            SeedTakenOver("A");
            SeedTakenOver("B");
            Record("A", "B");
            var helper = new FailingReturn();
            helper.FailNames.Add("A");
            var log = new FakeUninstallLog();

            var result = helper.Execute(_sandbox.Root, log);

            Assert.Equal(1, result.Returned);
            Assert.Equal(Approved(0x03), ApprovedOf("A"));
            Assert.Equal(EnabledBytes, ApprovedOf("B"));
            Assert.Equal(new[] { "A" }, RecordValue());
            Assert.Contains(log.Lines, l => l.StartsWith("INFO Kept 'A': could not be written", StringComparison.Ordinal));
        }

        [Fact]
        public void RecordRewriteFails_TheApprovedWritesStay_AndALaterRunWritesTheSameValueAgain()
        {
            SeedTakenOver("A");
            Record("A");
            var helper = new FailingReturn { FailRecord = true };
            var log = new FakeUninstallLog();

            Assert.Equal(1, helper.Execute(_sandbox.Root, log).Returned);

            Assert.Equal(EnabledBytes, ApprovedOf("A"));
            Assert.Equal(new[] { "A" }, RecordValue());
            Assert.Contains(log.Lines, l => l.StartsWith("ERROR Could not update TakenOverPrograms", StringComparison.Ordinal));

            Assert.Equal(1, ReturnToWindows.Run(_sandbox.Root, new FakeUninstallLog()).Returned);
            Assert.Equal(EnabledBytes, ApprovedOf("A"));
            Assert.Equal(Array.Empty<string>(), RecordValue());
            var approved = _sandbox.Dump(RegistrySandbox.ApprovedPath);
            Assert.Equal(0, ReturnToWindows.Run(_sandbox.Root, new FakeUninstallLog()).Returned);
            Assert.Equal(approved, _sandbox.Dump(RegistrySandbox.ApprovedPath));
        }

        // ---------- format parity with the app ----------

        [Fact]
        public void NamesWrittenByTheApp_AreReadBackUnchangedByTheHelper()
        {
            var names = new[] { "Plain", "With|Bar", "Line\r\nBreak" };
            _sandbox.SeedRun("StartupController", "\"C:\\x\\StartupController.exe\" --launch");
            foreach (var name in names)
                _sandbox.SeedRun(name, @"C:\Apps\x.exe");

            new StartupRegistryService(_sandbox.Root, () => DateTime.UtcNow, IsTestApp).TakeOverWindowsEntries(true);

            Assert.True(HelperRecord.TryRead(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.TakenOverValue), out var read));
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), read.OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(StartupApprovedState.Enabled(), EnabledBytes);
        }

        [Theory]
        [InlineData(RegistryValueKind.String, @"C:\Apps\A.exe")]
        [InlineData(RegistryValueKind.ExpandString, @"%ProgramFiles%\V\V.exe --x")]
        public void HelperFingerprint_MatchesTheApps(RegistryValueKind kind, string raw)
        {
            Assert.Equal(RunFingerprint.Compute(kind, raw), HelperHash.Compute(kind, raw));
        }

        // ---------- log ----------

        private static string TempDir() => Path.Combine(Path.GetTempPath(), "StartupController.Tests", "uninstall-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Log_HasHeaderLinePerNameAndSummary_Escaped_WithoutCommands()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "uninstall.log");
                var log = new UninstallLog(path);
                var odd = "Odd\r\nFAKE [INFO] x";
                _sandbox.SeedRun(odd, @"C:\Secret\odd.exe --token hunter2");
                _sandbox.SeedApproved(odd, Approved(0x03));
                SeedTakenOver("A");
                Record("A", odd, "Gone");

                Assert.Equal(0, HelperProgram.Run(new[] { "--uilevel", "2", "--choice", "" }, _sandbox.Root, new FakePrompt(), log, new FakeHelperEnvironment()));

                var lines = File.ReadAllLines(path);
                Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \[INFO\] === StartupController uninstall helper .* \| user: S-1-5-21-1000-2000-3000-1001 \| UILevel: 2 \| choice:  ===$", lines[0]);
                Assert.Contains(lines, l => l.EndsWith("[INFO] Silent: Return", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.EndsWith("[INFO] Returned 'A' to Windows", StringComparison.Ordinal)); // no stored order: no app state to report
                Assert.Contains(lines, l => l.Contains(@"[INFO] Returned 'Odd\r\nFAKE [INFO] x' to Windows", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.EndsWith("[INFO] Kept 'Gone': not in Run", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.EndsWith("[INFO] Returned 2 of 3", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.Contains("other user profiles on this machine were not handled", StringComparison.Ordinal));
                Assert.DoesNotContain(lines, l => l.Contains("hunter2", StringComparison.Ordinal) || l.Contains(@"C:\", StringComparison.Ordinal));
                Assert.DoesNotContain(lines, l => l.StartsWith("FAKE", StringComparison.Ordinal));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void Log_UnwritablePath_DoesNotThrow()
        {
            var dir = TempDir();
            Directory.CreateDirectory(dir);
            try
            {
                var log = new UninstallLog(dir); // a directory, so every append fails

                log.Info("x");
                log.Warning("y");
                log.Error("z");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

        [Fact]
        public void Log_RefusesAFileWithASecondHardLink()
        {
            var dir = TempDir();
            Directory.CreateDirectory(dir);
            try
            {
                var target = Path.Combine(dir, "target.txt");
                File.WriteAllText(target, "keep");
                var path = Path.Combine(dir, "uninstall.log");
                Assert.True(CreateHardLink(path, target, IntPtr.Zero), "could not create the test hard link");

                new UninstallLog(path).Info("must not be written");

                Assert.Equal("keep", File.ReadAllText(target));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // Directory symbolic link (a reparse point like a junction). Needs developer mode or elevation; null when not allowed.
        private static bool TryMakeLink(string link, string target)
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        [Fact]
        public void Log_RefusesALogFolderThatIsAJunction_AndWritesNothingThroughIt()
        {
            var dir = TempDir();
            var real = Path.Combine(dir, "real");
            Directory.CreateDirectory(real);
            try
            {
                var link = Path.Combine(dir, "link");
                if (!TryMakeLink(link, real)) return; // cannot create links here: nothing to verify

                new UninstallLog(Path.Combine(link, "uninstall.log")).Info("must not be written");

                Assert.Empty(Directory.GetFiles(real));
            }
            finally
            {
                if (Directory.Exists(Path.Combine(dir, "link"))) Directory.Delete(Path.Combine(dir, "link"));
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Log_RefusesAParentFolderThatIsAJunction()
        {
            var dir = TempDir();
            var real = Path.Combine(dir, "real");
            Directory.CreateDirectory(real);
            try
            {
                var link = Path.Combine(dir, "link");
                if (!TryMakeLink(link, real)) return; // cannot create links here: nothing to verify

                new UninstallLog(Path.Combine(link, "sub", "uninstall.log")).Info("must not be written");

                Assert.Empty(Directory.GetFileSystemEntries(real));
            }
            finally
            {
                if (Directory.Exists(Path.Combine(dir, "link"))) Directory.Delete(Path.Combine(dir, "link"));
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Log_PlainFile_IsAppendedAndStaysReadable()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "uninstall.log");
                var log = new UninstallLog(path);
                log.Info("one");
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    log.Info("two"); // a reader doesn't block the append (FileShare.Read on the writer)
                }

                var lines = File.ReadAllLines(path);
                Assert.Equal(2, lines.Length);
                Assert.EndsWith("[INFO] two", lines[1], StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        // N1: no file log while elevated or SYSTEM
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Log_ElevatedOrSystem_WritesNoFile(bool elevated, bool system)
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "uninstall.log");
                var log = UninstallLog.For(new FakeHelperEnvironment { IsElevated = elevated, IsSystem = system }, path);
                SeedTakenOver("A");
                Record("A");

                Assert.Equal(0, HelperProgram.Run(new[] { "--uilevel", "2", "--choice", "" }, _sandbox.Root, new FakePrompt(), log, new FakeHelperEnvironment()));

                Assert.IsType<NullUninstallLog>(log);
                Assert.False(Directory.Exists(dir));
                Assert.Equal(EnabledBytes, ApprovedOf("A")); // the return itself still happens
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void Log_NotElevated_UsesTheGuardedFileLog()
        {
            Assert.IsType<UninstallLog>(UninstallLog.For(new FakeHelperEnvironment(), Path.Combine(TempDir(), "uninstall.log")));
        }

        [Fact]
        public void Log_DefaultPath_IsTheUsersLogsFolder()
        {
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StartupController", "logs", "uninstall.log");

            Assert.Equal(expected, UninstallLog.DefaultPath());
        }

        [Theory]
        [InlineData(new[] { "--uilevel", "5", "--choice", "1" }, "--choice", "1")]
        [InlineData(new[] { "--UILEVEL", "3" }, "--uilevel", "3")]
        [InlineData(new[] { "--choice" }, "--choice", null)]
        [InlineData(new string[0], "--uilevel", null)]
        [InlineData(new[] { "--uilevel", "5", "--uilevel", "2" }, "--uilevel", null)]
        public void Argument_ParsesSwitchValues(string[] args, string name, string? expected)
        {
            Assert.Equal(expected, HelperProgram.Argument(args, name));
        }
    }
}
