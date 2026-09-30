using Microsoft.Win32;
using StartupController.Tests.Infrastructure;

namespace StartupController.Tests
{
    // Guards the hard rule: tests never touch the real HKCU startup/app keys and never start processes.
    // Implemented as a source scan (no Roslyn, see SourceScan) over .cs files, with comments stripped.
    public class SandboxGuardTests
    {
        private const string ThisFile = nameof(SandboxGuardTests) + ".cs";

        // Optional namespace qualification, e.g. StartupController.ProcessStarter or global::StartupController.Form1
        private const string Q = @"(?:global::)?(?:\w+\s*\.\s*)*";

        // Patterns are split so this file does not match itself
        private static readonly (string Pattern, string Why)[] Forbidden =
        {
            (@"new\s+" + Q + @"StartupRegistryService\s*\(\s*\)", "parameterless StartupRegistryService() targets real HKCU"),
            (@"new\s+" + Q + @"UserSettings\s*\(\s*Registry\s*\.\s*" + "CurrentUser", "UserSettings on real HKCU"),
            (@"new\s+" + Q + @"Form1\s*\(\s*\)", "parameterless Form1() wires real HKCU and real process start"),
            (@"new\s+" + Q + @"ProcessStarter\s*\(", "real ProcessStarter would launch programs"),
            (@"Process\s*\.\s*" + "Start\\s*\\(", "tests must not start processes"),
            (@"Registry\s*\.\s*" + "LocalMachine", "HKLM is out of scope"),
            (@"Registry\s*\.\s*(Users|ClassesRoot|CurrentConfig|PerformanceData)\b", "other registry hives are off limits"),
            (@"Registry\s*\.\s*(Set|Get)" + @"Value\s*\(", "Registry.SetValue/GetValue take absolute paths and bypass the sandbox"),
            (@"RegistryKey\s*\.\s*Open(Remote)?" + "BaseKey", "OpenBaseKey bypasses the sandbox"),
            (@"\b(StartupRegistryService|Form1|ProcessStarter)\s+\w+\s*=\s*" + @"new\s*\(\s*\)", "target-typed new() of a type whose default constructor is real"),
            (@"new\s+" + @"(System\.Diagnostics\.)?Process\s*\(", "tests must not create Process objects"),
            (@"Activator\s*\.\s*" + "CreateInstance", "reflection could bypass the constructor checks"),
            (@"OpenLog" + @"File\s*\(", "LoggingService.OpenLogFile shell-executes the log file"),
            (@"RegistryHive\s*\.\s*" + "CurrentUser", "RegistryHive.CurrentUser reaches real HKCU without the sandbox"),
            (@"using\s+static\s+Microsoft\.Win32\." + @"Registry\b", "using static Registry hides Registry.CurrentUser from these checks"),
            (@"typeof\s*\(\s*" + Q + @"ProcessStarter\b", "typeof(ProcessStarter) could be used to create the real starter"),
        };

        [Fact]
        public void TestCode_DoesNotUseRealRegistryOrProcessEntryPoints()
        {
            var violations = SourceScan.FindViolations(SourceScan.TestSourceDirectory(), Forbidden, ThisFile);

            Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        }

        [Fact]
        public void OnlyRegistrySandbox_TouchesRegistryCurrentUser()
        {
            var offenders = SourceScan.FilesMatching(
                SourceScan.TestSourceDirectory(),
                @"Registry\s*\.\s*" + "CurrentUser",
                nameof(RegistrySandbox) + ".cs", ThisFile);

            Assert.True(offenders.Count == 0, "Registry.CurrentUser used outside RegistrySandbox: " + string.Join(", ", offenders));
        }

        [Fact]
        public void ProductionCode_UsesRealHkcuOnlyInCompositionRoots()
        {
            // Program.cs and the designer ctor of Form1 wire the defaults; StartupRegistryService() defaults to HKCU
            var offenders = SourceScan.FilesMatching(
                SourceScan.ProductionSourceDirectory(),
                @"Registry\s*\.\s*" + "CurrentUser",
                "Program.cs", "Form1.cs", "StartupRegistryService.cs");

            Assert.True(offenders.Count == 0, "Registry.CurrentUser used outside the composition roots: " + string.Join(", ", offenders));
        }

