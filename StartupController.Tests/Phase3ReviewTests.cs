using System.Diagnostics;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Tester review of Phase 3: adversarial parser inputs, self-launch guard bypass attempts, activation lifecycle.
    // Fake starter / fake file checks only; test-only event names.
    public class Phase3ReviewTests
    {
        private const string Self = @"C:\Program Files\StartupController\StartupController.exe";

        private static Func<string, bool> Existing(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        private static readonly Func<string, bool> Nothing = _ => false;

        // ---------- Parser ----------

        [Theory]
        [InlineData(@"C:\100%\x.exe -y")]
        [InlineData(@"%")]
        [InlineData(@"%%")]
        [InlineData(@"%NOPE_NOT_SET_XYZ%\x.exe")]
        [InlineData("\"")]
        [InlineData("\"\"")]
        [InlineData("\"\" -y")]
        [InlineData("\"   \"")]
        [InlineData("   ")]
        [InlineData("\"\"\"")]
        [InlineData("C:\\a\0b.exe")]
        [InlineData("C:\\a|b<>.exe -y")]
        [InlineData("\"C:\\a\" \"b\" \"c")]
        public void Parser_NeverThrows_OnOddInput(string command)
        {
            var parsed = CommandLineParser.Parse(command, Nothing, expandEnvironment: true);

            Assert.NotNull(parsed.ExePath);
            Assert.NotNull(parsed.Arguments);
        }

        [Fact]
        public void EmptyQuotes_GiveNoExe()
        {
            var parsed = CommandLineParser.Parse("\"\" -y", _ => true, false);

            Assert.Equal("", parsed.ExePath);
        }

        [Fact]
        public void PercentWithoutClosing_IsKeptLiterally()
        {
            var (exe, args) = CommandLineParser.Split(@"C:\100%\x.exe -y", Nothing);

            Assert.Equal(@"C:\100%\x.exe", exe);
            Assert.Equal("-y", args);
        }

        [Fact]
        public void ArgumentsWithQuotes_ArePreservedVerbatim()
        {
            var (exe, args) = CommandLineParser.Split("\"C:\\a b\\x.exe\" --name=\"a b\" \"c d\"", Nothing);

            Assert.Equal(@"C:\a b\x.exe", exe);
            Assert.Equal("--name=\"a b\" \"c d\"", args);
        }

        [Fact]
        public void Unquoted_ArgumentsWithQuotes_ArePreserved()
        {
            var (exe, args) = CommandLineParser.Split("C:\\Program Files\\x.exe --name=\"a b\"", Existing(@"C:\Program Files\x.exe"));

            Assert.Equal(@"C:\Program Files\x.exe", exe);
            Assert.Equal("--name=\"a b\"", args);
        }

        [Fact]
        public void TrailingSpaces_AreTrimmed()
        {
            var (exe, args) = CommandLineParser.Split(@"C:\Program Files\x.exe    ", Existing(@"C:\Program Files\x.exe"));

            Assert.Equal(@"C:\Program Files\x.exe", exe);
            Assert.Equal("", args);
        }

        [Fact]
        public void DoubleSpaceInsidePath_Resolves()
        {
            var (exe, args) = CommandLineParser.Split(@"C:\a  b\x.exe -y", Existing(@"C:\a  b\x.exe"));

            Assert.Equal(@"C:\a  b\x.exe", exe);
            Assert.Equal("-y", args);
        }

        [Fact]
        public void Unc_Unquoted_WithSpaces_Resolves()
        {
            var (exe, args) = CommandLineParser.Split(@"\\srv\share\My Apps\x.exe /q", Existing(@"\\srv\share\My Apps\x.exe"));

            Assert.Equal(@"\\srv\share\My Apps\x.exe", exe);
            Assert.Equal("/q", args);
        }

        [Fact]
        public void ForwardSlashes_Resolve()
        {
            var (exe, args) = CommandLineParser.Split("C:/Program Files/x.exe -y", Existing("C:/Program Files/x.exe"));

            Assert.Equal("C:/Program Files/x.exe", exe);
            Assert.Equal("-y", args);
        }

        [Fact]
        public void DotExeInDirectory_WithSpaces_ShortestExecutableTokenWins() // was DotExeInDirectory_WithSpaces_LongestWins (security M1)
        {
            var (exe, args) = CommandLineParser.Split(@"C:\My.exe Tools\app.exe", Existing(@"C:\My.exe", @"C:\My.exe Tools\app.exe"));

            Assert.Equal(@"C:\My.exe", exe);
            Assert.Equal(@"Tools\app.exe", args);
        }

        [Fact]
        public void Unquoted_ExistingExe_ArgumentThatLooksLikeAPath_IsNotMergedIntoExe()
        {
            var (exe, args) = CommandLineParser.Split(@"C:\Tools\x.exe C:\data\file.txt", Existing(@"C:\Tools\x.exe"));

            Assert.Equal(@"C:\Tools\x.exe", exe);
            Assert.Equal(@"C:\data\file.txt", args);
        }

        [Fact]
        public void Unquoted_ShortestFirst_IgnoresAPlantedCombinedName() // was Unquoted_LongestFirst_PicksACombinedNameWhenSuchAFileExists (security M1)
        {
            // Shortest-first closes the longest-first residual risk: a file literally named "x.exe y.exe" never wins
            var (exe, args) = CommandLineParser.Split(@"C:\Tools\x.exe y.exe", Existing(@"C:\Tools\x.exe", @"C:\Tools\x.exe y.exe"));

            Assert.Equal(@"C:\Tools\x.exe", exe);
            Assert.Equal("y.exe", args);
        }

        [Fact]
        public void VeryLongCommand_ManyTokens_FinishesQuickly()
        {
            var command = @"C:\a\x.exe " + string.Join(" ", Enumerable.Repeat("arg", 30000));
            var sw = Stopwatch.StartNew();

            var (exe, _) = CommandLineParser.Split(command, Nothing);

            Assert.Equal(@"C:\a\x.exe", exe);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        }

        [Fact]
        public void VeryLongCommand_DoesNotProbeTheFileSystemWithoutBound()
        {
            // Every space is a candidate and each one is a file-system probe (a network probe for UNC paths).
            var command = @"\\srv\share\x.exe " + string.Join(" ", Enumerable.Repeat("a", 5000));
            int probes = 0;

            CommandLineParser.Split(command, _ => { probes++; return false; });

            Assert.True(probes < 1000, $"{probes} file probes for one Run value");
        }

        // ---------- Self-launch guard ----------

        private static ProgramLauncher Launcher(FakeProcessStarter starter) => new ProgramLauncher(starter, Self);

        [Theory]
        [InlineData(@"C:\x\StartupController.exe.")]
        [InlineData("\"C:\\x\\StartupController.exe \"")]
        [InlineData("\"C:\\x\\StartupController.exe...\"")]
        [InlineData(@"C:\x\StartupController.")]
        [InlineData(@"\\?\C:\x\StartupController.exe")]
        [InlineData(@"\\.\C:\x\StartupController.exe")]
        [InlineData("C:/x/StartupController.exe")]
        [InlineData(@"..\StartupController.exe")]
        [InlineData(@".\StartupController.exe")]
        [InlineData(@"C:\x\STARTUPCONTROLLER.EXE")]
        [InlineData(@"C:\x\..\y\StartupController.exe")]
        [InlineData("StartupController.exe.")]
        [InlineData("StartupController.")]
        [InlineData("\"StartupController.exe \"")]
        [InlineData(@"\\srv\share\StartupController.exe")]
        public void SelfVariants_AreBlocked_AndNothingIsStarted(string command)
        {
            var starter = FakeProcessStarter.AllExesExist();

            var result = Launcher(starter).Launch(P("X", path: command));

            Assert.True(result.Blocked, $"not blocked: {command}; started: {string.Join("|", starter.Started.Select(s => s.FileName))}");
            Assert.Empty(starter.Started);
        }

        [Theory]
        [InlineData("StartupController.exe.")]
        [InlineData("StartupController.")]
        public void SelfVariants_BareName_NothingExists_StillBlocked(string command)
        {
            var starter = new FakeProcessStarter();

            var result = Launcher(starter).Launch(P("X", path: command));

            Assert.True(result.Blocked, $"not blocked: {command}; started: {string.Join("|", starter.Started.Select(s => s.FileName))}");
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void Self_ViaRealNormalizer_OnAnExistingTempCopy_ShortAndRelativeForms_AreBlocked()
        {
            var dir = Path.Combine(Path.GetTempPath(), "StartupController.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var selfPath = Path.Combine(dir, "My Long Self Name.exe");
                File.WriteAllText(selfPath, "x");
                var short83 = GetShort(selfPath);
                var forms = new List<string>
                {
                    selfPath,
                    selfPath.ToUpperInvariant(),
                    Path.Combine(dir, "sub", "..", "My Long Self Name.exe"),
                    @"\\?\" + selfPath,
                    selfPath + "."
                };
                if (!string.IsNullOrEmpty(short83)) forms.Add(short83);

                var missed = new List<string>();
                foreach (var form in forms)
                {
                    var starter = FakeProcessStarter.AllExesExist();
                    var launcher = new ProgramLauncher(starter, selfPath); // real normalizer
                    var result = launcher.Launch(P("X", path: "\"" + form + "\" --launch"));
                    if (!result.Blocked) missed.Add(form);
                }
                Assert.True(missed.Count == 0, "not blocked: " + string.Join(" || ", missed));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint GetShortPathNameW(string path, System.Text.StringBuilder buf, uint len);

        private static string GetShort(string path)
        {
            var sb = new System.Text.StringBuilder(520);
            var n = GetShortPathNameW(path, sb, 520);
            return n > 0 && n < 520 ? sb.ToString() : "";
        }

        [Fact]
        public void EmptySelfPath_StillBlocksByName()
        {
            var starter = FakeProcessStarter.AllExesExist();

            var result = new ProgramLauncher(starter, "").Launch(P("X", path: @"C:\z\StartupController.exe"));

            Assert.True(result.Blocked);
        }

        [Fact]
        public void NormalizerThrows_DoesNotCrashTheLaunch()
        {
            var starter = FakeProcessStarter.AllExesExist();
            var launcher = new ProgramLauncher(starter, Self, _ => throw new IOException("boom"));

            var result = launcher.Launch(P("X", path: @"C:\z\other.exe"));

            Assert.True(result.Success);
        }

        // ---------- InstanceActivation lifecycle ----------

        private static string Name() => @"Local\StartupController.Tests.Review." + Guid.NewGuid().ToString("N");

        [Fact]
        public void Dispose_Twice_IsSafe()
        {
            var activation = new InstanceActivation(Name(), () => { });

            activation.Dispose();
            activation.Dispose();
        }

        [Fact]
        public void Signal_AfterDispose_ReturnsFalse()
        {
            var name = Name();
            var activation = new InstanceActivation(name, () => { });
            activation.Dispose();

            Assert.False(InstanceActivation.SignalExisting(name));
        }

        [Fact]
        public void Dispose_FromInsideTheCallback_DoesNotHangForLong()
        {
            var name = Name();
            InstanceActivation? activation = null;
            using var done = new ManualResetEventSlim(false);
            activation = new InstanceActivation(name, () =>
            {
                activation!.Dispose();
                done.Set();
            });

            InstanceActivation.SignalExisting(name);

            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void Dispose_WhileCallbackRuns_ReturnsWithinTheJoinTimeout_AndDoesNotThrow()
        {
            var name = Name();
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var activation = new InstanceActivation(name, () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
            InstanceActivation.SignalExisting(name);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

            var sw = Stopwatch.StartNew();
            activation.Dispose();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
            release.Set();
        }

        [Fact]
        public void ManyConcurrentSignals_AtLeastOneActivation_NoExceptions()
        {
            var name = Name();
            int count = 0;
            using var first = new ManualResetEventSlim(false);
            using var activation = new InstanceActivation(name, () => { Interlocked.Increment(ref count); first.Set(); });

            Parallel.For(0, 200, _ => InstanceActivation.SignalExisting(name));

            Assert.True(first.Wait(TimeSpan.FromSeconds(5)));
            Assert.InRange(Volatile.Read(ref count), 1, 200);
        }

        [Fact]
        public void SignalThenImmediateDispose_DoesNotThrowOrHang()
        {
            for (int i = 0; i < 50; i++)
            {
                var name = Name();
                var activation = new InstanceActivation(name, () => { });
                InstanceActivation.SignalExisting(name);
                activation.Dispose();
            }
        }

        [Fact]
        public void NullCallback_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new InstanceActivation(Name(), null!));
        }

        [Fact]
        public void RecreateAfterDispose_WithTheSameName_Works()
        {
            var name = Name();
            new InstanceActivation(name, () => { }).Dispose();
            using var activated = new ManualResetEventSlim(false);
            using var again = new InstanceActivation(name, activated.Set);

            Assert.True(InstanceActivation.SignalExisting(name));
            Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        }
    }
}
