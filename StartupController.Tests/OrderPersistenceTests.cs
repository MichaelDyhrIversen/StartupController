using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // 2.2 order persistence v2 (ProgramOrder / EnabledPrograms), legacy migration (D3) and hidden names (D4).
    // All against a RegistrySandbox; launches go through FakeProcessStarter.
    public sealed class OrderPersistenceTests : IDisposable
    {
        private readonly RegistrySandbox _sandbox = new RegistrySandbox();
        private readonly StartupRegistryService _service;

        public OrderPersistenceTests()
        {
            _service = new StartupRegistryService(_sandbox.Root);
        }

        public void Dispose() => _sandbox.Dispose();

        private static string Describe(IEnumerable<StartupProgram> programs) =>
            string.Join(",", programs.Select(p => p.Name + (p.Enabled ? "(on)" : "(off)")));

        private void SeedDisabled(params string[] names)
        {
            foreach (var name in names)
            {
                _sandbox.SeedRun(name, $@"C:\Apps\{name}.exe");
                _sandbox.SeedApproved(name, Approved(0x03));
            }
        }

        private StartupListModel LoadModel()
        {
            var model = new StartupListModel();
            model.Load(_service.GetStartupPrograms());
            return model;
        }

        private string[]? ReadMulti(string name) => _sandbox.ReadValue(RegistrySandbox.AppPath, name) as string[];

        // Launches what --launch mode would launch and returns the file names handed to the (fake) starter
        private List<string> LaunchEnabled(StartupListModel model)
        {
            var starter = FakeProcessStarter.AllExesExist(); // 3.1: a missing path with a directory is not started
            var launcher = new ProgramLauncher(starter);
            foreach (var program in model.EnabledPrograms())
                launcher.Launch(program);
            return starter.Started.Select(s => s.FileName).ToList();
        }

        [Fact]
        public void MiddleDisable_SaveReload_KeepsOrderAndFlags() // regression for #3
        {
            SeedDisabled("A", "B", "C");
            _sandbox.SeedStoredOrder(new[] { "A", "B", "C" }, new[] { "A", "B", "C" });
            var model = LoadModel();

            model.Disable(model.Programs.Single(p => p.Name == "B"));
            _service.SaveStartupOrder(model.Snapshot());

            Assert.Equal("A(on),B(off),C(on)", Describe(_service.GetStartupPrograms()));
        }

        [Fact]
        public void CaseDifference_IsMatched_AndDisplayUsesRegistryCasing()
        {
            SeedDisabled("Spotify");
            _sandbox.SeedStoredOrder(new[] { "spotify" }, new[] { "SPOTIFY" });

            Assert.Equal("Spotify(on)", Describe(_service.GetStartupPrograms()));
        }

        // --- Migration (D3) ---

        [Fact]
        public void Migration_LegacyOnly_LoadsAllEnabled_WritesNothingOnLoad_AndLeavesLegacyUntouchedOnSave()
        {
            SeedDisabled("A", "B", "C");
            _sandbox.SeedOrder("A", "B");

            var loaded = _service.LoadStoredOrder();
            Assert.Equal(new[] { "A", "B" }, loaded.Order);
            Assert.Equal(new[] { "A", "B" }, loaded.EnabledInOrder());

            var model = LoadModel();
            Assert.Equal("A(on),B(on),C(off)", Describe(model.Programs));
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.ProgramOrderValue));
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.EnabledProgramsValue));

            _service.SaveStartupOrder(model.Snapshot());

            Assert.Equal(new[] { "A", "B", "C" }, ReadMulti(RegistrySandbox.ProgramOrderValue));
            Assert.Equal(new[] { "A", "B" }, ReadMulti(RegistrySandbox.EnabledProgramsValue));
            Assert.Equal("A;B", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
            Assert.Equal(RegistryValueKind.String, _sandbox.ReadKind(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
        }

        [Fact]
        public void PostMigration_ProgramOrderWins_AndLegacyIsNeverWrittenOrDeleted()
        {
            SeedDisabled("A", "B", "C");
            _sandbox.SeedOrder("A", "B", "C");
            _sandbox.SeedStoredOrder(new[] { "B", "A" }, new[] { "B" });

            var model = LoadModel();
            Assert.Equal("B(on),A(off),C(off)", Describe(model.Programs));

            model.Enable(model.Programs.Single(p => p.Name == "C"));
            _service.SaveStartupOrder(model.Snapshot());
            _service.SaveStartupOrder(StoredOrder.Empty);

            Assert.Equal("A;B;C", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
            Assert.Equal(RegistryValueKind.String, _sandbox.ReadKind(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
        }

        [Fact]
        public void FailedProgramOrderWrite_NextLoadMigratesAgain_WithoutDataLoss()
        {
            SeedDisabled("A", "B", "C");
            _sandbox.SeedOrder("A", "B");
            var failing = new FailingWriteService(_sandbox.Root, RegistrySandbox.ProgramOrderValue);
            var model = new StartupListModel();
            model.Load(failing.GetStartupPrograms());
            model.Enable(model.Programs.Single(p => p.Name == "C"));

            Assert.Throws<IOException>(() => failing.SaveStartupOrder(model.Snapshot()));

            Assert.NotNull(ReadMulti(RegistrySandbox.EnabledProgramsValue)); // written first
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.ProgramOrderValue));
            Assert.Equal("A(on),B(on),C(off)", Describe(_service.GetStartupPrograms()));
            Assert.Equal("A;B", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
        }

        [Fact]
        public async Task FailedWrite_ThroughCoordinator_MarksDirty_AndNotifies()
        {
            SeedDisabled("A");
            var failing = new FailingWriteService(_sandbox.Root, RegistrySandbox.EnabledProgramsValue);
            var model = new StartupListModel();
            model.Load(failing.GetStartupPrograms());
            var notifier = new FakeNotifier();

            var ok = await new OrderSaveCoordinator(model, failing, notifier).SaveAsync(manual: false);

            Assert.False(ok);
            Assert.True(model.IsDirty);
            Assert.Single(notifier.Messages);
        }

        [Theory]
        [InlineData(RegistryValueKind.DWord)]
        [InlineData(RegistryValueKind.String)]
        public void MalformedProgramOrder_IgnoresLegacy_EnablesNothing(RegistryValueKind kind) // was MalformedProgramOrder_Dword_FallsBackToLegacy (security Low 2)
        {
            SeedDisabled("A", "B");
            _sandbox.SeedOrder("B", "A");
            _sandbox.SeedAppValue(RegistrySandbox.ProgramOrderValue, kind == RegistryValueKind.DWord ? 7 : "B", kind);

            Assert.Equal("A(off),B(off)", Describe(_service.GetStartupPrograms()));
            Assert.Empty(_service.LoadStoredOrder().Order);
        }

        [Fact]
        public void MalformedProgramOrder_SaveDoesNotCarryLegacyNamesForward()
        {
            SeedDisabled("A");
            _sandbox.SeedOrder("A", "Hidden");
            _sandbox.SeedAppValue(RegistrySandbox.ProgramOrderValue, "junk", RegistryValueKind.String);

            _service.SaveStartupOrder(LoadModel().Snapshot());

            Assert.Equal(new[] { "A" }, ReadMulti(RegistrySandbox.ProgramOrderValue));
            Assert.Empty(ReadMulti(RegistrySandbox.EnabledProgramsValue)!);
            Assert.Equal("A;Hidden", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue)); // still untouched
        }

        [Fact]
        public void MalformedProgramOrder_String_WithoutLegacy_LoadsEmpty()
        {
            SeedDisabled("A", "B");
            _sandbox.SeedAppValue(RegistrySandbox.ProgramOrderValue, "B", RegistryValueKind.String);

            Assert.Equal("A(off),B(off)", Describe(_service.GetStartupPrograms()));
        }

        [Fact]
        public void MalformedEnabledPrograms_KeepsOrder_EnablesNothing()
        {
            SeedDisabled("A", "B");
            _sandbox.SeedAppValue(RegistrySandbox.ProgramOrderValue, new[] { "B", "A" }, RegistryValueKind.MultiString);
            _sandbox.SeedAppValue(RegistrySandbox.EnabledProgramsValue, 1, RegistryValueKind.DWord);

            Assert.Equal("B(off),A(off)", Describe(_service.GetStartupPrograms()));
        }

        // --- Hidden names (D4) ---

        [Fact]
        public void HiddenName_KeepsPositionAndFlag_WhenTheListIsReordered()
        {
            SeedDisabled("A", "B");
            _sandbox.SeedStoredOrder(new[] { "A", "X", "B" }, new[] { "A", "X", "B" });
            var model = LoadModel();
            Assert.Equal("A(on),B(on)", Describe(model.Programs));

            model.MoveUp(model.Programs.Single(p => p.Name == "B"));
            _service.SaveStartupOrder(model.Snapshot());

            Assert.Equal(new[] { "B", "A", "X" }, ReadMulti(RegistrySandbox.ProgramOrderValue));
            Assert.Contains("X", ReadMulti(RegistrySandbox.EnabledProgramsValue)!);
        }

        [Fact]
        public void HiddenName_IsListedAgain_WhenItReturnsDisabledInWindows()
        {
            SeedDisabled("A");
            _sandbox.SeedStoredOrder(new[] { "A", "X" }, new[] { "X" });

            var model = LoadModel();
            Assert.Equal("A(off)", Describe(model.Programs));
            Assert.Empty(LaunchEnabled(model));

            SeedDisabled("X");

            Assert.Equal("A(off),X(on)", Describe(_service.GetStartupPrograms()));
        }

        [Theory]
        [InlineData(true)]  // StartupApproved 02: Windows runs it
        [InlineData(false)] // no StartupApproved value: Windows runs it
        public void StoredEnabledName_ThatWindowsRuns_IsNotListed_NorLaunched(bool approved02)
        {
            SeedDisabled("A");
            _sandbox.SeedRun("X", @"C:\Apps\X.exe");
            if (approved02)
                _sandbox.SeedApproved("X", Approved(0x02));
            _sandbox.SeedStoredOrder(new[] { "X", "A" }, new[] { "X", "A" });

            var model = LoadModel();

            Assert.Equal("A(on)", Describe(model.Programs));
            var launched = LaunchEnabled(model);
            Assert.Equal(new[] { @"C:\Apps\A.exe" }, launched);
        }

        [Fact]
        public void EntryRemovedExternally_WhileRunning_IsKeptInStorage_AndReloadDoesNotThrow()
        {
            SeedDisabled("A", "B");
            var model = LoadModel();
            model.Enable(model.Programs.Single(p => p.Name == "A"));
            model.Enable(model.Programs.Single(p => p.Name == "B"));

            _sandbox.DeleteRunValue("B");
            _service.SaveStartupOrder(model.Snapshot());

            Assert.Equal(new[] { "A", "B" }, ReadMulti(RegistrySandbox.ProgramOrderValue));
            Assert.Equal(new[] { "A", "B" }, ReadMulti(RegistrySandbox.EnabledProgramsValue));
            Assert.Equal("A(on)", Describe(_service.GetStartupPrograms()));

            // A later save from the reloaded list still keeps B
            _service.SaveStartupOrder(LoadModel().Snapshot());
            Assert.Equal(new[] { "A", "B" }, ReadMulti(RegistrySandbox.ProgramOrderValue));
        }

        // Simulates a registry write failure for one value
        private sealed class FailingWriteService : StartupRegistryService
        {
            private readonly string _failOn;

            public FailingWriteService(RegistryKey root, string failOn) : base(root)
            {
                _failOn = failOn;
            }

            internal override void WriteMultiString(RegistryKey key, string name, string[] values)
            {
                if (name == _failOn) throw new IOException("simulated write failure: " + name);
                base.WriteMultiString(key, name, values);
            }
        }
    }
}