        [Fact]
        public void ProductionCode_StartsProcessesOnlyThroughTheSeam()
        {
            // ProcessStarter is the launch seam; LoggingService.OpenLogFile opens the log in the default editor
            var offenders = SourceScan.FilesMatching(
                SourceScan.ProductionSourceDirectory(),
                @"Process\s*\.\s*" + "Start\\s*\\(",
                "ProcessStarter.cs", "LoggingService.cs");

            Assert.True(offenders.Count == 0, "Process.Start outside ProcessStarter: " + string.Join(", ", offenders));
        }

        [Fact]
        public void ProductionCode_DoesNotUseHklm()
        {
            var offenders = SourceScan.FilesMatching(SourceScan.ProductionSourceDirectory(), @"Registry\s*\.\s*" + "LocalMachine");

            Assert.True(offenders.Count == 0, "HKLM is out of scope (D6): " + string.Join(", ", offenders));
        }

        [Fact]
        public void StripComments_IgnoresCommentsButKeepsLiterals()
        {
            var code = SourceScan.StripComments(
                "var a = 1; // Process" + ".Start(x)\n" +
                "/* Registry" + ".CurrentUser\n */ var b = \"// kept\";\n" +
                "var c = @\"C:\\x \"\"q\"\" // kept too\"; var d = '\\'';");

            Assert.DoesNotContain("Process" + ".Start", code);
            Assert.DoesNotContain("Registry" + ".CurrentUser", code);
            Assert.Contains("\"// kept\"", code);
            Assert.Contains("// kept too", code);
            Assert.Equal(3, code.Count(ch => ch == '\n'));
        }

        [Fact]
        public void SandboxRoot_IsBelowTestsParent_AndIsDeletedOnDispose()
        {
            string relative;
            using (var sandbox = new RegistrySandbox())
            {
                relative = sandbox.RelativePath;
                Assert.StartsWith(RegistrySandbox.RequiredPrefix, sandbox.Root.Name, StringComparison.OrdinalIgnoreCase);
                sandbox.SeedRun("A", @"C:\Apps\A.exe");
                Assert.True(sandbox.KeyExists(RegistrySandbox.RunPath));
            }

            using var key = Registry.CurrentUser.OpenSubKey(relative, writable: false);
            Assert.Null(key);
        }

        [Fact]
        public void Guard_RejectsKeysOutsideTheSandboxParent()
        {
            // Read-only handles; the guard must throw before anything is written
            using var software = Registry.CurrentUser.OpenSubKey("Software", writable: false)!;
            Assert.Throws<InvalidOperationException>(() => RegistrySandbox.Guard(software));
        }

        [Fact]
        public void Guard_RejectsTheParentItself()
        {
            using var sandbox = new RegistrySandbox();
            using var parent = Registry.CurrentUser.OpenSubKey(RegistrySandbox.ParentPath, writable: false)!;

            Assert.Throws<InvalidOperationException>(() => RegistrySandbox.Guard(parent));
        }

        [Fact]
        public void Sweep_RemovesSandboxOfDeadProcess_AndKeepsLiveOnes()
        {
            // int.MaxValue is never a valid Windows pid, so this sandbox looks orphaned
            using var orphan = new RegistrySandbox(int.MaxValue);
            using var live = new RegistrySandbox();
            orphan.SeedRun("A", @"C:\Apps\A.exe");

            RegistrySandbox.SweepStale();

            Assert.False(RegistrySandbox.Exists(orphan.RelativePath));
            Assert.True(RegistrySandbox.Exists(live.RelativePath));
        }

        [Fact]
        public void Exists_RejectsPathsOutsideTheParent()
        {
            Assert.Throws<InvalidOperationException>(() => RegistrySandbox.Exists(@"Software\StartupController"));
        }
    }
}
