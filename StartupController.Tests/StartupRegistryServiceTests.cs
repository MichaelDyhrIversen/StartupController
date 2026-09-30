using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // All tests run against a RegistrySandbox root (HKCU\Software\StartupController.Tests\{guid}).
    public sealed class StartupRegistryServiceTests : IDisposable
    {
        private readonly RegistrySandbox _sandbox = new RegistrySandbox();
        private readonly StartupRegistryService _service;

        public StartupRegistryServiceTests()
        {
            _service = new StartupRegistryService(_sandbox.Root);
        }

        public void Dispose() => _sandbox.Dispose();

        [Fact]
        public void MissingRunKey_ReturnsEmpty()
        {
            _sandbox.SeedApproved("A", Approved(0x03));

            Assert.Empty(_service.GetStartupPrograms());
        }

        [Fact]
        public void MissingApprovedKey_ReturnsEmpty() // no approved values at all: Windows runs every entry
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");

            Assert.Empty(_service.GetStartupPrograms());
        }

        [Fact]
        public void MixedSeed_ListsOnlyOddFirstByteEntries() // was MixedSeed_ListsOnlyEntriesTreatedAsDisabled_Current
        {
            _sandbox.SeedRun("Disabled03", @"C:\Apps\d.exe");
            _sandbox.SeedRun("Enabled02", @"C:\Apps\e.exe");
            _sandbox.SeedRun("NoApprovedValue", @"C:\Apps\none.exe");
            _sandbox.SeedRun("AllZero", @"C:\Apps\z.exe");
            _sandbox.SeedRun("Even06", @"C:\Apps\s.exe");
            _sandbox.SeedRun("Odd07", @"C:\Apps\o.exe --x");
            _sandbox.SeedApproved("Disabled03", Approved(0x03));
            _sandbox.SeedApproved("Enabled02", Approved(0x02));
            _sandbox.SeedApproved("AllZero", new byte[12]);
            _sandbox.SeedApproved("Even06", Approved(0x06));
            _sandbox.SeedApproved("Odd07", Approved(0x07));

            var result = _service.GetStartupPrograms();

            Assert.Equal(
                new[] { "Disabled03", "Odd07" },
                result.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.All(result, p => Assert.False(p.Enabled));
            Assert.Equal(@"C:\Apps\o.exe --x", result.Single(p => p.Name == "Odd07").Path);
        }

        [Theory]
        [InlineData("StartupController", false)]
        [InlineData("StartupController", true)]
        [InlineData("startupcontroller", true)]
        public void OwnRunEntry_IsNeverListed(string name, bool disabledInWindows) // was OwnRunEntryWithoutApprovedValue_IsListed_Current (#1)
        {
            _sandbox.SeedRun(name, "\"C:\\Program Files\\StartupController\\StartupController.exe\" --launch");
            _sandbox.CreateApprovedKey();
            if (disabledInWindows)
                _sandbox.SeedApproved(name, Approved(0x03));
            _sandbox.SeedStoredOrder(new[] { name }, new[] { name });

            Assert.Empty(_service.GetStartupPrograms());
        }

        [Fact]
        public void EntryWithoutApprovedValue_IsNotListed_EvenWhenStoredAndEnabled()
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedRun("B", @"C:\Apps\B.exe");
            _sandbox.SeedApproved("A", Approved(0x03));
            _sandbox.SeedStoredOrder(new[] { "A", "B" }, new[] { "A", "B" });

            Assert.Equal("A", Assert.Single(_service.GetStartupPrograms()).Name);
        }

        [Fact]
        public void SavedOrder_IsAppliedFromSandbox() // legacy StartupOrder, migrated in memory
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedRun("B", @"C:\Apps\B.exe");
            _sandbox.SeedRun("C", @"C:\Apps\C.exe");
            _sandbox.SeedApproved("A", Approved(0x03));
            _sandbox.SeedApproved("B", Approved(0x03));
            _sandbox.SeedApproved("C", Approved(0x03));
            _sandbox.SeedOrder("C", "A");

            var result = _service.GetStartupPrograms();

            Assert.Equal(new[] { "C", "A", "B" }, result.Select(p => p.Name));
            Assert.Equal(new[] { true, true, false }, result.Select(p => p.Enabled));
        }

        [Fact]
        public void SavedOrderWithRemovedEntry_DoesNotThrow() // was SavedOrderWithRemovedEntry_Throws_Current (#2)
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedApproved("A", Approved(0x03));
            _sandbox.SeedOrder("A", "Removed");

            var result = Assert.Single(_service.GetStartupPrograms());
            Assert.Equal("A", result.Name);
            Assert.True(result.Enabled);
        }

        [Fact]
        public void SaveStartupOrder_WritesMultiSzValues_AndNoLegacyValue() // was SaveStartupOrder_WritesSemicolonJoinedString
        {
            _service.SaveStartupOrder(StoredOrder.Create(new[] { "A", "B", "C" }, new[] { "C", "A" }));

            Assert.Equal(new[] { "A", "B", "C" }, (string[])_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.ProgramOrderValue)!);
            Assert.Equal(new[] { "A", "C" }, (string[])_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.EnabledProgramsValue)!);
            Assert.Equal(RegistryValueKind.MultiString, _sandbox.ReadKind(RegistrySandbox.AppPath, RegistrySandbox.ProgramOrderValue));
            Assert.Equal(RegistryValueKind.MultiString, _sandbox.ReadKind(RegistrySandbox.AppPath, RegistrySandbox.EnabledProgramsValue));
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));

            var loaded = _service.LoadStoredOrder();
            Assert.Equal(new[] { "A", "B", "C" }, loaded.Order);
            Assert.Equal(new[] { "A", "C" }, loaded.EnabledInOrder());
        }

        [Fact]
        public void NameContainingSemicolon_RoundTrips() // was NameContainingSemicolon_DoesNotRoundTrip_Current (#13)
        {
            _service.SaveStartupOrder(StoredOrder.Create(new[] { "A;B", "C" }, new[] { "A;B" }));

            var loaded = _service.LoadStoredOrder();
            Assert.Equal(new[] { "A;B", "C" }, loaded.Order);
            Assert.Equal(new[] { "A;B" }, loaded.EnabledInOrder());
        }

        [Fact]
        public void LoadStoredOrder_NoAppKey_ReturnsEmpty()
        {
            Assert.Empty(_service.LoadStoredOrder().Order);
        }

        [Fact]
        public void LegacyOrder_DropsEmptySegments() // was LoadStartupOrder_KeepsEmptySegments_Current
        {
            _sandbox.SeedAppValue(RegistrySandbox.OrderValue, "A;;B;", RegistryValueKind.String);

            var loaded = _service.LoadStoredOrder();

            Assert.Equal(new[] { "A", "B" }, loaded.Order);
            Assert.Equal(new[] { "A", "B" }, loaded.EnabledInOrder());
        }

        [Fact]
        public void AddThisApplication_WritesQuotedPathWithLaunchArg()
        {
            _sandbox.CreateRunKey();

            _service.AddThisApplicationToStartup(@"C:\Program Files\StartupController\StartupController.exe");

            Assert.Equal(
                "\"C:\\Program Files\\StartupController\\StartupController.exe\" --launch",
                _sandbox.ReadValue(RegistrySandbox.RunPath, "StartupController"));
        }

        [Fact]
        public void AddThisApplication_MissingRunKey_DoesNothing_Current() // pinned (#16), 3.3 creates the key
        {
            _service.AddThisApplicationToStartup(@"C:\x\app.exe");

            Assert.False(_sandbox.KeyExists(RegistrySandbox.RunPath));
        }

        [Fact]
        public void RemoveThisApplication_RemovesValue_AndToleratesMissing()
        {
            _sandbox.SeedRun("StartupController", "\"C:\\x\\app.exe\" --launch");

            _service.RemoveThisApplicationFromStartup();
            _service.RemoveThisApplicationFromStartup();

            Assert.Null(_sandbox.ReadValue(RegistrySandbox.RunPath, "StartupController"));
        }
    }
}
