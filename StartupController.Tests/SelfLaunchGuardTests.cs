using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // 3.1a: StartupController never launches itself, whatever the Run value name. Every test uses
    // FakeProcessStarter and a fake normalizePath, so nothing is started and no real path is resolved.
    public class SelfLaunchGuardTests
    {
        private const string Self = @"C:\Program Files\StartupController\StartupController.exe";
        private const string ShortSelf = @"C:\PROGRA~1\STARTU~1\STARTU~1.EXE";

        // Stands in for GetFullPath + GetLongPathNameW: resolves ".." and maps the 8.3 name to the long one
        private static string Normalize(string path) =>
            string.Equals(path, ShortSelf, StringComparison.OrdinalIgnoreCase) ? Self : Path.GetFullPath(path);

        private static ProgramLauncher Launcher(FakeProcessStarter starter) => new ProgramLauncher(starter, Self, Normalize);

        private static LaunchRunner Runner(ProgramLauncher launcher, FakeNotifier notifier, FakeDialog dialog) =>
            new LaunchRunner(launcher, notifier, dialog, launch => Task.FromResult(launch()));

        [Fact]
        public void SelfPath_UnderAnotherName_IsBlocked_AndLogsTheNameOnly()
        {
            var name = TestLog.Unique("Foo");
            var starter = new FakeProcessStarter(Self);

            var result = Launcher(starter).Launch(P(name, path: "\"" + Self + "\" --launch"));

            Assert.True(result.Blocked);
            Assert.False(result.Success);
            Assert.Empty(starter.Started);
            var lines = TestLog.LinesContaining(name);
            var warning = Assert.Single(lines);
            Assert.Contains("\tWARN\t", warning, StringComparison.Ordinal);
            Assert.DoesNotContain(@"Program Files", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("--launch", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void SelfPath_DifferentCase_IsBlocked()
        {
            var starter = new FakeProcessStarter(Self);

            var result = Launcher(starter).Launch(P("Foo", path: "\"c:\\program files\\startupcontroller\\STARTUPCONTROLLER.EXE\" --launch"));

            Assert.True(result.Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void StartupControllerExe_InAnotherDirectory_IsBlocked()
        {
            var starter = new FakeProcessStarter(@"D:\Old\StartupController.exe");

            var result = Launcher(starter).Launch(P("Old", path: "\"D:\\Old\\StartupController.exe\" --launch"));

            Assert.True(result.Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void UnquotedWithSpaces_ResolvedByPrefix_IsBlocked()
        {
            var starter = new FakeProcessStarter(Self);

            var result = Launcher(starter).Launch(P("Foo", path: Self + " --launch"));

            Assert.True(result.Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void NoExtension_ResolvedByExeProbe_IsBlocked()
        {
            var starter = new FakeProcessStarter(Self);

            var result = Launcher(starter).Launch(P("Foo", path: "\"C:\\Program Files\\StartupController\\StartupController\" --launch"));

            Assert.True(result.Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void NoExtension_RenamedTarget_ResolvedByExeProbe_IsBlocked()
        {
            // "C:\Apps\sc" resolves (via the .exe probe) to a file that normalizes to this app
            var starter = new FakeProcessStarter(@"C:\Apps\sc.exe");
            var launcher = new ProgramLauncher(starter, Self, p => p.StartsWith(@"C:\Apps\sc", StringComparison.OrdinalIgnoreCase) ? Self : p);

            var result = launcher.Launch(P("Foo", path: @"C:\Apps\sc --launch"));

            Assert.True(result.Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void EnvironmentVariable_IsBlockedAfterExpansion()
        {
            var expanded = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\StartupController\StartupController.exe");
            var starter = new FakeProcessStarter(expanded);

            var result = Launcher(starter).Launch(P("Foo", path: @"%ProgramFiles%\StartupController\StartupController.exe"));

            Assert.True(result.Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void RelativeSegments_AreBlockedAfterNormalization()
        {
            const string path = @"C:\Program Files\x\..\StartupController\StartupController.exe";
            var starter = new FakeProcessStarter(path);

            var result = Launcher(starter).Launch(P("Foo", path: "\"" + path + "\""));

            Assert.True(result.Blocked);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void ShortName_83_IsBlockedAfterNormalization()
        {
            var starter = new FakeProcessStarter(ShortSelf);

            var result = Launcher(starter).Launch(P("Foo", path: ShortSelf + " --launch"));

            Assert.True(result.Blocked);
            Assert.Empty(starter.Started);
        }

        [Theory]
        [InlineData(@"C:\Missing\StartupController.exe --launch")]
        [InlineData("StartupController.exe --launch")]
        [InlineData("StartupController --launch")]
        public void ShellFallback_ForAMissingSelfFile_IsBlocked_NoShellStart(string command)
        {
            var starter = new FakeProcessStarter(); // nothing exists: the shell fallback would be next

            var result = Launcher(starter).Launch(P("Foo", path: command));

            Assert.True(result.Blocked);
            Assert.Empty(starter.Started);
        }

        [Theory]
        [InlineData(@"C:\x\StartupControllerHelper.exe", @"C:\x\StartupControllerHelper.exe")]
        [InlineData(@"C:\x\MyStartupController.exe", @"C:\x\MyStartupController.exe")]
        [InlineData(@"notepad.exe C:\x\StartupController.exe", "notepad.exe")] // the name only in the arguments
        public void SimilarNames_AndNameInArguments_AreNotBlocked(string command, string expectedExe)
        {
            var starter = new FakeProcessStarter(@"C:\x\StartupControllerHelper.exe", @"C:\x\MyStartupController.exe");

            var result = Launcher(starter).Launch(P("Other", path: command));

            Assert.True(result.Success);
            Assert.False(result.Blocked);
            Assert.Equal(expectedExe, Assert.Single(starter.Started).FileName);
        }

        [Fact]
        public async Task LaunchSequence_SkipsTheSelfEntry_AndCountsIt()
        {
            var starter = new FakeProcessStarter(@"C:\Apps\A.exe", @"C:\Apps\B.exe", Self);
            var notifier = new FakeNotifier();
            var dialog = new FakeDialog();
            var programs = new List<StartupProgram>
            {
                P("A", enabled: true),
                P("S", enabled: true, path: "\"" + Self + "\" --launch"),
                P("B", enabled: true),
            };

            var summary = await Runner(Launcher(starter), notifier, dialog).LaunchSequenceAsync(programs);

            Assert.Equal(new[] { @"C:\Apps\A.exe", @"C:\Apps\B.exe" }, starter.Started.Select(s => s.FileName));
            Assert.Equal("Launched 2 of 3", summary.Text);
            Assert.Equal(1, summary.Skipped);
            Assert.Equal(0, summary.Failed);
            Assert.DoesNotContain(notifier.Messages, m => m.Contains(" S "));   // no balloon for the blocked entry
            Assert.Equal(2, notifier.Messages.Count);
            Assert.Empty(dialog.Warnings);
        }

        [Fact]
        public async Task ManualLaunch_OnTheSelfEntry_ShowsTheDialog()
        {
            var starter = new FakeProcessStarter(Self);
            var notifier = new FakeNotifier();
            var dialog = new FakeDialog();

            var result = await Runner(Launcher(starter), notifier, dialog).LaunchManualAsync(P("S", path: "\"" + Self + "\" --launch"));

            Assert.True(result.Blocked);
            Assert.Empty(starter.Started);
            Assert.Equal(LaunchRunner.BlockedMessage, Assert.Single(dialog.Warnings));
            Assert.Empty(notifier.Messages);
        }

        [Fact]
        public void DefaultNormalizePath_KeepsAMissingPathAsItsFullPath()
        {
            // GetLongPathNameW fails for paths that don't exist; the full path is used instead
            var path = @"C:\" + Guid.NewGuid().ToString("N") + @"\x\..\app.exe";

            Assert.Equal(Path.GetFullPath(path), PathHelper.NormalizePath(path));
        }
    }
}
