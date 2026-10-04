using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Fixes from the Phase 3 reviews (security M1, L1-L4, tester bugs, code-inspector items).
    // FakeProcessStarter only: nothing is started. Test-only event names.
    public class Phase3FixTests
    {
        private const string Self = @"C:\Program Files\StartupController\StartupController.exe";

        private static ProgramLauncher Launcher(FakeProcessStarter starter) => new ProgramLauncher(starter, Self, p => p, (a, b) => false);

        // ---------- 1. M1: shortest executable token, no prefix probing ----------

        [Fact]
        public void PlantedShortExe_TargetMissing_IsNotFound_AndNothingStarts()
        {
            var starter = new FakeProcessStarter(@"C:\ProgramData\My.exe");

            var result = Launcher(starter).Launch(P("V", path: @"C:\ProgramData\My App\a.exe -x"));

            Assert.True(result.NotFound);
            Assert.Empty(starter.Started);
            Assert.DoesNotContain(@"C:\ProgramData\My.exe", starter.FileExistsCalls);
        }

        [Fact]
        public void PlantedWholeCommandFile_IsNotStarted()
        {
            var starter = new FakeProcessStarter(@"C:\ProgramData\Vendor\app.exe --load cfg.js", @"C:\ProgramData\Vendor\app.exe");

            Launcher(starter).Launch(P("V", path: @"C:\ProgramData\Vendor\app.exe --load cfg.js"));

            var psi = Assert.Single(starter.Started);
            Assert.Equal(@"C:\ProgramData\Vendor\app.exe", psi.FileName);
            Assert.Equal("--load cfg.js", psi.Arguments);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ProgramFilesXY_WithPlantedProgramExe_NeverStartsProgramExe(bool targetExists)
        {
            var files = targetExists ? new[] { @"C:\Program.exe", @"C:\Program Files\X Y\a.exe" } : new[] { @"C:\Program.exe" };
            var starter = new FakeProcessStarter(files);

            var result = Launcher(starter).Launch(P("V", path: @"C:\Program Files\X Y\a.exe arg"));

            Assert.DoesNotContain(starter.Started, s => s.FileName == @"C:\Program.exe");
            if (targetExists)
                Assert.Equal(@"C:\Program Files\X Y\a.exe", Assert.Single(starter.Started).FileName);
            else
                Assert.True(result.NotFound);
        }

        // ---------- 2. Direct start for .exe/.com ----------

        [Theory]
        [InlineData(@"C:\Apps\a.exe", false)]
        [InlineData(@"C:\Apps\a.COM", false)]
        [InlineData(@"C:\Apps\a.bat", true)]
        [InlineData(@"C:\Apps\a.cmd", true)]
        [InlineData(@"C:\Apps\a.lnk", true)]
        public void ExistingFile_UsesTheShellOnlyForScriptsAndShortcuts(string path, bool shell)
        {
            var starter = new FakeProcessStarter(path);

            Assert.True(Launcher(starter).Launch(P("A", path: path)).Success);

            Assert.Equal(shell, Assert.Single(starter.Started).UseShellExecute);
        }

        [Fact]
        public void BareName_UsesTheShell()
        {
            var starter = new FakeProcessStarter();

            Launcher(starter).Launch(P("R", path: "rundll32.exe shell32.dll,Foo"));

            Assert.True(Assert.Single(starter.Started).UseShellExecute);
        }

        [Fact]
        public void ElevationRequired_RetriesThroughTheShell()
        {
            var starter = new ElevationStarter(@"C:\Apps\admin.exe", nativeError: 740);

            var result = new ProgramLauncher(starter, Self, p => p, (a, b) => false).Launch(P("A", path: @"C:\Apps\admin.exe"));

            Assert.True(result.Success);
            Assert.Equal(new[] { false, true }, starter.ShellFlags);
            Assert.True(starter.LastHandle!.Disposed);
        }

        [Fact]
        public void OtherWin32Error_IsAFailure_WithoutShellRetry()
        {
            var starter = new ElevationStarter(@"C:\Apps\bad.exe", nativeError: 193); // ERROR_BAD_EXE_FORMAT

            var result = new ProgramLauncher(starter, Self, p => p, (a, b) => false).Launch(P("A", path: @"C:\Apps\bad.exe"));

            Assert.False(result.Success);
            Assert.Equal(new[] { false }, starter.ShellFlags);
        }

        // ---------- 3. L1: no current-directory resolution ----------

        [Theory]
        [InlineData(@"sub\app.exe")]
        [InlineData(@"..\x.exe")]
        [InlineData(@"C:app.exe")]
        [InlineData(@"\app.exe")]
        [InlineData("\"sub\\app\" -q")]
        public void RelativePaths_AreNotFound_AndNeverProbed(string command)
        {
            var starter = new FakeProcessStarter { ExistsWhen = _ => true };

            var result = Launcher(starter).Launch(P("R", path: command));

            Assert.True(result.NotFound);
            Assert.Empty(starter.Started);
            Assert.Empty(starter.FileExistsCalls);
        }

        [Fact]
        public void BareName_IsNeverProbed()
        {
            var starter = new FakeProcessStarter { ExistsWhen = _ => true };

            Launcher(starter).Launch(P("T", path: "tool.exe /q"));

            Assert.Empty(starter.FileExistsCalls);
            Assert.True(Assert.Single(starter.Started).UseShellExecute);
        }

        // ---------- 4. Self guard and path hygiene ----------

        [Theory]
        [InlineData("\"C:\\x\\app.exe:evil.exe\"")]
        [InlineData("\"C:\\x\\app.exe::$DATA\"")]
        [InlineData("\"C:\\x\\app.exe.\"")]
        [InlineData(@"C:\x\app.exe. -y")]
        [InlineData("notepad.exe.")]
        public void StreamNamesAndTrailingDots_AreRejected(string command)
        {
            var starter = new FakeProcessStarter { ExistsWhen = _ => true };

            var result = Launcher(starter).Launch(P("Ads", path: command));

            Assert.False(result.Success);
            Assert.False(result.Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void DevicePrefixedPath_ToAnotherProgram_IsStarted()
        {
            var starter = new FakeProcessStarter(@"\\?\C:\Apps\x.exe");

            Assert.True(Launcher(starter).Launch(P("X", path: @"\\?\C:\Apps\x.exe")).Success);
        }

        [Theory]
        [InlineData(@"\\?\UNC\srv\share\StartupController.exe")]
        [InlineData(@"\\?\C:\Program Files\StartupController\StartupController.exe")]
        public void DevicePrefixedSelf_IsBlocked(string command)
        {
            var starter = new FakeProcessStarter { ExistsWhen = _ => true };

            Assert.True(Launcher(starter).Launch(P("S", path: command)).Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void DevicePrefixedSelfPath_MatchesByPath()
        {
            var launcher = new ProgramLauncher(new FakeProcessStarter(), @"\\?\C:\Apps\Renamed.exe", p => p, (a, b) => false);

            Assert.True(launcher.IsSelf(@"C:\Apps\Renamed.exe"));
        }

        [Fact]
        public void BareName_NextToThisApp_IsBlocked()
        {
            var self = Path.Combine(AppContext.BaseDirectory, "RenamedSelf.exe");
            var starter = new FakeProcessStarter();
            var launcher = new ProgramLauncher(starter, self, Path.GetFullPath, (a, b) => false);

            Assert.True(launcher.Launch(P("S", path: "RenamedSelf.exe --launch")).Blocked);
            Assert.True(launcher.Launch(P("S", path: "RenamedSelf. --launch")).Blocked);
            Assert.True(launcher.Launch(P("S", path: "RenamedSelf --launch")).Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void SameFileIdentity_BlocksAnAlias()
        {
            var starter = new FakeProcessStarter(@"D:\Alias\other.exe");
            var launcher = new ProgramLauncher(starter, Self, p => p, (a, b) => a == @"D:\Alias\other.exe" && b == Self);

            Assert.True(launcher.Launch(P("S", path: @"D:\Alias\other.exe")).Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void SameFileIdentityThrows_FailsSafe_OtherChecksStillBlock()
        {
            var starter = FakeProcessStarter.AllExesExist();
            var launcher = new ProgramLauncher(starter, Self, p => p, (a, b) => throw new IOException("locked"));

            Assert.True(launcher.Launch(P("S", path: @"D:\Old\StartupController.exe")).Blocked);
            Assert.True(launcher.Launch(P("O", path: @"D:\Old\other.exe")).Success);
        }

        [Fact]
        public void RealIdentity_HardlinkToSelf_IsBlocked()
        {
            var dir = Path.Combine(Path.GetTempPath(), "StartupController.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var self = Path.Combine(dir, "self.exe");
                var link = Path.Combine(dir, "innocent.exe");
                var other = Path.Combine(dir, "other.exe");
                File.WriteAllText(self, "x");
                File.WriteAllText(other, "y");
                if (!CreateHardLinkW(link, self, IntPtr.Zero))
                    return; // file system without hardlinks: nothing to check

                Assert.True(PathHelper.IsSameFile(link, self));
                Assert.False(PathHelper.IsSameFile(other, self));

                var starter = FakeProcessStarter.AllExesExist();
                var launcher = new ProgramLauncher(starter, self); // real normalizer and identity check
                Assert.True(launcher.Launch(P("L", path: "\"" + link + "\"")).Blocked);
                Assert.True(launcher.Launch(P("O", path: "\"" + other + "\"")).Success);
                Assert.Equal(other, Assert.Single(starter.Started).FileName);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        // ---------- 5. Bounds ----------

        [Fact]
        public void OverlongCommand_IsAFailure_LoggedByNameOnly()
        {
            var name = TestLog.Unique("Long");
            var starter = new FakeProcessStarter { ExistsWhen = _ => true };
            var command = @"C:\Apps\x.exe " + new string('q', ProgramLauncher.MaxCommandLength);

            var result = Launcher(starter).Launch(P(name, path: command));

            Assert.False(result.Success);
            Assert.False(result.NotFound);
            Assert.Empty(starter.Started);
            Assert.Empty(starter.FileExistsCalls);
            var line = Assert.Single(TestLog.LinesContaining(name));
            Assert.DoesNotContain("qqqq", line, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\Apps", line, StringComparison.Ordinal);
        }

        [Fact]
        public void CommandTooLongAfterExpansion_IsAFailure()
        {
            // A test-only variable, so the result doesn't depend on this machine's environment
            var variable = "SC_TESTS_LONG_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(variable, @"C:\" + new string('d', 1000));
            try
            {
                var starter = new FakeProcessStarter { ExistsWhen = _ => true };
                var raw = "%" + variable + @"%\x.exe " + new string('q', ProgramLauncher.MaxCommandLength - 200);
                Assert.True(raw.Length <= ProgramLauncher.MaxCommandLength);

                var result = Launcher(starter).Launch(P("E", path: raw));

                Assert.False(result.Success);
                Assert.Equal("Command is too long", result.Error);
                Assert.Empty(starter.Started);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        // ---------- 6. IPC ----------

        private static string EventName() => @"Local\StartupController.Tests.Fix." + Guid.NewGuid().ToString("N");

        [Fact]
        public void RepeatedSignals_WithinTheInterval_ActivateOnce()
        {
            var name = EventName();
            int count = 0;
            using var first = new ManualResetEventSlim(false);
            using var activation = new InstanceActivation(name, () => { Interlocked.Increment(ref count); first.Set(); }, TimeSpan.FromSeconds(30));

            InstanceActivation.SignalExisting(name);
            Assert.True(first.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 5; i++)
            {
                InstanceActivation.SignalExisting(name);
                Thread.Sleep(20);
            }
            Thread.Sleep(200);

            Assert.Equal(1, Volatile.Read(ref count));
        }

        [Fact]
        public void SignalAfterTheInterval_ActivatesAgain()
        {
            var name = EventName();
            using var twice = new CountdownEvent(2);
            using var activation = new InstanceActivation(name, () => twice.Signal(), TimeSpan.FromMilliseconds(100));

            InstanceActivation.SignalExisting(name);
            SpinWait.SpinUntil(() => twice.CurrentCount == 1, TimeSpan.FromSeconds(5));
            Thread.Sleep(250);
            InstanceActivation.SignalExisting(name);

            Assert.True(twice.Wait(TimeSpan.FromSeconds(5)));
        }

        [Theory]
        [InlineData("")]
        [InlineData(@"Global\..\bad\name\with\backslashes")]
        public void SignalExisting_BadName_ReturnsFalse(string name)
        {
            Assert.False(InstanceActivation.SignalExisting(name));
        }

        [Fact]
        public void Relay_RequestBeforeAttach_IsDeliveredOnAttach()
        {
            var relay = new ActivationRelay();
            int delivered = 0;

            relay.Request();
            relay.Request();
            relay.Attach(() => delivered++);

            Assert.Equal(1, delivered);
        }

        [Fact]
        public void Relay_RequestAfterAttach_IsDeliveredImmediately()
        {
            var relay = new ActivationRelay();
            int delivered = 0;
            relay.Attach(() => delivered++);

            relay.Request();
            relay.Request();

            Assert.Equal(2, delivered);
        }

        [Fact]
        public void Relay_WithActivation_EarlySignalReachesTheLateTarget()
        {
            var name = EventName();
            var relay = new ActivationRelay();
            using var delivered = new ManualResetEventSlim(false);
            using var activation = new InstanceActivation(name, relay.Request);

            InstanceActivation.SignalExisting(name);
            Thread.Sleep(200); // the signal is handled before the "form" exists
            relay.Attach(delivered.Set);

            Assert.True(delivered.Wait(TimeSpan.FromSeconds(5)));
        }

        // ---------- 7/8. LaunchRunner ----------

        [Fact]
        public void FailureMessage_DistinguishesNotFound()
        {
            var program = P("A");

            Assert.Equal("Executable not found for A: gone", LaunchRunner.FailureMessage(program, new LaunchResult(false, NotFound: true, Error: "gone")));
            Assert.Equal("Failed to launch A: boom", LaunchRunner.FailureMessage(program, new LaunchResult(false, Error: "boom")));
        }

        [Fact]
        public async Task HangingLaunch_TimesOut_AndTheSequenceContinues()
        {
            var name = TestLog.Unique("Hang");
            var never = new TaskCompletionSource<LaunchResult>();
            int calls = 0;
            var launcher = new ProgramLauncher(new FakeProcessStarter(@"C:\Apps\B.exe"), Self, p => p, (a, b) => false);
            // The first launch never returns; the rest run inline
            var runner = new LaunchRunner(launcher, new FakeNotifier(), new FakeDialog(),
                launch => Interlocked.Increment(ref calls) == 1 ? never.Task : Task.FromResult(launch()),
                TimeSpan.FromMilliseconds(200));

            var sw = Stopwatch.StartNew();
            var summary = await runner.LaunchSequenceAsync(new[] { P(name, path: @"C:\Hang\h.exe"), P("B") });

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
            Assert.Equal(1, summary.Failed);
            Assert.Equal(1, summary.Launched);
            var line = Assert.Single(TestLog.LinesContaining(name), l => l.Contains("\tLAUNCH\t"));
            Assert.Contains("did not return", line, StringComparison.Ordinal);
        }

        [Fact]
        public void NotifierExtension_SwallowsAndLogs()
        {
            var name = TestLog.Unique("Tray");

            new ThrowingNotifier().SafeNotify(name);

            Assert.Contains("\tERROR\t", TestLog.Read(), StringComparison.Ordinal);
        }

        private sealed class ThrowingNotifier : INotifier
        {
            public void Notify(string message) => throw new InvalidOperationException("tray gone");
        }

        // Throws a Win32Exception for direct starts (UseShellExecute = false) and records every attempt
        private sealed class ElevationStarter : IProcessStarter
        {
            private readonly int _nativeError;

            public ElevationStarter(string existing, int nativeError)
            {
                Inner = new FakeProcessStarter(existing);
                _nativeError = nativeError;
            }

            public FakeProcessStarter Inner { get; }

            public List<bool> ShellFlags { get; } = new List<bool>();

            public FakeProcessStarter.FakeHandle? LastHandle { get; private set; }

            public IDisposable? Start(ProcessStartInfo psi)
            {
                ShellFlags.Add(psi.UseShellExecute);
                if (!psi.UseShellExecute) throw new Win32Exception(_nativeError);
                LastHandle = new FakeProcessStarter.FakeHandle();
                return LastHandle;
            }

            public bool FileExists(string path) => Inner.FileExists(path);
        }
    }
}
