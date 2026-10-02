extern alias Uninstall;

using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using Uninstall::StartupController.Uninstall;
using static StartupController.Tests.Infrastructure.Programs;
using HelperProgram = Uninstall::StartupController.Uninstall.Program;

namespace StartupController.Tests
{
    // Review additions for the silent takeover: the write-order invariant (an entry is never disabled in Windows
    // without being stored Enabled and recorded), clock and name edge cases, the record cap, and helper argument edges.
    // Sandbox only, fixed clock, no launches, no real MessageBox.
    public sealed class TakeoverReviewTests : IDisposable
    {
        private static readonly DateTime Clock = new DateTime(2026, 10, 2, 7, 15, 30, DateTimeKind.Utc);

        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        private void SeedOwnEntry(byte[]? approved = null)
        {
            _sandbox.SeedRun("StartupController", "\"C:\\x\\StartupController.exe\" --launch");
            if (approved != null)
                _sandbox.SeedApproved("StartupController", approved);
        }

        private byte[]? ApprovedOf(string name) => _sandbox.ReadValue(RegistrySandbox.ApprovedPath, name) as byte[];

        private string[]? Multi(string name) => _sandbox.ReadValue(RegistrySandbox.AppPath, name) as string[];

        // Records every write in order; can fail chosen values and run a hook right before each StartupApproved write
        private sealed class SpyService : StartupRegistryService
        {
            public SpyService(RegistryKey root, Func<DateTime>? clock = null) : base(root, clock ?? (() => Clock), IsTestApp) { }

            public List<string> Writes { get; } = new List<string>();

            public HashSet<string> FailValues { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Action<string>? BeforeApproved { get; set; }

            internal override void WriteMultiString(RegistryKey key, string name, string[] values)
            {
                if (FailValues.Contains(name)) throw new UnauthorizedAccessException("denied " + name);
                Writes.Add(name);
                base.WriteMultiString(key, name, values);
            }

            internal override void WriteApprovedDisabled(RegistryKey approvedKey, string name, byte[] value)
            {
                BeforeApproved?.Invoke(name);
                Writes.Add("approved:" + name);
                base.WriteApprovedDisabled(approvedKey, name, value);
            }
        }

        // ---------- write order: order -> record -> StartupApproved ----------

        [Fact]
        public void WriteOrder_IsEnabledPrograms_Fingerprints_ProgramOrder_Record_ThenApproved()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N1", @"C:\Apps\N1.exe");
            _sandbox.SeedRun("N2", @"C:\Apps\N2.exe");
            var service = new SpyService(_sandbox.Root);

            service.TakeOverWindowsEntries(true);

            Assert.Equal(new[]
            {
                RegistrySandbox.EnabledProgramsValue, RegistrySandbox.EnabledFingerprintsValue, RegistrySandbox.ProgramOrderValue,
                RegistrySandbox.TakenOverValue, "approved:N1", "approved:N2"
            }, service.Writes);
        }

        [Fact]
        public void BeforeEveryApprovedWrite_TheNameIsAlreadyStoredEnabled_WithFingerprint_AndRecorded()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N1", @"C:\Apps\N1.exe");
            _sandbox.SeedRun("N2", @"C:\Apps\N2.exe");
            var service = new SpyService(_sandbox.Root);
            var checkedNames = new List<string>();
            service.BeforeApproved = name =>
            {
                Assert.Contains(name, Multi(RegistrySandbox.EnabledProgramsValue)!);
                Assert.Contains(name, Multi(RegistrySandbox.ProgramOrderValue)!);
                Assert.Contains(Multi(RegistrySandbox.EnabledFingerprintsValue)!, l => l.StartsWith(name + "|", StringComparison.Ordinal));
                Assert.Contains(name, Multi(RegistrySandbox.TakenOverValue)!);
                checkedNames.Add(name);
            };

            service.TakeOverWindowsEntries(true);

            Assert.Equal(new[] { "N1", "N2" }, checkedNames);
        }

        [Theory]
        [InlineData("EnabledPrograms")]
        [InlineData("EnabledFingerprints")]
        [InlineData("ProgramOrder")]
        [InlineData("TakenOverPrograms")]
        public void AnyStoreWriteFailing_NeverLeavesAnEntryDisabledInWindows(string failing)
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = new SpyService(_sandbox.Root);
            service.FailValues.Add(failing);

            Assert.Empty(service.TakeOverWindowsEntries(true));

