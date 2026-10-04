using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Silent takeover in the load path (StartupSession) and in --launch mode. Sandbox and fakes only.
    public sealed class TakeoverSessionTests : IDisposable
    {
        private static readonly DateTime Clock = new DateTime(2026, 10, 2, 6, 0, 0, DateTimeKind.Utc);

        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        private static Task<List<StartupProgram>> Inline(Func<List<StartupProgram>> read) => Task.FromResult(read());

        private static LaunchRunner Runner(IProcessStarter starter) =>
            new LaunchRunner(new ProgramLauncher(starter, TestHostExe), new FakeNotifier(), new FakeDialog(), launch => Task.FromResult(launch()));

        // Own entry enabled (gate open); A is Windows-disabled and stored Enabled with its fingerprint; N is Windows-run
        private void SeedAAndN()
        {
            _sandbox.SeedRun("StartupController", "\"C:\\x\\StartupController.exe\" --launch");
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedApproved("A", Approved(0x03));
            _sandbox.SeedStoredOrder(new[] { "A" }, new[] { "A" });
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue,
                new[] { "A|" + RunFingerprint.Compute(RegistryValueKind.String, @"C:\Apps\A.exe") }, RegistryValueKind.MultiString);
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
        }

        [Fact]
        public async Task LaunchLoad_TakesOverN_ButLaunchesOnlyA_ThenTheNextLogonLaunchesBoth()
        {
            SeedAAndN();
            var model = new StartupListModel();

            Assert.True(await StartupSession.LoadProgramsAsync(new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp), model, new FakeNotifier(), takeOver: true, Inline));

            var n = model.Programs.Single(p => p.Name == "N");
            Assert.True(n.Enabled);
            Assert.True(n.TakenOver);
            Assert.False(model.Programs.Single(p => p.Name == "A").TakenOver);

            var starter = FakeProcessStarter.AllExesExist();
            await StartupSession.RunLaunchModeAsync(Runner(starter), StartupSession.LaunchableAtLogon(model.EnabledPrograms()),
                loaded: true, blocked: false, notificationsSilenced: true, new FakeNotifier(), _ => Task.CompletedTask);
            Assert.Equal(new[] { @"C:\Apps\A.exe" }, starter.Started.Select(s => s.FileName));

            // Next logon: a new service and model; nothing is left to take over
            var next = new StartupListModel();
            Assert.True(await StartupSession.LoadProgramsAsync(new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp), next, new FakeNotifier(), takeOver: true, Inline));
            Assert.All(next.Programs, p => Assert.False(p.TakenOver));
            var nextStarter = FakeProcessStarter.AllExesExist();
            await StartupSession.RunLaunchModeAsync(Runner(nextStarter), StartupSession.LaunchableAtLogon(next.EnabledPrograms()),
                loaded: true, blocked: false, notificationsSilenced: true, new FakeNotifier(), _ => Task.CompletedTask);
            Assert.Equal(new[] { @"C:\Apps\A.exe", @"C:\Apps\N.exe" }, nextStarter.Started.Select(s => s.FileName));
        }

        [Fact]
        public async Task ManualLaunch_OfATakenOverRow_Launches()
        {
            SeedAAndN();
            var model = new StartupListModel();
            await StartupSession.LoadProgramsAsync(new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp), model, new FakeNotifier(), takeOver: true, Inline);
            var starter = FakeProcessStarter.AllExesExist();

            var result = new ProgramLauncher(starter, TestHostExe).Launch(model.Programs.Single(p => p.TakenOver));

            Assert.True(result.Success);
            Assert.Equal(@"C:\Apps\N.exe", Assert.Single(starter.Started).FileName);
        }

        [Fact]
        public async Task TakeoverOff_ListsNothingNew()
        {
            SeedAAndN();
            var model = new StartupListModel();

            Assert.True(await StartupSession.LoadProgramsAsync(new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp), model, new FakeNotifier(), takeOver: false, Inline));

            Assert.Equal(new[] { "A" }, model.Programs.Select(p => p.Name));
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.ApprovedPath, "N"));
        }

        [Fact]
        public async Task TakeoverThrows_TheListStillLoads_AndAnErrorIsLogged()
        {
            var registry = new ThrowingTakeover(new StartupRegistryService(_sandbox.Root, () => DateTime.UtcNow, IsTestApp));
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedApproved("A", Approved(0x03));
            var model = new StartupListModel();
            var notifier = new FakeNotifier();

            Assert.True(await StartupSession.LoadProgramsAsync(registry, model, notifier, takeOver: true, Inline));

            Assert.Equal("A", Assert.Single(model.Programs).Name);
            Assert.Empty(notifier.Messages);
            Assert.NotEmpty(TestLog.LinesContaining("Takeover of Windows startup entries failed; Windows keeps starting them | Exception: InvalidOperationException: " + registry.Marker));
        }

        // ---------- D-T6: taken-over programs that start nowhere ----------

        private void SeedStranded(byte[]? ownApproved, bool ownPresent = true)
        {
            if (ownPresent)
                _sandbox.SeedRun("StartupController", "\"C:\\x\\StartupController.exe\" --launch");
            if (ownApproved != null)
                _sandbox.SeedApproved("StartupController", ownApproved);
            _sandbox.SeedRun("N1", @"C:\Apps\N1.exe");
            _sandbox.SeedApproved("N1", Approved(0x03));
            _sandbox.SeedRun("N2", @"C:\Apps\N2.exe");
            _sandbox.SeedApproved("N2", Approved(0x03));
            _sandbox.SeedRun("Back", @"C:\Apps\Back.exe"); // re-enabled in Task Manager: Windows starts it
            _sandbox.SeedApproved("Back", Approved(0x02));
            _sandbox.SeedRun("Off", @"C:\Apps\Off.exe"); // switched off in the app: not expected to start
            _sandbox.SeedApproved("Off", Approved(0x03));
            _sandbox.SeedRun("Chg", @"C:\Apps\Chg-new.exe"); // Changed (D7): not launched anyway
            _sandbox.SeedApproved("Chg", Approved(0x03));
            _sandbox.SeedStoredOrder(new[] { "N1", "N2", "Back", "Off", "Chg" }, new[] { "N1", "N2", "Back", "Chg" });
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue, new[]
            {
                "N1|" + RunFingerprint.Compute(RegistryValueKind.String, @"C:\Apps\N1.exe"),
                "N2|" + RunFingerprint.Compute(RegistryValueKind.String, @"C:\Apps\N2.exe"),
                "Back|" + RunFingerprint.Compute(RegistryValueKind.String, @"C:\Apps\Back.exe"),
                "Chg|" + RunFingerprint.Compute(RegistryValueKind.String, @"C:\Apps\Chg-old.exe"),
            }, RegistryValueKind.MultiString);
            _sandbox.SeedAppValue(RegistrySandbox.TakenOverValue, new[] { "N1", "N2", "Back", "Off", "Chg", "Gone" }, RegistryValueKind.MultiString);
        }

        // N3: an own entry that can't start the app with --launch counts as missing
        [Theory]
        [InlineData("\"C:\\Other\\Tool.exe\" --launch")]
        [InlineData("\"C:\\x\\StartupController.exe\"")]
        [InlineData("\"C:\\x\\StartupController.exe\" --launchx")]
        public async Task OwnEntryInvalid_WarnsAboutTheStrandedPrograms(string ownCommand)
        {
            SeedStranded(Approved(0x02));
            _sandbox.SeedRun("StartupController", ownCommand);

            var warning = await StartupSession.CheckStrandedTakeoverAsync(new StartupRegistryService(_sandbox.Root, () => DateTime.UtcNow, IsTestApp), read => Task.FromResult(read()));

            Assert.Equal(StartupSession.StrandedWarning(2), warning);
        }

        [Fact]
        public async Task OwnEntryNotAString_WarnsAboutTheStrandedPrograms()
        {
            SeedStranded(Approved(0x02), ownPresent: false);
            _sandbox.SeedRunValue("StartupController", 1, RegistryValueKind.DWord);

            var warning = await StartupSession.CheckStrandedTakeoverAsync(new StartupRegistryService(_sandbox.Root, () => DateTime.UtcNow, IsTestApp), read => Task.FromResult(read()));

            Assert.Equal(StartupSession.StrandedWarning(2), warning);
        }

        [Theory]
        [InlineData(false, null)]
        [InlineData(true, (byte)0x03)]
        public async Task OwnEntryMissingOrDisabled_WarnsAboutTheStrandedPrograms(bool ownPresent, byte? ownApproved)
        {
            SeedStranded(ownApproved.HasValue ? Approved(ownApproved.Value) : null, ownPresent);
            var before = _sandbox.Dump(RegistrySandbox.AppPath);

            var warning = await StartupSession.CheckStrandedTakeoverAsync(new StartupRegistryService(_sandbox.Root, () => DateTime.UtcNow, IsTestApp), read => Task.FromResult(read()));

            Assert.Equal(StartupSession.StrandedWarning(2), warning);
            Assert.Contains("Task Manager > Startup apps", warning, StringComparison.Ordinal);
            Assert.Equal(before, _sandbox.Dump(RegistrySandbox.AppPath)); // read-only
            Assert.NotEmpty(TestLog.LinesContaining("2 taken-over program(s) will not start at logon"));
        }

        [Fact]
        public async Task OwnEntryEnabled_NoWarning()
        {
            SeedStranded(Approved(0x02));

            Assert.Null(await StartupSession.CheckStrandedTakeoverAsync(new StartupRegistryService(_sandbox.Root, () => DateTime.UtcNow, IsTestApp), read => Task.FromResult(read())));
        }

        [Fact]
        public async Task NoRecord_NoWarning()
        {
            _sandbox.SeedRun("N1", @"C:\Apps\N1.exe");
            _sandbox.SeedApproved("N1", Approved(0x03));

            Assert.Null(await StartupSession.CheckStrandedTakeoverAsync(new StartupRegistryService(_sandbox.Root, () => DateTime.UtcNow, IsTestApp), read => Task.FromResult(read())));
        }

        [Fact]
        public async Task StrandedCheckThrows_NoWarning_ErrorLogged()
        {
            var registry = new ThrowingTakeover(new StartupRegistryService(_sandbox.Root, () => DateTime.UtcNow, IsTestApp));

            Assert.Null(await StartupSession.CheckStrandedTakeoverAsync(registry, read => Task.FromResult(read())));
            Assert.NotEmpty(TestLog.LinesContaining("Could not check whether taken-over programs still start at logon | Exception: InvalidOperationException: " + registry.Marker));
        }

        [Fact]
        public void Form1_ShowsTheStrandedWarningOnlyOutsideLaunchMode_EvenWhenSilenced()
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "Form1.cs"));

            Assert.Matches(@"if\s*\(\s*!IsLaunchMode\s*\)\s*\{\s*var warning = await StartupSession\.CheckStrandedTakeoverAsync\(_registry\);", code);
            var balloon = code.Substring(code.IndexOf("private void ShowWarningBalloon", StringComparison.Ordinal));
            balloon = balloon.Substring(0, balloon.IndexOf('}') + 1);
            Assert.DoesNotContain("GetSilenceNotifications", balloon, StringComparison.Ordinal);
            Assert.Contains("ToolTipIcon.Warning", balloon, StringComparison.Ordinal);
        }

        // ---------- security L2: no UAC fallback at logon ----------

        private static FakeProcessStarter ElevationRequired() => new FakeProcessStarter(@"C:\Apps\Admin.exe")
        {
            ThrowOnStart = new System.ComponentModel.Win32Exception(740) // ERROR_ELEVATION_REQUIRED
        };

        [Fact]
        public void LaunchAtLogon_ProgramNeedingElevation_IsNotStartedThroughTheShell()
        {
            var starter = ElevationRequired();
            var marker = TestLog.Unique("Admin");

            var result = new ProgramLauncher(starter, TestHostExe).LaunchAtLogon(P(marker, enabled: true, path: @"C:\Apps\Admin.exe"));

            Assert.False(result.Success);
            Assert.Equal("Requires elevation, not started at logon", result.Error);
            Assert.False(Assert.Single(starter.Started).UseShellExecute);
            Assert.Contains(TestLog.LinesContaining(marker), l => l.Contains("requires elevation, not started", StringComparison.Ordinal));
        }

        [Fact]
        public void ManualLaunch_ProgramNeedingElevation_StillFallsBackToTheShell()
        {
            var starter = ElevationRequired();

            new ProgramLauncher(starter, TestHostExe).Launch(P("Admin", enabled: true, path: @"C:\Apps\Admin.exe"));

            // The launcher retries with the same ProcessStartInfo, switched to the shell
            Assert.Equal(2, starter.Started.Count);
            Assert.True(starter.Started[1].UseShellExecute);
        }

        // N5: a shortcut with "Run as administrator" (SLDF_RUNAS_USER) is not shell-started at logon
        private static string WriteShortcutHeader(string directory, bool runAsAdmin)
        {
            Directory.CreateDirectory(directory);
            var header = new byte[0x4C];
            BitConverter.GetBytes(0x4C).CopyTo(header, 0);
            new Guid("00021401-0000-0000-C000-000000000046").ToByteArray().CopyTo(header, 4);
            BitConverter.GetBytes(runAsAdmin ? 0x2000u | 0x1u : 0x1u).CopyTo(header, 20);
            var path = Path.Combine(directory, (runAsAdmin ? "admin" : "plain") + ".lnk");
            File.WriteAllBytes(path, header);
            return path;
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void LaunchAtLogon_RunAsAdminShortcut_IsNotStarted(bool runAsAdmin, bool started)
        {
            var dir = Path.Combine(Path.GetTempPath(), "StartupController.Tests", "lnk-" + Guid.NewGuid().ToString("N"));
            try
            {
                var lnk = WriteShortcutHeader(dir, runAsAdmin);
                var starter = new FakeProcessStarter(lnk);
                var marker = TestLog.Unique("Lnk");

                var result = new ProgramLauncher(starter, TestHostExe).LaunchAtLogon(P(marker, enabled: true, path: "\"" + lnk + "\""));

                Assert.Equal(started, result.Success);
                Assert.Equal(started ? 1 : 0, starter.Started.Count);
                if (!started)
                    Assert.Contains(TestLog.LinesContaining(marker), l => l.Contains("requires elevation, not started", StringComparison.Ordinal));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void ManualLaunch_RunAsAdminShortcut_IsStillShellStarted()
        {
            var dir = Path.Combine(Path.GetTempPath(), "StartupController.Tests", "lnk-" + Guid.NewGuid().ToString("N"));
            try
            {
                var lnk = WriteShortcutHeader(dir, runAsAdmin: true);
                var starter = new FakeProcessStarter(lnk);

                Assert.True(new ProgramLauncher(starter, TestHostExe).Launch(P("Lnk", enabled: true, path: "\"" + lnk + "\"")).Success);
                Assert.True(Assert.Single(starter.Started).UseShellExecute);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void ShortcutRunsAsAdmin_IsFalseForMissingShortOrForeignFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), "StartupController.Tests", "lnk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var shortFile = Path.Combine(dir, "short.lnk");
                File.WriteAllBytes(shortFile, new byte[] { 0x4C, 0, 0, 0 });
                var foreign = Path.Combine(dir, "foreign.lnk");
                var bytes = new byte[0x4C];
                BitConverter.GetBytes(0x4C).CopyTo(bytes, 0);
                BitConverter.GetBytes(0x2000u).CopyTo(bytes, 20); // flag set, but no shell link CLSID
                File.WriteAllBytes(foreign, bytes);

                Assert.False(ProgramLauncher.ShortcutRunsAsAdmin(Path.Combine(dir, "missing.lnk")));
                Assert.False(ProgramLauncher.ShortcutRunsAsAdmin(shortFile));
                Assert.False(ProgramLauncher.ShortcutRunsAsAdmin(foreign));
                Assert.True(ProgramLauncher.ShortcutRunsAsAdmin(WriteShortcutHeader(dir, runAsAdmin: true)));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void LogonLaunch_HasNoDefaultImplementation()
        {
            var method = typeof(IProgramLauncher).GetMethod(nameof(IProgramLauncher.LaunchAtLogon))!;

            Assert.True(method.IsAbstract);
        }

        [Fact]
        public async Task LaunchSequence_UsesTheLogonLaunch()
        {
            var starter = ElevationRequired();

            var summary = await Runner(starter).LaunchSequenceAsync(new[] { P("Admin", enabled: true, path: @"C:\Apps\Admin.exe") });

            Assert.Equal(0, summary.Launched);
            Assert.Single(starter.Started);
        }

        [Fact]
        public void LaunchableAtLogon_DropsTakenOverPrograms()
        {
            var a = P("A", enabled: true);
            var n = P("N", enabled: true);
            n.TakenOver = true;

            Assert.Equal(new[] { a }, StartupSession.LaunchableAtLogon(new[] { a, n }));
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void TakeoverRequested_FollowsTheSetting(bool setting, bool expected)
        {
            Assert.Equal(expected, StartupSession.TakeoverRequested(new StubSettings { Launch = () => setting }));
        }

        [Fact]
        public void TakeoverRequested_SettingThrows_IsFalse()
        {
            Assert.False(StartupSession.TakeoverRequested(new StubSettings { Launch = () => throw new UnauthorizedAccessException("denied") }));
        }

        private sealed class ThrowingTakeover : IStartupRegistry
        {
            private readonly IStartupRegistry _inner;

            public ThrowingTakeover(IStartupRegistry inner) => _inner = inner;

            public string Marker { get; } = TestLog.Unique("takeover-boom");

            public IReadOnlySet<string> TakeOverWindowsEntries(bool launchSettingOn) => throw new InvalidOperationException(Marker);

            public int CountStrandedTakenOver() => throw new InvalidOperationException(Marker);
            public List<StartupProgram> GetStartupPrograms() => _inner.GetStartupPrograms();
            public void AddThisApplicationToStartup(string exePath) => throw new NotSupportedException();
            public void RemoveThisApplicationFromStartup() => throw new NotSupportedException();
            public StoredOrder LoadStoredOrder() => _inner.LoadStoredOrder();
            public void SaveStartupOrder(StoredOrder displayed) => _inner.SaveStartupOrder(displayed);
        }

        private sealed class StubSettings : IUserSettings
        {
            public Func<bool> Launch { get; set; } = () => false;

            public bool GetLaunchProgramsOnStartup() => Launch();
            public bool GetSilenceNotifications() => false;
            public void SetSilenceNotifications(bool value) { }
            public bool GetStartToTray() => false;
            public void SetStartToTray(bool value) { }
            public void SetLaunchProgramsOnStartup(bool value) { }
            public bool GetAutoSaveOnChange() => false;
            public void SetAutoSaveOnChange(bool value) { }
        }
    }
}
