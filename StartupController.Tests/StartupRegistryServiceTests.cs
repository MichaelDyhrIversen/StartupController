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
        public void MissingApprovedKey_ReturnsEmpty()
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");

            Assert.Empty(_service.GetStartupPrograms());
        }

        [Fact]
        public void MixedSeed_ListsOnlyEntriesTreatedAsDisabled_Current() // 2.1 drops the no-value and 0x06 entries
        {
            _sandbox.SeedRun("Disabled03", @"C:\Apps\d.exe");
            _sandbox.SeedRun("Enabled02", @"C:\Apps\e.exe");
            _sandbox.SeedRun("NoApprovedValue", @"C:\Apps\n.exe");
            _sandbox.SeedRun("AllZero", @"C:\Apps\z.exe");
            _sandbox.SeedRun("Even06", @"C:\Apps\s.exe --x");
            _sandbox.SeedApproved("Disabled03", Approved(0x03));
            _sandbox.SeedApproved("Enabled02", Approved(0x02));
            _sandbox.SeedApproved("AllZero", new byte[12]);
            _sandbox.SeedApproved("Even06", Approved(0x06));

            var result = _service.GetStartupPrograms();

            Assert.Equal(
                new[] { "Disabled03", "Even06", "NoApprovedValue" },
                result.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.All(result, p => Assert.False(p.Enabled));
            Assert.Equal(@"C:\Apps\s.exe --x", result.Single(p => p.Name == "Even06").Path);
        }

        [Fact]
        public void OwnRunEntryWithoutApprovedValue_IsListed_Current() // pinned bug (#1), flipped in 2.1
        {
            _sandbox.SeedRun("StartupController", "\"C:\\Program Files\\StartupController\\StartupController.exe\" --launch");
            _sandbox.CreateApprovedKey();

            var result = _service.GetStartupPrograms();

            Assert.Equal("StartupController", Assert.Single(result).Name);
        }

        [Fact]
        public void SavedOrder_IsAppliedFromSandbox()
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
        public void SavedOrderWithRemovedEntry_Throws_Current() // pinned bug (#2), flipped in 2.2
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedApproved("A", Approved(0x03));
            _sandbox.SeedOrder("A", "Removed");

            Assert.Throws<ArgumentOutOfRangeException>(() => _service.GetStartupPrograms());
        }

        [Fact]
        public void SaveStartupOrder_WritesSemicolonJoinedString()
        {
            _service.SaveStartupOrder(new List<string> { "A", "B" });

            Assert.Equal("A;B", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
            Assert.Equal(RegistryValueKind.String, _sandbox.ReadKind(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
            Assert.Equal(new[] { "A", "B" }, _service.LoadStartupOrder());
        }

        [Fact]
        public void NameContainingSemicolon_DoesNotRoundTrip_Current() // pinned (#13), flipped in 2.2
        {
            _service.SaveStartupOrder(new List<string> { "A;B" });

            Assert.Equal(new[] { "A", "B" }, _service.LoadStartupOrder());
        }

        [Fact]
        public void LoadStartupOrder_NoAppKey_ReturnsEmpty()
        {
            Assert.Empty(_service.LoadStartupOrder());
        }

        [Fact]
        public void LoadStartupOrder_KeepsEmptySegments_Current() // 2.2 drops empty segments
        {
            _sandbox.SeedOrder("A", "", "B", "");

            Assert.Equal(new[] { "A", "", "B", "" }, _service.LoadStartupOrder());
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
