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

        // --- OrderMerger (was ApplyCustomOrder) ---

        [Fact]
        public void Merge_ReturnsNewInstances_AndLeavesInputUntouched() // was ApplyCustomOrder_EmptyOrder_ReturnsSameListInstance
        {
            var input = List("B", "A");

            var result = OrderMerger.Merge(input, StoredOrder.Create(new[] { "A" }, new[] { "A" }));

            Assert.NotSame(input, result);
            Assert.DoesNotContain(result, p => input.Contains(p));
            Assert.All(input, p => Assert.False(p.Enabled));
            Assert.Equal("B,A", Names(input));
        }

        [Fact]
        public void Merge_DuplicateStoredNames_FirstWins_AndEnablesByName() // was ApplyCustomOrder_DuplicateNamesInOrder_EnableByPosition_Current
        {
            var result = OrderMerger.Merge(List("A", "B", "C"), StoredOrder.Create(new[] { "A", "A", "B" }, new[] { "A" }));

            Assert.Equal("A,B,C", Names(result));
            Assert.Equal("A", EnabledNames(result)); // B is stored but not enabled
        }

        [Fact]
        public void LegacyEmptySegments_DoNotEnableExtraPrograms() // was ApplyCustomOrder_EmptySegmentInOrder_EnablesAnExtraProgram_Current
        {
            SeedDisabled("A", "B", "C");
            _sandbox.SeedAppValue(RegistrySandbox.OrderValue, "A;;B", RegistryValueKind.String);

            var result = new StartupRegistryService(_sandbox.Root).GetStartupPrograms();

            Assert.Equal("A,B,C", Names(result));
            Assert.Equal("A,B", EnabledNames(result));
        }

        [Fact]
        public void Merge_IsStableForUnlistedEntries() // was ApplyCustomOrder_IsStableForUnlistedEntries_AndFlagsInputObjects
        {
            var input = List("D", "C", "B", "A");

            var result = OrderMerger.Merge(input, StoredOrder.Create(new[] { "B" }, new[] { "B" }));

            Assert.Equal("B,D,C,A", Names(result));
            Assert.Equal("B", EnabledNames(result));
            Assert.False(input.Single(p => p.Name == "B").Enabled); // inputs are not flagged any more
        }

        // --- Service via sandbox ---

        [Fact]
        public void ApprovedValueOfWrongKind_IsTreatedAsMissing_AndNotListed() // was ..._AndListed_Current: missing = Windows runs it
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.CreateApprovedKey();
            using (var key = _sandbox.Root.CreateSubKey(RegistrySandbox.ApprovedPath, writable: true)!)
                key.SetValue("A", "03", RegistryValueKind.String);

            Assert.Empty(new StartupRegistryService(_sandbox.Root).GetStartupPrograms());
        }

        [Fact]
        public void EmptyApprovedArray_IsNotListed() // was EmptyApprovedArray_IsListed_Current: empty binary = enabled
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedApproved("A", Array.Empty<byte>());

            Assert.Empty(new StartupRegistryService(_sandbox.Root).GetStartupPrograms());
        }

        private void SeedDisabled(params string[] names)
        {
            foreach (var name in names)
            {
                _sandbox.SeedRun(name, $@"C:\Apps\{name}.exe");
                _sandbox.SeedApproved(name, Approved(0x03));
            }
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
            service.SaveStartupOrder(StoredOrder.Create(listed.Select(p => p.Name), listed.Select(p => p.Name)));
            service.GetStartupPrograms();

            Assert.Equal(Approved(0x03), (byte[])_sandbox.ReadValue(RegistrySandbox.ApprovedPath, "A")!);
            Assert.Equal(Approved(0x02), (byte[])_sandbox.ReadValue(RegistrySandbox.ApprovedPath, "B")!);
            Assert.Equal(@"C:\Apps\A.exe", _sandbox.ReadValue(RegistrySandbox.RunPath, "A"));
            Assert.Equal(@"C:\Apps\B.exe", _sandbox.ReadValue(RegistrySandbox.RunPath, "B"));
        }

        [Fact]
        public void SaveStartupOrder_Empty_WritesEmptyMultiSz_AndLoadsEmpty() // was SaveStartupOrder_EmptyList_WritesEmptyString_AndLoadsEmpty
        {
            var service = new StartupRegistryService(_sandbox.Root);

            service.SaveStartupOrder(StoredOrder.Empty);

            Assert.Equal(Array.Empty<string>(), (string[])_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.ProgramOrderValue)!);
            Assert.Equal(Array.Empty<string>(), (string[])_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.EnabledProgramsValue)!);
            Assert.Empty(service.LoadStoredOrder().Order);
        }

        [Fact]
        public void SaveStartupOrder_EmptyDisplayed_KeepsPreviouslyStoredNames() // D4: nothing displayed is not "forget everything"
        {
            var service = new StartupRegistryService(_sandbox.Root);
            service.SaveStartupOrder(StoredOrder.Create(new[] { "A" }, new[] { "A" }));

            service.SaveStartupOrder(StoredOrder.Empty);

            var loaded = service.LoadStoredOrder();
            Assert.Equal(new[] { "A" }, loaded.Order);
            Assert.Equal(new[] { "A" }, loaded.EnabledInOrder());
        }

        [Fact]
        public void SaveStartupOrder_DoesNotTouchSettingsValues()
        {
            var settings = new UserSettings(_sandbox.Root);
            settings.SetStartToTray(true);

            new StartupRegistryService(_sandbox.Root).SaveStartupOrder(StoredOrder.Create(new[] { "A" }, new[] { "A" }));

            Assert.Equal(1, _sandbox.ReadValue(RegistrySandbox.AppPath, "StartToTray"));
            Assert.Equal(new[] { "A" }, (string[])_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.ProgramOrderValue)!);
        }

        [Fact]
        public void LoadStoredOrder_LegacyNonStringValue_ReturnsEmpty() // was LoadStartupOrder_NonStringValue_ReturnsEmpty
        {
            _sandbox.SeedAppValue(RegistrySandbox.OrderValue, 5, RegistryValueKind.DWord);

            Assert.Empty(new StartupRegistryService(_sandbox.Root).LoadStoredOrder().Order);
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
            Assert.ThrowsAny<Exception>(() => service.SaveStartupOrder(StoredOrder.Create(new[] { "A" }, new[] { "A" })));
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.ProgramOrderValue));
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.EnabledProgramsValue));
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
        public void Launcher_BareExe_WorkingDirectoryIsTheSystemDirectory() // was Launcher_ExeWithoutDirectory_HasEmptyWorkingDirectory (L-A)
        {
            var starter = new FakeProcessStarter("tool.exe");

            new ProgramLauncher(starter).Launch(P("T", path: "tool.exe /q"));

            var psi = Assert.Single(starter.Started);
            Assert.Equal("tool.exe", psi.FileName);
            Assert.Equal("/q", psi.Arguments);
            Assert.Equal(Environment.SystemDirectory, psi.WorkingDirectory);
        }

        [Fact]
        public void Launcher_MissingQuotedExe_IsNotFound_AndNothingIsShellStarted() // was ..._ShellStartsRawCommandIncludingQuotes_Current
        {
            var starter = new FakeProcessStarter();

            var result = new ProgramLauncher(starter).Launch(P("X", path: "\"C:\\No Such\\x.exe\" -y"));

            Assert.False(result.Success);
            Assert.True(result.NotFound);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void Launcher_ExpandsEnvironmentVariables() // was Launcher_DoesNotExpandEnvironmentVariables_Current
        {
            var expanded = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\x.exe");
            var starter = new FakeProcessStarter(expanded);

            new ProgramLauncher(starter).Launch(P("X", path: @"%LOCALAPPDATA%\x.exe"));

            Assert.Equal(expanded, Assert.Single(starter.Started).FileName);
            Assert.DoesNotContain(starter.FileExistsCalls, c => c.Contains('%'));
        }

        [Fact]
        public void Launcher_ExpandStringPath_IsNotExpandedTwice()
        {
            // The Path of a REG_EXPAND_SZ value was expanded when read; text that came out of a variable stays literal
            var starter = new FakeProcessStarter(@"C:\Apps\%LOCALAPPDATA%\x.exe");
            var program = P("X", path: @"C:\Apps\%LOCALAPPDATA%\x.exe");
            program.PathExpanded = true;

            var result = new ProgramLauncher(starter).Launch(program);

            Assert.True(result.Success);
            Assert.Equal(@"C:\Apps\%LOCALAPPDATA%\x.exe", Assert.Single(starter.Started).FileName);
        }

        [Fact]
        public void Registry_ExpandStringValue_IsMarkedExpanded_StringValueIsNot()
        {
            _sandbox.SeedRunValue("E", @"%LOCALAPPDATA%\e.exe", RegistryValueKind.ExpandString);
            _sandbox.SeedApproved("E", Approved(0x03));
            _sandbox.SeedRunValue("S", @"%LOCALAPPDATA%\s.exe", RegistryValueKind.String);
            _sandbox.SeedApproved("S", Approved(0x03));

            var programs = new StartupRegistryService(_sandbox.Root).GetStartupPrograms();

            Assert.True(programs.Single(p => p.Name == "E").PathExpanded);
            Assert.False(programs.Single(p => p.Name == "S").PathExpanded);
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
