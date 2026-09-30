using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Security re-review of Phase 3 (L-A to L-D, I-2). FakeProcessStarter only; test-only event names.
    public class Phase3SecurityFollowUpTests
    {
        private const string Self = @"C:\Program Files\StartupController\StartupController.exe";

        private static ProgramLauncher Launcher(FakeProcessStarter starter) => new ProgramLauncher(starter, Self, p => p, (a, b) => false);

        // ---------- L-A: no current-directory search for bare names ----------

        [Fact]
        public void BareNameShellStart_WorksInTheSystemDirectory()
        {
            var starter = new FakeProcessStarter();

            Launcher(starter).Launch(P("H", path: "helper.exe /x"));

            var psi = Assert.Single(starter.Started);
            Assert.True(psi.UseShellExecute);
            Assert.Equal(Environment.SystemDirectory, psi.WorkingDirectory);
        }

        [Fact]
        public void Main_SetsTheCurrentDirectoryToTheSystemDirectory()
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "Program.cs"));

            Assert.Contains("Directory.SetCurrentDirectory(Environment.SystemDirectory)", code);
        }

        // ---------- L-B: a pre-created activation event is never used ----------

        [Fact]
        public void PreCreatedManualResetEvent_IsNotUsed_AndNothingListens()
        {
            var name = @"Local\StartupController.Tests.Squat." + Guid.NewGuid().ToString("N");
            using var squatter = new EventWaitHandle(true, EventResetMode.ManualReset, name);
            int activations = 0;

            Assert.Throws<ActivationEventExistsException>(() => new InstanceActivation(name, () => Interlocked.Increment(ref activations)));

            Thread.Sleep(200);
            Assert.Equal(0, Volatile.Read(ref activations));
            Assert.True(squatter.WaitOne(0)); // still set: no listener consumed or reset it
        }

        [Fact]
        public void ActivationEventExistsException_IsAnIOException()
        {
            // Program.CreateActivation relies on this to fall back to running without activation
            Assert.IsAssignableFrom<IOException>(new ActivationEventExistsException("x"));
        }

        [Fact]
        public void NewEvent_StillActivates()
        {
            var name = @"Local\StartupController.Tests.Squat." + Guid.NewGuid().ToString("N");
            using var activated = new ManualResetEventSlim(false);
            using var activation = new InstanceActivation(name, activated.Set);

            Assert.True(InstanceActivation.SignalExisting(name));
            Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        }

        // ---------- L-C: no first-token split of a path ----------

        [Theory]
        [InlineData(@"D:\Apps v2\tool", @"D:\Apps")]
        [InlineData(@"D:\node.js tools\run", @"D:\node.js")]
        [InlineData(@"D:\Apps v2\tool --flag", @"D:\Apps")]
        public void PathWithSpaces_WholeCommandMissing_IsNotFound_PlantedPrefixNeverStarts(string command, string planted)
        {
            var starter = new FakeProcessStarter(planted, planted + ".exe");

            var result = Launcher(starter).Launch(P("P", path: command));

            Assert.True(result.NotFound);
            Assert.Empty(starter.Started);
            Assert.Equal(command, CommandLineParser.Split(command, starter.FileExists).exePath);
        }

        [Fact]
        public void BareNameWithArgs_StillGoesToTheShell()
        {
            var starter = new FakeProcessStarter();

            Launcher(starter).Launch(P("H", path: "helper /x"));

            var psi = Assert.Single(starter.Started);
            Assert.Equal("helper", psi.FileName);
            Assert.Equal("/x", psi.Arguments);
            Assert.True(psi.UseShellExecute);
        }

        // ---------- I-2: CreateProcess probe order ----------

        [Fact]
        public void QuotedExtensionless_PrefersDotExe()
        {
            var (exe, args) = CommandLineParser.Split("\"C:\\a b\\app\" -q", _ => true);

            Assert.Equal(@"C:\a b\app.exe", exe);
            Assert.Equal("-q", args);
        }

        // ---------- L-D: timeouts ----------

        [Fact]
        public async Task ManualLaunch_HasNoTimeout()
        {
            var starter = new FakeProcessStarter(@"C:\Apps\slow.exe");
            var runner = new LaunchRunner(Launcher(starter), new FakeNotifier(), new FakeDialog(),
                async launch => { await Task.Delay(500); return launch(); },
                launchTimeout: TimeSpan.FromMilliseconds(50));

            var result = await runner.LaunchManualAsync(P("Slow", path: @"C:\Apps\slow.exe"));

            Assert.True(result.Success);
            Assert.Single(starter.Started);
        }

        [Fact]
        public async Task Sequence_LateCompletionAfterTimeout_IsLoggedByName()
        {
            var name = TestLog.Unique("Late");
            var late = new TaskCompletionSource<LaunchResult>();
            var runner = new LaunchRunner(Launcher(new FakeProcessStarter()), new FakeNotifier(), new FakeDialog(),
                _ => late.Task, launchTimeout: TimeSpan.FromMilliseconds(100));

            var summary = await runner.LaunchSequenceAsync(new[] { P(name, path: @"C:\Secret\late.exe --token") });
            Assert.Equal(1, summary.Failed);

            late.SetResult(LaunchResult.Ok);

            string? line = null;
            SpinWait.SpinUntil(() => (line = TestLog.LinesContaining(name).FirstOrDefault(l => l.Contains("after the launch timeout"))) != null, TimeSpan.FromSeconds(5));
            Assert.NotNull(line);
            Assert.Contains("started", line);
            Assert.DoesNotContain("Secret", line);
            Assert.DoesNotContain("--token", line);
        }

        [Fact]
        public async Task Sequence_LateFailureAfterTimeout_IsLogged()
        {
            var name = TestLog.Unique("LateFail");
            var late = new TaskCompletionSource<LaunchResult>();
            var runner = new LaunchRunner(Launcher(new FakeProcessStarter()), new FakeNotifier(), new FakeDialog(),
                _ => late.Task, launchTimeout: TimeSpan.FromMilliseconds(100));

            await runner.LaunchSequenceAsync(new[] { P(name) });
            late.SetException(new InvalidOperationException("boom"));

            string? line = null;
            SpinWait.SpinUntil(() => (line = TestLog.LinesContaining(name).FirstOrDefault(l => l.Contains("after the launch timeout"))) != null, TimeSpan.FromSeconds(5));
            Assert.NotNull(line);
            Assert.Contains("failed", line);
        }
    }
}
