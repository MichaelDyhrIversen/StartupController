using System.Text.RegularExpressions;
using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // 2.3 / D2: the app is an add-on and never writes StartupApproved (or Run, apart from its own entry).
    public sealed class StartupApprovedWriteGuardTests : IDisposable
    {
        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        // Identifiers assigned a string that contains "StartupApproved" (e.g. const STARTUP_APPROVED_KEY)
        private static readonly Regex ApprovedConstant = new Regex(@"\b(?<name>\w+)\s*=\s*@?""[^""]*Startup" + @"Approved[^""]*""");

        // Violations in one file's code (comments already stripped)
        internal static List<string> FindApprovedWrites(string code)
        {
            var targets = ApprovedConstant.Matches(code).Select(m => Regex.Escape(m.Groups["name"].Value)).ToList();
            targets.Add(@"@?""[^""]*Startup" + @"Approved[^""]*""");
            var target = "(?:" + string.Join("|", targets) + ")";

            var rules = new (string Pattern, string Why)[]
            {
                (@"OpenSubKey\s*\(\s*" + target + @"\s*,\s*(?:writable\s*:\s*)?true\b", "opens StartupApproved writable"),
                (@"OpenSubKey\s*\(\s*" + target + @"\s*,\s*RegistryKeyPermissionCheck\s*\.\s*ReadWriteSubTree", "opens StartupApproved writable"),
                (@"OpenSubKey\s*\(\s*" + target + @"\s*,\s*RegistryRights\b", "opens StartupApproved with explicit rights"),
                (@"CreateSubKey\s*\(\s*" + target, "creates/opens StartupApproved for writing"),
                (@"DeleteSubKey(?:Tree)?\s*\(\s*" + target, "deletes StartupApproved"),
            };
            return rules.Where(r => Regex.IsMatch(code, r.Pattern)).Select(r => r.Why).ToList();
        }

        [Fact]
        public void ProductionCode_NeverOpensStartupApprovedForWriting()
        {
            var violations = new List<string>();
            foreach (var file in SourceScan.SourceFiles(SourceScan.ProductionSourceDirectory()))
            {
                foreach (var why in FindApprovedWrites(SourceScan.ReadCode(file)))
                    violations.Add($"{Path.GetFileName(file)}: {why}");
            }

            Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        }

        [Fact]
        public void ProductionCode_MentionsStartupApprovedOnlyInTheRegistryService()
        {
            var files = SourceScan.FilesMatching(SourceScan.ProductionSourceDirectory(), "Startup" + "Approved\\\\");

            Assert.Equal(new[] { "StartupRegistryService.cs" }, files);
        }

        [Fact]
        public void ProductionCode_HasNoSetProgramEnabled()
        {
            var files = SourceScan.FilesMatching(SourceScan.ProductionSourceDirectory(), @"\bSetProgram" + "Enabled\\b");

            Assert.Empty(files);
        }

        [Theory]
        [InlineData("const string K = @\"Software\\Explorer\\StartupApproved\\Run\"; using var k = root.OpenSubKey(K, true);")]
        [InlineData("const string K = @\"Software\\Explorer\\StartupApproved\\Run\"; using var k = root.OpenSubKey(K, writable: true);")]
        [InlineData("const string K = @\"Software\\Explorer\\StartupApproved\\Run\"; using var k = root.CreateSubKey(K);")]
        [InlineData("using var k = root.CreateSubKey(@\"Software\\Explorer\\StartupApproved\\Run\", true);")]
        [InlineData("using var k = root.OpenSubKey(\"Software\\\\StartupApproved\\\\Run\", RegistryKeyPermissionCheck.ReadWriteSubTree);")]
        public void Guard_DetectsWritableOpens(string code)
        {
            Assert.NotEmpty(FindApprovedWrites(code));
        }

        [Theory]
        [InlineData("const string K = @\"Software\\Explorer\\StartupApproved\\Run\"; using var k = root.OpenSubKey(K, false);")]
        [InlineData("const string K = @\"Software\\Explorer\\StartupApproved\\Run\"; using var k = root.OpenSubKey(K);")]
        [InlineData("const string R = @\"Software\\Run\"; using var k = root.OpenSubKey(R, true);")]
        public void Guard_AllowsReadOnlyOpens(string code)
        {
            Assert.Empty(FindApprovedWrites(code));
        }

        [Fact]
        public async Task ListSaveLaunchCycles_LeaveRunAndStartupApprovedByteIdentical()
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedRun("B", "\"C:\\Apps\\B b.exe\" --min");
            _sandbox.SeedRun("C", @"%ProgramFiles%\C\C.exe");
            _sandbox.SeedRun("WinRuns02", @"C:\Apps\W.exe");
            _sandbox.SeedRun("NoValue", @"C:\Apps\N.exe");
            _sandbox.SeedRun("StartupController", "\"C:\\x\\StartupController.exe\" --launch");
            _sandbox.SeedApproved("A", new byte[] { 0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 });
            _sandbox.SeedApproved("B", Approved(0x07));
            _sandbox.SeedApproved("C", new byte[] { 0x01 });
            _sandbox.SeedApproved("WinRuns02", Approved(0x02));
            _sandbox.SeedApproved("StartupController", Approved(0x03));
            _sandbox.SeedApproved("Orphan", Approved(0x03)); // approved value without a Run entry
            var runBefore = Dump(RegistrySandbox.RunPath);
            var approvedBefore = Dump(RegistrySandbox.ApprovedPath);

            var service = new StartupRegistryService(_sandbox.Root);
            var starter = FakeProcessStarter.AllExesExist();
            var launcher = new ProgramLauncher(starter, TestHostExe);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                var model = new StartupListModel();
                model.Load(service.GetStartupPrograms());
                foreach (var program in model.Programs.ToList())
                    model.Toggle(program);
                model.MoveTop(model.Programs[^1]);
                var saver = new OrderSaveCoordinator(model, service, new FakeNotifier());
                Assert.True(await saver.SaveAsync(manual: true));
                Assert.True(saver.SaveNow(out _));
                foreach (var program in model.EnabledPrograms())
                    Assert.True(launcher.Launch(program).Success);
            }

            Assert.NotEmpty(starter.Started);
            Assert.Equal(runBefore, Dump(RegistrySandbox.RunPath));
            Assert.Equal(approvedBefore, Dump(RegistrySandbox.ApprovedPath));
        }

        private List<string> Dump(string subPath)
        {
            return _sandbox.ReadAllValues(subPath)
                .Select(v => $"{v.Name}|{v.Kind}|{Format(v.Data)}")
                .ToList();
        }

        private static string Format(object? data) => data switch
        {
            byte[] bytes => Convert.ToHexString(bytes),
            string[] strings => string.Join("\u0001", strings),
            null => "<null>",
            _ => data.ToString() ?? ""
        };
    }
}