            Assert.Null(ApprovedOf("N"));
            Assert.DoesNotContain(service.Writes, w => w.StartsWith("approved:", StringComparison.Ordinal));
        }

        [Fact]
        public void StaleUiSave_BetweenTheOrderWriteAndTheApprovedWrite_KeepsTheEntryEnabled()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = new SpyService(_sandbox.Root);
            var staleModel = new StartupListModel();
            staleModel.Load(service.GetStartupPrograms()); // loaded before the takeover: N is not displayed
            service.BeforeApproved = _ => service.SaveStartupOrder(staleModel.Snapshot());

            Assert.Equal(new[] { "N" }, service.TakeOverWindowsEntries(true));

            Assert.Contains("N", Multi(RegistrySandbox.EnabledProgramsValue)!);
            Assert.Contains(Multi(RegistrySandbox.EnabledFingerprintsValue)!, l => l.StartsWith("N|", StringComparison.Ordinal));
            var model = new StartupListModel();
            model.Load(service.GetStartupPrograms());
            Assert.True(model.Programs.Single().Enabled);
            Assert.False(model.Programs.Single().Changed);
        }

        // Race (review report, fixed): a UI save that lands between the takeover's EnabledPrograms write and its
        // ProgramOrder write reads a ProgramOrder without N, so it rewrites EnabledPrograms without N. The takeover
        // re-reads the stored order right before the StartupApproved writes and skips N, so N is never disabled in
        // Windows without being enabled in the app.
        [Fact]
        public void UiSave_BetweenTheTakeoversStoreWrites_DoesNotDropTheEntry()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = new RaceService(_sandbox.Root);
            var stale = new StartupListModel();
            stale.Load(service.GetStartupPrograms());
            service.AfterWrite = name =>
            {
                if (name != RegistrySandbox.EnabledProgramsValue) return;
                service.AfterWrite = null; // the save below writes through the same seam
                service.SaveStartupOrder(stale.Snapshot());
            };

            service.TakeOverWindowsEntries(true);

            var disabledInWindows = ApprovedOf("N") is { } b && (b[0] & 1) == 1;
            var enabled = Multi(RegistrySandbox.EnabledProgramsValue) ?? Array.Empty<string>();
            Assert.True(!disabledInWindows || enabled.Contains("N"), "N is disabled in Windows but not enabled in the app");
        }

        private sealed class RaceService : StartupRegistryService
        {
            public RaceService(RegistryKey root) : base(root, () => Clock, IsTestApp) { }

            public Action<string>? AfterWrite { get; set; }

            internal override void WriteMultiString(RegistryKey key, string name, string[] values)
            {
                base.WriteMultiString(key, name, values);
                AfterWrite?.Invoke(name);
            }
        }

        // ---------- clock ----------

        [Fact]
        public void ClockBefore1601_IsLogged_AndNothingIsWritten()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var appBefore = _sandbox.Dump(RegistrySandbox.AppPath);
            var approvedBefore = _sandbox.Dump(RegistrySandbox.ApprovedPath);
            var service = new SpyService(_sandbox.Root, () => new DateTime(1600, 12, 31, 0, 0, 0, DateTimeKind.Utc));

            Assert.Empty(service.TakeOverWindowsEntries(true));

            Assert.Empty(service.Writes);
            Assert.NotEmpty(TestLog.LinesContaining("Takeover skipped: the system clock is before 1601"));
            Assert.Equal(appBefore, _sandbox.Dump(RegistrySandbox.AppPath));
            Assert.Equal(approvedBefore, _sandbox.Dump(RegistrySandbox.ApprovedPath));
        }

        [Fact]
        public async Task ClockBefore1601_InTheSession_StillLoadsTheList_AndLogsAnError()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            _sandbox.SeedRun("D", @"C:\Apps\D.exe");
            _sandbox.SeedApproved("D", Approved(0x03));
            var service = new StartupRegistryService(_sandbox.Root, () => DateTime.MinValue, IsTestApp);
            var model = new StartupListModel();

            var loaded = await StartupSession.LoadProgramsAsync(service, model, new FakeNotifier(), takeOver: true,
                read => Task.FromResult(read()));

            Assert.True(loaded);
            Assert.Equal(new[] { "D" }, model.Programs.Select(p => p.Name));
            Assert.Null(ApprovedOf("N"));
        }

        // ---------- names ----------

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("   \t")]
        public void EmptyOrWhitespaceRunValueName_IsNeverTakenOver(string name)
        {
            SeedOwnEntry();
            _sandbox.SeedRun(name, @"C:\Apps\Odd.exe");
            var appBefore = _sandbox.Dump(RegistrySandbox.AppPath);
            var service = new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp);

            Assert.Empty(service.TakeOverWindowsEntries(true));

            Assert.Null(ApprovedOf(name));
            Assert.Equal(appBefore, _sandbox.Dump(RegistrySandbox.AppPath));
            Assert.Empty(service.GetStartupPrograms());
        }

        [Fact]
        public void EmptyName_DoesNotBlockTheOthers()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("", @"C:\Apps\Odd.exe");
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");

            var taken = new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp).TakeOverWindowsEntries(true);

            Assert.Equal(new[] { "N" }, taken);
            Assert.Equal(new[] { "N" }, Multi(RegistrySandbox.TakenOverValue));
        }

        // ---------- own entry ----------

        [Fact]
        public void OwnEntry_IsByteIdentical_AndNotStoredOrRecorded_WhileOthersAreTakenOver()
        {
            var ownApproved = Approved(0x02, 9);
            SeedOwnEntry(ownApproved);
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var runBefore = _sandbox.Dump(RegistrySandbox.RunPath).Where(l => l.Contains("StartupController", StringComparison.Ordinal)).ToList();

            new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp).TakeOverWindowsEntries(true);

            Assert.Equal(ownApproved, ApprovedOf("StartupController"));
            Assert.Equal(runBefore, _sandbox.Dump(RegistrySandbox.RunPath).Where(l => l.Contains("StartupController", StringComparison.Ordinal)).ToList());
            Assert.DoesNotContain("StartupController", Multi(RegistrySandbox.ProgramOrderValue)!, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("StartupController", Multi(RegistrySandbox.EnabledProgramsValue)!, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("StartupController", Multi(RegistrySandbox.TakenOverValue)!, StringComparer.OrdinalIgnoreCase);
        }

        // ---------- caps ----------

        [Fact]
        public void RecordAtMaxNames_NewEntryIsNotTakenOver_ButARecordedOneStillIs()
        {
            SeedOwnEntry();
            var names = Enumerable.Range(0, StartupRegistryService.MAX_NAMES).Select(i => "Old" + i).ToArray();
            _sandbox.SeedAppValue(RegistrySandbox.TakenOverValue, names, RegistryValueKind.MultiString);
            _sandbox.SeedRun("Fresh", @"C:\Apps\Fresh.exe");
            _sandbox.SeedRun("Old7", @"C:\Apps\Old7.exe");
            var service = new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp);

            var taken = service.TakeOverWindowsEntries(true);

            Assert.Equal(new[] { "Old7" }, taken);
            Assert.Null(ApprovedOf("Fresh")); // cannot be recorded, so Windows keeps starting it
            Assert.Equal(StartupRegistryService.MAX_NAMES, Multi(RegistrySandbox.TakenOverValue)!.Length);
            Assert.DoesNotContain("Fresh", Multi(RegistrySandbox.TakenOverValue)!);
            Assert.NotEmpty(TestLog.LinesContaining("already holds"));
        }

        [Fact]
        public void FullOrder_HiddenStoredName_IsStillTakenOver_KeepingItsPosition()
        {
            SeedOwnEntry();
            var order = Enumerable.Range(0, StartupRegistryService.MAX_NAMES).Select(i => "S" + i).ToArray();
            _sandbox.SeedStoredOrder(order, Array.Empty<string>());
            _sandbox.SeedRun("S500", @"C:\Apps\S500.exe");
            _sandbox.SeedRun("Brand", @"C:\Apps\Brand.exe");
            var service = new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp);

            var taken = service.TakeOverWindowsEntries(true);

            Assert.Equal(new[] { "S500" }, taken);
            Assert.Null(ApprovedOf("Brand"));
            Assert.Equal(order, Multi(RegistrySandbox.ProgramOrderValue));
            Assert.Equal(new[] { "S500" }, Multi(RegistrySandbox.EnabledProgramsValue));
        }

        // ---------- helper arguments ----------

        // Review fix 19: a switch given more than once is unparsable (null), so the silent default decides
        [Fact]
        public void Argument_GivenTwice_IsUnparsable()
        {
            var args = new[] { "--choice", "1", "--uilevel", "5", "--choice", "0" };

            Assert.Null(HelperProgram.Argument(args, "--choice"));
            Assert.Equal("5", HelperProgram.Argument(args, "--uilevel"));
        }

        [Theory]
        [InlineData("--uilevel", "5", "--choice")]
        [InlineData("--choice")]
        public void Argument_SwitchWithoutValue_IsNull(params string[] args)
        {
            Assert.Null(HelperProgram.Argument(args, "--choice"));
        }

        [Theory]
        [InlineData("67", "Return")] // UILevel carries flag bits above the level; anything but exactly 3-5 is silent
        [InlineData("1", "Return")]
        [InlineData("6", "Return")]
        [InlineData("0", "Return")]
        [InlineData("-5", "Return")]
        [InlineData("99999999999", "Return")]
        [InlineData(" 4 ", "Prompt")] // round 2 (N7): exactly 3, 4 or 5 prompts
        [InlineData(" 5 ", "Prompt")]
        public void Decide_UiLevelEdges(string uiLevel, string expected)
        {
            var log = new List<string>();

            Assert.Equal(Enum.Parse<UninstallChoice>(expected), UninstallDecision.Decide(uiLevel, "", new Capture(log)));
        }

        private sealed class Capture : IUninstallLog
        {
            private readonly List<string> _lines;

            public Capture(List<string> lines) => _lines = lines;

            public void Info(string text) => _lines.Add(text);
            public void Warning(string text) => _lines.Add(text);
            public void Error(string text) => _lines.Add(text);
        }
    }
}
