using System.ComponentModel;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // 3.1: one launch path for the Launch button and --launch mode. Logging and notifying happen outside
    // the launch try. FakeProcessStarter only: nothing is started.
    public class LaunchRunnerTests
    {
        private static LaunchRunner Runner(IProcessStarter starter, INotifier notifier, FakeDialog? dialog = null) =>
            new LaunchRunner(new ProgramLauncher(starter, TestHostExe), notifier, dialog ?? new FakeDialog(), launch => Task.FromResult(launch()));

        private static List<string> LaunchLines(string name) =>
            TestLog.LinesContaining(name).Where(l => l.Contains("\tLAUNCH\t")).ToList();

        [Fact]
        public async Task Success_IsLogged_AndNotified()
        {
            var name = TestLog.Unique("Ok");
            var notifier = new FakeNotifier();

            var result = await Runner(new FakeProcessStarter(@"C:\Apps\ok.exe"), notifier).LaunchManualAsync(P(name, path: @"C:\Apps\ok.exe"));

            Assert.True(result.Success);
            Assert.Contains("SUCCESS", Assert.Single(LaunchLines(name)), StringComparison.Ordinal);
            Assert.Equal("Launched: " + name, Assert.Single(notifier.Messages));
        }

        [Fact]
        public async Task NotifierThrows_LaunchIsStillLoggedAsSuccess()
        {
            var name = TestLog.Unique("Loud");

            var result = await Runner(new FakeProcessStarter(@"C:\Apps\x.exe"), new ThrowingNotifier()).LaunchManualAsync(P(name, path: @"C:\Apps\x.exe"));

            Assert.True(result.Success);
            Assert.Contains("SUCCESS", Assert.Single(LaunchLines(name)), StringComparison.Ordinal);
        }

        [Fact]
        public async Task NotFound_IsNotified_InBothModes()
        {
            var notifier = new FakeNotifier();
            var runner = Runner(new FakeProcessStarter(), notifier);

            await runner.LaunchManualAsync(P("Gone", path: @"C:\Missing\gone.exe"));
            var summary = await runner.LaunchSequenceAsync(new[] { P("Gone2", path: @"C:\Missing\gone2.exe") });

            Assert.Equal(2, notifier.Messages.Count);
            Assert.All(notifier.Messages, m => Assert.Contains("not found", m, StringComparison.Ordinal));
            Assert.Equal(1, summary.Failed);
            Assert.Equal("Launched 0 of 1", summary.Text);
        }

        [Fact]
        public async Task StarterThrows_IsAFailure_LoggedOnce()
        {
            var name = TestLog.Unique("Boom");
            var notifier = new FakeNotifier();
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe") { ThrowOnStart = new Win32Exception("boom") };

            var result = await Runner(starter, notifier).LaunchManualAsync(P(name, path: @"C:\Apps\x.exe"));

            Assert.False(result.Success);
            var line = Assert.Single(LaunchLines(name));
            Assert.Contains("FAILURE", line, StringComparison.Ordinal);
            Assert.Contains("boom", line, StringComparison.Ordinal);
            Assert.Contains("boom", Assert.Single(notifier.Messages), StringComparison.Ordinal);
        }

        [Fact]
        public async Task LauncherThrows_IsCaught_AsAFailure()
        {
            var name = TestLog.Unique("Crash");
            var runner = new LaunchRunner(new ThrowingLauncher(), new FakeNotifier(), new FakeDialog(), launch => Task.FromResult(launch()));

            var summary = await runner.LaunchSequenceAsync(new[] { P(name), P(name + "b") });

            Assert.Equal(2, summary.Failed);
            Assert.Contains("FAILURE", LaunchLines(name).First(), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task EmptyCommand_IsAnError_WithNoStart(string path)
        {
            var starter = new FakeProcessStarter();
            var notifier = new FakeNotifier();

            var result = await Runner(starter, notifier).LaunchManualAsync(P("Empty", path: path));

            Assert.False(result.Success);
            Assert.Empty(starter.Started);
            Assert.Single(notifier.Messages);
        }

        [Fact]
        public async Task Sequence_OneFailureDoesNotStopTheRest_AndSummarizes()
        {
            var starter = new FakeProcessStarter(@"C:\Apps\A.exe", @"C:\Apps\C.exe");
            var notifier = new FakeNotifier();

            var summary = await Runner(starter, notifier).LaunchSequenceAsync(new[] { P("A"), P("B"), P("C") });

            Assert.Equal(new[] { @"C:\Apps\A.exe", @"C:\Apps\C.exe" }, starter.Started.Select(s => s.FileName));
            Assert.Equal(new LaunchSummary(2, 3, 0, 1), summary);
            Assert.Equal("Launched 2 of 3", summary.Text);
            Assert.Equal(new[] { "Starting A (1 of 3)", "Starting C (3 of 3)" }, notifier.Messages.Where(m => m.StartsWith("Starting", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task Sequence_DisposesEveryHandle()
        {
            var starter = new FakeProcessStarter(@"C:\Apps\A.exe", @"C:\Apps\B.exe");

            await Runner(starter, new FakeNotifier()).LaunchSequenceAsync(new[] { P("A"), P("B") });

            Assert.Equal(2, starter.Handles.Count);
            Assert.All(starter.Handles, h => Assert.True(h.Disposed));
        }

        private sealed class ThrowingNotifier : INotifier
        {
            public void Notify(string message) => throw new InvalidOperationException("tray gone");
        }

        private sealed class ThrowingLauncher : IProgramLauncher
        {
            public LaunchResult Launch(StartupProgram program) => throw new InvalidOperationException("launcher crashed");
            public LaunchResult LaunchAtLogon(StartupProgram program) => throw new InvalidOperationException("launcher crashed");
        }
    }
}
