using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Reviewer additions: characterization of behaviour at 701437a that the first test pass did not pin,
    // plus resilience cases. Tests named *_Current pin bugs that Phase 2/3 flip.
    public sealed class GapTests : IDisposable
    {
        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        private static string Names(IEnumerable<StartupProgram> p) => string.Join(",", p.Select(x => x.Name));
        private static string EnabledNames(IEnumerable<StartupProgram> p) => string.Join(",", p.Where(x => x.Enabled).Select(x => x.Name));

        // --- ApplyCustomOrder ---

        [Fact]
        public void ApplyCustomOrder_EmptyOrder_ReturnsSameListInstance()
        {
            var input = List("B", "A");

            Assert.Same(input, StartupRegistryService.ApplyCustomOrder(input, Array.Empty<string>()));
        }

        [Fact]
        public void ApplyCustomOrder_DuplicateNamesInOrder_EnableByPosition_Current() // pinned (#2): positions, not names
        {
            var result = StartupRegistryService.ApplyCustomOrder(List("A", "B", "C"), new[] { "A", "A" });

            Assert.Equal("A,B,C", Names(result));
            Assert.Equal("A,B", EnabledNames(result)); // B is enabled only because the order has 2 entries
        }

        [Fact]
        public void ApplyCustomOrder_EmptySegmentInOrder_EnablesAnExtraProgram_Current() // pinned: legacy "A;;B" counts 3 entries
        {
            var result = StartupRegistryService.ApplyCustomOrder(List("A", "B", "C"), new[] { "A", "", "B" });

            Assert.Equal("A,B,C", Names(result));
            Assert.Equal("A,B,C", EnabledNames(result));
        }

        [Fact]
        public void ApplyCustomOrder_IsStableForUnlistedEntries_AndFlagsInputObjects()
        {
            var input = List("D", "C", "B", "A");

            var result = StartupRegistryService.ApplyCustomOrder(input, new[] { "B" });

            Assert.Equal("B,D,C,A", Names(result));
            Assert.Equal("B", EnabledNames(result));
            Assert.True(input.Single(p => p.Name == "B").Enabled); // same instances are flagged
        }

        // --- Service via sandbox ---

        [Fact]
        public void ApprovedValueOfWrongKind_IsTreatedAsMissing_AndListed_Current() // pinned: 'as byte[]' gives null -> disabled
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.CreateApprovedKey();
            using (var key = _sandbox.Root.CreateSubKey(RegistrySandbox.ApprovedPath, writable: true)!)
                key.SetValue("A", "03", RegistryValueKind.String);

            Assert.Equal("A", Assert.Single(new StartupRegistryService(_sandbox.Root).GetStartupPrograms()).Name);
        }

        [Fact]
        public void EmptyApprovedArray_IsListed_Current() // pinned via the registry: empty binary = disabled
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedApproved("A", Array.Empty<byte>());

            Assert.Single(new StartupRegistryService(_sandbox.Root).GetStartupPrograms());
        }

        [Fact]
        public void ListAndSave_NeverModifyRunOrStartupApproved()
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedRun("B", @"C:\Apps\B.exe");
            _sandbox.SeedApproved("A", Approved(0x03));
            _sandbox.SeedApproved("B", Approved(0x02));
            var service = new StartupRegistryService(_sandbox.Root);

            var listed = service.GetStartupPrograms();
            service.SaveStartupOrder(listed.Select(p => p.Name).ToList());
            service.GetStartupPrograms();

            Assert.Equal(Approved(0x03), (byte[])_sandbox.ReadValue(RegistrySandbox.ApprovedPath, "A")!);
            Assert.Equal(Approved(0x02), (byte[])_sandbox.ReadValue(RegistrySandbox.ApprovedPath, "B")!);
            Assert.Equal(@"C:\Apps\A.exe", _sandbox.ReadValue(RegistrySandbox.RunPath, "A"));
            Assert.Equal(@"C:\Apps\B.exe", _sandbox.ReadValue(RegistrySandbox.RunPath, "B"));
        }

        [Fact]
        public void SaveStartupOrder_EmptyList_WritesEmptyString_AndLoadsEmpty()
        {
            var service = new StartupRegistryService(_sandbox.Root);
            service.SaveStartupOrder(new List<string> { "A" });

            service.SaveStartupOrder(new List<string>());

            Assert.Equal("", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
            Assert.Empty(service.LoadStartupOrder());
        }

        [Fact]
        public void SaveStartupOrder_DoesNotTouchSettingsValues()
        {
            var settings = new UserSettings(_sandbox.Root);
            settings.SetStartToTray(true);

            new StartupRegistryService(_sandbox.Root).SaveStartupOrder(new List<string> { "A" });

            Assert.Equal(1, _sandbox.ReadValue(RegistrySandbox.AppPath, "StartToTray"));
            Assert.Equal("A", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
        }

        [Fact]
        public void LoadStartupOrder_NonStringValue_ReturnsEmpty()
        {
            _sandbox.SeedAppValue(RegistrySandbox.OrderValue, 5, RegistryValueKind.DWord);

            Assert.Empty(new StartupRegistryService(_sandbox.Root).LoadStartupOrder());
        }

        [Fact]
        public void Constructor_NullRoot_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new StartupRegistryService(null!));
            Assert.Throws<ArgumentNullException>(() => new UserSettings(null!));
        }

        [Fact]
        public void ReadOnlyRoot_SaveThrows_AndListStillWorks()
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedApproved("A", Approved(0x03));
            using var readOnly = _sandbox.OpenReadOnlyRoot();
            var service = new StartupRegistryService(readOnly);

            Assert.Single(service.GetStartupPrograms());
            Assert.ThrowsAny<Exception>(() => service.SaveStartupOrder(new List<string> { "A" }));
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
        }

        // --- UserSettings ---

        [Fact]
        public void Settings_DwordOtherThanOne_IsFalse()
        {
            _sandbox.SeedAppValue("StartToTray", 2, RegistryValueKind.DWord);
            _sandbox.SeedAppValue("SilenceNotifications", -1, RegistryValueKind.DWord);

            var settings = new UserSettings(_sandbox.Root);
            Assert.False(settings.GetStartToTray());
            Assert.False(settings.GetSilenceNotifications());
        }

        [Fact]
        public void Settings_SetOnReadOnlyRoot_Throws_AndDoesNotUpdateCache()
        {
            using var readOnly = _sandbox.OpenReadOnlyRoot();
            var settings = new UserSettings(readOnly);
            Assert.False(settings.GetAutoSaveOnChange());

            Assert.ThrowsAny<Exception>(() => settings.SetAutoSaveOnChange(true));

            Assert.False(settings.GetAutoSaveOnChange());
        }

        [Fact]
        public void Settings_AreIndependent()
        {
            var settings = new UserSettings(_sandbox.Root);

            settings.SetSilenceNotifications(true);

            Assert.True(settings.GetSilenceNotifications());
            Assert.False(settings.GetStartToTray());
            Assert.False(settings.GetLaunchProgramsOnStartup());
            Assert.False(settings.GetAutoSaveOnChange());
        }

        // --- Launcher ---

        [Fact]
        public void Launcher_OneFailureDoesNotPreventLaterLaunches()
        {
            var starter = new FakeProcessStarter(@"C:\Apps\a.exe", @"C:\Apps\b.exe");
            var launcher = new ProgramLauncher(starter);

            starter.ThrowOnStart = new InvalidOperationException("first fails");
            var first = launcher.Launch(P("A", path: @"C:\Apps\a.exe"));
            starter.ThrowOnStart = null;
            var second = launcher.Launch(P("B", path: @"C:\Apps\b.exe"));

            Assert.False(first.Success);
            Assert.True(second.Success);
            Assert.Equal(2, starter.Started.Count);
        }

        [Fact]
        public void Launcher_ExeWithoutDirectory_HasEmptyWorkingDirectory()
        {
            var starter = new FakeProcessStarter("tool.exe");

            new ProgramLauncher(starter).Launch(P("T", path: "tool.exe /q"));

            var psi = Assert.Single(starter.Started);
            Assert.Equal("tool.exe", psi.FileName);
            Assert.Equal("/q", psi.Arguments);
            Assert.Equal("", psi.WorkingDirectory);
        }

        [Fact]
        public void Launcher_MissingQuotedExe_ShellStartsRawCommandIncludingQuotes_Current()
        {
            var starter = new FakeProcessStarter();

            var result = new ProgramLauncher(starter).Launch(P("X", path: "\"C:\\No Such\\x.exe\" -y"));

            Assert.True(result.Success);
            Assert.Equal("\"C:\\No Such\\x.exe\" -y", Assert.Single(starter.Started).FileName);
        }

        [Fact]
        public void Launcher_DoesNotExpandEnvironmentVariables_Current() // 3.1 adds expansion
        {
            var starter = new FakeProcessStarter();

            new ProgramLauncher(starter).Launch(P("X", path: @"%LOCALAPPDATA%\x.exe"));

            Assert.Equal(@"%LOCALAPPDATA%\x.exe", Assert.Single(starter.FileExistsCalls));
        }

        // --- Model ---

        [Fact]
        public void Model_Load_CopiesTheInputList()
        {
            var input = List("A", "B");
            var model = new StartupListModel();
            model.Load(input);

            input.RemoveAt(0);
            model.MoveDown(model.Programs[0]);

            Assert.Equal("B,A", Names(model.Programs));
        }

        [Fact]
        public void Model_EmptySnapshot_IsEmpty()
        {
            var snapshot = new StartupListModel().Snapshot();

            Assert.Empty(snapshot.Order);
            Assert.Empty(snapshot.Enabled);
            Assert.Empty(snapshot.EnabledInOrder());
        }
    }
}
