using System.Text.RegularExpressions;
using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // 2.3, revised for the silent takeover (reverses D2): StartupApproved is written in exactly one place, the takeover
    // in StartupRegistryService, and only as REG_BINARY. Run is written only for the app's own entry. The uninstall
    // helper writes StartupApproved only in ReturnToWindows. Nothing deletes StartupApproved values or keys.
    public sealed class StartupApprovedWriteGuardTests : IDisposable
    {
        private static readonly DateTime Clock = new DateTime(2026, 10, 2, 8, 30, 0, DateTimeKind.Utc);

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

        private static List<string> ApprovedWriteViolations(string directory)
        {
            var violations = new List<string>();
            foreach (var file in SourceScan.SourceFiles(directory))
            {
                foreach (var why in FindApprovedWrites(SourceScan.ReadCode(file)))
                    violations.Add($"{Path.GetFileName(file)}: {why}");
            }
            return violations;
        }

        private static string ServiceCode() =>
            SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "StartupRegistryService.cs"));

        // The member whose signature line comes last before index (signature lines start with an access modifier)
        private static string EnclosingMember(string code, int index)
        {
            var signatures = Regex.Matches(code.Substring(0, index), @"\n[ \t]*(?:public|internal|private|protected)\b[^\n;=]*?\b(?<name>\w+)\s*\(");
            return signatures.Count == 0 ? "" : signatures[^1].Groups["name"].Value;
        }

        private static List<string> NormalizedCalls(string code, string method) =>
            Regex.Matches(code, @"\b\w+\s*\.\s*" + method + @"\s*\((?<args>[^;]*)\)\s*;")
                .Select(m => Regex.Replace(m.Groups["args"].Value, @"\s+", ""))
                .ToList();

        // ---------- production: the app ----------

        [Fact]
        public void ProductionCode_OpensStartupApprovedForWritingOnlyInTheTakeover()
        {
            var violations = ApprovedWriteViolations(SourceScan.ProductionSourceDirectory());

            Assert.Equal(new[] { "StartupRegistryService.cs: creates/opens StartupApproved for writing" }, violations);

            var code = ServiceCode();
            var create = Assert.Single(Regex.Matches(code, @"CreateSubKey\s*\(\s*STARTUP_APPROVED_KEY\b"));
            Assert.Equal(nameof(StartupRegistryService.TakeOverWindowsEntries), EnclosingMember(code, create.Index));
        }

        [Fact]
        public void ProductionCode_SetsValuesOnlyInTheThreeAllowedForms()
        {
            // Own Run entry (REG_SZ), the order lists (REG_MULTI_SZ) and the takeover's StartupApproved value (REG_BINARY)
            var code = ServiceCode();

            Assert.Equal(
                new[]
                {
                    "nameof(WriteApprovedDisabled):name,value,RegistryValueKind.Binary",
                    "nameof(WriteMultiString):name,values,RegistryValueKind.MultiString",
                    "nameof(AddThisApplicationToStartup):STARTUP_CONTROLLER_NAME,$\"\\\"{exePath}\\\"--launch\",RegistryValueKind.String",
                }.OrderBy(s => s, StringComparer.Ordinal),
                Regex.Matches(code, @"\b\w+\s*\.\s*SetValue\s*\((?<args>[^;]*)\)\s*;")
                    .Select(m => $"nameof({EnclosingMember(code, m.Index)}):" + Regex.Replace(m.Groups["args"].Value, @"\s+", ""))
                    .OrderBy(s => s, StringComparer.Ordinal));

            var otherFiles = SourceScan.FilesMatching(SourceScan.ProductionSourceDirectory(), @"\.\s*SetValue\s*\(",
                "StartupRegistryService.cs", "UserSettings.cs", "RegistryLaunchSessionStore.cs");
            Assert.Empty(otherFiles);
        }

        [Fact]
        public void ProductionCode_DeletesOnlyTheOwnRunEntry()
        {
            var code = ServiceCode();

            var delete = Assert.Single(Regex.Matches(code, @"\.\s*DeleteValue\s*\("));
            Assert.Equal(nameof(StartupRegistryService.RemoveThisApplicationFromStartup), EnclosingMember(code, delete.Index));
            Assert.Equal(new[] { "STARTUP_CONTROLLER_NAME,false" }, NormalizedCalls(code, "DeleteValue"));
            Assert.Empty(SourceScan.FilesMatching(SourceScan.ProductionSourceDirectory(), @"\bDeleteSubKey(?:Tree)?\s*\("));
        }

        [Fact]
        public void ProductionCode_OpensRunWritableOnlyForTheOwnEntry()
        {
            const string writableRun = @"(?:CreateSubKey\s*\(\s*(?:RUN_KEY|(?:AppRegistryPaths\s*\.\s*)?RunKey)\b|OpenSubKey\s*\(\s*(?:RUN_KEY|(?:AppRegistryPaths\s*\.\s*)?RunKey)\s*,\s*(?:writable\s*:\s*)?true\b)";
            var found = new List<string>();
            foreach (var file in SourceScan.SourceFiles(SourceScan.ProductionSourceDirectory()))
            {
                var code = SourceScan.ReadCode(file);
                foreach (Match m in Regex.Matches(code, writableRun))
                    found.Add($"{Path.GetFileName(file)}:{EnclosingMember(code, m.Index)}");
            }

            Assert.Equal(
                new[] { "StartupRegistryService.cs:AddThisApplicationToStartup", "StartupRegistryService.cs:RemoveThisApplicationFromStartup" },
                found.OrderBy(s => s, StringComparer.Ordinal));
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

        // ---------- production: the uninstall helper ----------

        [Fact]
        public void Helper_OpensStartupApprovedForWritingOnlyInReturnToWindows()
        {
            var violations = ApprovedWriteViolations(SourceScan.UninstallSourceDirectory());

            Assert.Equal(new[] { "ReturnToWindows.cs: opens StartupApproved writable" }, violations);
        }

        [Fact]
        public void Helper_WritesOnlyBinaryApprovedValuesAndTheRecord_AndNeverDeletes()
        {
            var directory = SourceScan.UninstallSourceDirectory();
            var code = SourceScan.ReadCode(Path.Combine(directory, "ReturnToWindows.cs"));

            Assert.Equal(
                new[]
                {
                    "name,StartupApprovedState.Enabled(),RegistryValueKind.Binary",
                    "TakeoverRecord.ValueName,names,RegistryValueKind.MultiString",
                },
                NormalizedCalls(code, "SetValue"));
            Assert.Equal(new[] { "ReturnToWindows.cs" }, SourceScan.FilesMatching(directory, @"\.\s*SetValue\s*\("));
            Assert.Empty(SourceScan.FilesMatching(directory, @"\b(?:DeleteValue|DeleteSubKey|DeleteSubKeyTree|CreateSubKey)\s*\("));
        }

        [Fact]
        public void Helper_OpensRunReadOnly_AndTheAppKeyWritableOnlyToPruneTheRecord()
        {
            var directory = SourceScan.UninstallSourceDirectory();
            var code = SourceScan.ReadCode(Path.Combine(directory, "ReturnToWindows.cs"));

            Assert.Empty(SourceScan.FilesMatching(directory, @"OpenSubKey\s*\(\s*AppRegistryPaths\s*\.\s*RunKey\s*,\s*(?:writable\s*:\s*)?true"));
            var writableApp = Assert.Single(Regex.Matches(code, @"OpenSubKey\s*\(\s*AppRegistryPaths\s*\.\s*AppKey\s*,\s*(?:writable\s*:\s*)?true"));
            Assert.Equal("PruneRecord", EnclosingMember(code, writableApp.Index));
            Assert.Empty(SourceScan.FilesMatching(directory, @"Registry\s*\.\s*" + "LocalMachine"));
            Assert.Equal(new[] { "Program.cs" }, SourceScan.FilesMatching(directory, @"Registry\s*\.\s*" + "CurrentUser"));
        }

        // ---------- the guard itself ----------

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
        public void EnclosingMember_FindsTheMethod()
        {
            const string code = "class C\n{\n    public void A()\n    {\n        x.Foo();\n    }\n\n    internal static int B(int y)\n    {\n        z.Bar();\n    }\n}\n";

            Assert.Equal("A", EnclosingMember(code, code.IndexOf("Foo", StringComparison.Ordinal)));
            Assert.Equal("B", EnclosingMember(code, code.IndexOf("Bar", StringComparison.Ordinal)));
        }

        // ---------- behaviour ----------

        private void SeedCycleData()
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedRun("B", "\"C:\\Apps\\B b.exe\" --min");
            _sandbox.SeedRun("C", @"%ProgramFiles%\C\C.exe");
            _sandbox.SeedRun("WinRuns02", @"C:\Apps\W.exe");
            _sandbox.SeedRun("NoValue", @"C:\Apps\N.exe");
            _sandbox.SeedRunValue("Dword", 1, RegistryValueKind.DWord);
            _sandbox.SeedRun("StartupController", "\"C:\\x\\StartupController.exe\" --launch");
            _sandbox.SeedApproved("A", new byte[] { 0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 });
            _sandbox.SeedApproved("B", Approved(0x07));
            _sandbox.SeedApproved("C", new byte[] { 0x01 });
            _sandbox.SeedApproved("WinRuns02", Approved(0x02));
            _sandbox.SeedApproved("StartupController", Approved(0x02)); // enabled: the takeover gate is open
            _sandbox.SeedApproved("Orphan", Approved(0x03)); // approved value without a Run entry
        }

        private async Task RunCycle(StartupRegistryService service, ProgramLauncher launcher, bool takeOver)
        {
            service.TakeOverWindowsEntries(takeOver);
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

        [Fact]
        public async Task ListSaveLaunchCycles_WithTakeover_WriteOnlyTheWindowsRunEntries_Once()
        {
            SeedCycleData();
            var runBefore = Dump(RegistrySandbox.RunPath);
            var approvedBefore = Dump(RegistrySandbox.ApprovedPath);

            var service = new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp);
            var starter = FakeProcessStarter.AllExesExist();
            var launcher = new ProgramLauncher(starter, TestHostExe);
            await RunCycle(service, launcher, takeOver: true);
            var approvedAfterFirst = Dump(RegistrySandbox.ApprovedPath);
            await RunCycle(service, launcher, takeOver: true);
            await RunCycle(service, launcher, takeOver: true);

            Assert.NotEmpty(starter.Started);
            Assert.Equal(runBefore, Dump(RegistrySandbox.RunPath));
            Assert.Equal(approvedAfterFirst, Dump(RegistrySandbox.ApprovedPath));

            var disabled = Convert.ToHexString(StartupApprovedState.Disabled(Clock));
            var expected = approvedBefore
                .Where(l => !l.StartsWith("WinRuns02|", StringComparison.Ordinal))
                .Append($"NoValue|Binary|{disabled}")
                .Append($"WinRuns02|Binary|{disabled}")
                .OrderBy(l => l.Split('|')[0], StringComparer.OrdinalIgnoreCase)
                .ToList();
            Assert.Equal(expected, Dump(RegistrySandbox.ApprovedPath));
            Assert.Null(_sandbox.ReadKind(RegistrySandbox.ApprovedPath, "Dword"));
        }

        [Fact]
        public async Task ListSaveLaunchCycles_WithoutTakeover_LeaveRunAndStartupApprovedByteIdentical()
        {
            SeedCycleData();
            var runBefore = Dump(RegistrySandbox.RunPath);
            var approvedBefore = Dump(RegistrySandbox.ApprovedPath);

            var service = new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp);
            var starter = FakeProcessStarter.AllExesExist();
            var launcher = new ProgramLauncher(starter, TestHostExe);
            for (int cycle = 0; cycle < 3; cycle++)
                await RunCycle(service, launcher, takeOver: false);

            Assert.NotEmpty(starter.Started);
            Assert.Equal(runBefore, Dump(RegistrySandbox.RunPath));
            Assert.Equal(approvedBefore, Dump(RegistrySandbox.ApprovedPath));
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.TakenOverValue));
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
