using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // Silent takeover (docs/plans/2026-10-02-silent-takeover.md): Windows-run HKCU Run entries are disabled in
    // Windows, stored Enabled and recorded in TakenOverPrograms. Sandbox only, fixed clock, no launches.
    public sealed class TakeoverTests : IDisposable
    {
        private static readonly DateTime Clock = new DateTime(2026, 10, 2, 7, 15, 30, DateTimeKind.Utc);

        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        private StartupRegistryService Service() => new StartupRegistryService(_sandbox.Root, () => Clock, IsTestApp);

        private static byte[] DisabledBytes => StartupApprovedState.Disabled(Clock);

        // The app's own entry, enabled in Windows: the takeover gate is open
        private void SeedOwnEntry(byte[]? approved = null)
        {
            _sandbox.SeedRun("StartupController", "\"C:\\x\\StartupController.exe\" --launch");
            if (approved != null)
                _sandbox.SeedApproved("StartupController", approved);
        }

        private void SeedDisabled(string name, string? command = null)
        {
            _sandbox.SeedRun(name, command ?? $@"C:\Apps\{name}.exe");
            _sandbox.SeedApproved(name, Approved(0x03));
        }

        private byte[]? ApprovedOf(string name) => _sandbox.ReadValue(RegistrySandbox.ApprovedPath, name) as byte[];

        private string[]? Multi(string name) => _sandbox.ReadValue(RegistrySandbox.AppPath, name) as string[];

        private static string Fp(string raw, RegistryValueKind kind = RegistryValueKind.String) => RunFingerprint.Compute(kind, raw);

        private static string Describe(IEnumerable<StartupProgram> programs) =>
            string.Join(",", programs.Select(p => p.Name + (p.Changed ? "(changed)" : p.Enabled ? "(on)" : "(off)")));

        private List<string> AppValues() => _sandbox.Dump(RegistrySandbox.AppPath);

        // ---------- what is taken over ----------

        public static TheoryData<string> WindowsRunsIt => new TheoryData<string>
        {
            "none", "empty", "02+8zero", "allzero12", "06", "single02", "REG_SZ", "REG_DWORD"
        };

        private void SeedApprovedVariant(string name, string variant)
        {
            switch (variant)
            {
                case "none": break;
                case "empty": _sandbox.SeedApproved(name, Array.Empty<byte>()); break;
                case "02+8zero": _sandbox.SeedApproved(name, Approved(0x02, 9)); break;
                case "allzero12": _sandbox.SeedApproved(name, new byte[12]); break;
                case "06": _sandbox.SeedApproved(name, Approved(0x06)); break;
                case "single02": _sandbox.SeedApproved(name, new byte[] { 0x02 }); break;
                case "REG_SZ": _sandbox.SeedApprovedValue(name, "03", RegistryValueKind.String); break;
                case "REG_DWORD": _sandbox.SeedApprovedValue(name, 3, RegistryValueKind.DWord); break;
                default: throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        [Theory]
        [MemberData(nameof(WindowsRunsIt))]
        public void WindowsRunEntry_IsTakenOver_ListedEnabledLast_WithItsFingerprint(string variant)
        {
            SeedOwnEntry();
            SeedDisabled("A");
            _sandbox.SeedStoredOrder(new[] { "A" }, Array.Empty<string>());
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            SeedApprovedVariant("N", variant);
            var service = Service();

            var taken = service.TakeOverWindowsEntries(true);

            Assert.Equal(new[] { "N" }, taken);
            Assert.True(taken.Contains("n"), "the result set is case-insensitive");
            Assert.Equal(DisabledBytes, ApprovedOf("N"));
            Assert.Equal(RegistryValueKind.Binary, _sandbox.ReadKind(RegistrySandbox.ApprovedPath, "N"));
            Assert.Equal("A(off),N(on)", Describe(service.GetStartupPrograms()));
            Assert.Contains("N|" + Fp(@"C:\Apps\N.exe"), Multi(RegistrySandbox.EnabledFingerprintsValue)!);
            Assert.Equal(new[] { "N" }, Multi(RegistrySandbox.TakenOverValue));
        }

        [Fact]
        public void DisabledBytes_AreTaskManagersFormat()
        {
            var bytes = StartupApprovedState.Disabled(Clock);

            Assert.Equal(12, bytes.Length);
            Assert.Equal(new byte[] { 0x03, 0, 0, 0 }, bytes.Take(4));
            Assert.Equal(Clock.ToFileTimeUtc(), BitConverter.ToInt64(bytes, 4));
            Assert.Equal(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, StartupApprovedState.Enabled());
        }

        [Fact]
        public void DisabledBytes_ConvertALocalTime_AndTakeUnspecifiedAsUtc()
        {
            var local = Clock.ToLocalTime();
            var unspecified = DateTime.SpecifyKind(Clock, DateTimeKind.Unspecified);

            Assert.Equal(StartupApprovedState.Disabled(Clock), StartupApprovedState.Disabled(local));
            Assert.Equal(StartupApprovedState.Disabled(Clock), StartupApprovedState.Disabled(unspecified));
        }

        [Theory]
        [InlineData("A\0B")]
        [InlineData("\0")]
        [InlineData("Trailing\0")]
        public void NameWithNul_IsNotStorable(string name)
        {
            Assert.False(TakeoverRecord.IsStorable(name));
            Assert.True(TakeoverRecord.IsStorable("Fine"));
        }

        [Fact]
        public void GetStartupPrograms_SkipsNamesWithANul()
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "StartupRegistryService.cs"));
            var listing = code.Substring(code.IndexOf("public List<StartupProgram> GetStartupPrograms()", StringComparison.Ordinal));
            listing = listing.Substring(0, listing.IndexOf("ReadRunValue(runKey, name)", StringComparison.Ordinal));

            Assert.Contains("name.IndexOf('\\0') >= 0", listing, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(new byte[] { 0x03 })]
        [InlineData(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
        [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
        [InlineData(new byte[] { 0x03, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01 })]
        public void AlreadyDisabledEntry_IsNotWritten(byte[] approved)
        {
            SeedOwnEntry();
            _sandbox.SeedRun("D", @"C:\Apps\D.exe");
            _sandbox.SeedApproved("D", approved);
            var service = Service();

            Assert.Empty(service.TakeOverWindowsEntries(true));

            Assert.Equal(approved, ApprovedOf("D"));
            Assert.Empty(AppValues());
            Assert.Equal("D(off)", Describe(service.GetStartupPrograms()));
        }

        [Theory]
        [InlineData("StartupController", null)]
        [InlineData("startupcontroller", null)]
        [InlineData("StartupController", (byte)0x02)]
        public void OwnEntry_IsNeverWrittenOrListed(string name, byte? approved)
        {
            _sandbox.SeedRun(name, "\"C:\\x\\StartupController.exe\" --launch");
            if (approved.HasValue)
                _sandbox.SeedApproved(name, Approved(approved.Value));
            var service = Service();

            Assert.Empty(service.TakeOverWindowsEntries(true));

            Assert.Equal(approved.HasValue ? Approved(approved.Value) : null, ApprovedOf(name));
            Assert.Empty(service.GetStartupPrograms());
            Assert.Empty(AppValues());
        }

        [Fact]
        public void OwnEntryDisabledInWindows_ClosesTheGate()
        {
            SeedOwnEntry(Approved(0x03));
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            Assert.Empty(Service().TakeOverWindowsEntries(true));

            Assert.Null(ApprovedOf("N"));
            Assert.Equal(Approved(0x03), ApprovedOf("StartupController"));
            Assert.Empty(AppValues());
        }

        [Fact]
        public void Gate_SettingOff_WritesNothing_AndLogsOneInfoLine()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var before = TestLog.LinesContaining("Takeover skipped: \"Launch Enabled Programs").Count;

            Assert.Empty(Service().TakeOverWindowsEntries(false));

            Assert.False(_sandbox.KeyExists(RegistrySandbox.ApprovedPath));
            Assert.Empty(AppValues());
            Assert.True(TestLog.LinesContaining("Takeover skipped: \"Launch Enabled Programs").Count > before);
        }

        [Fact]
        public void Gate_OwnEntryMissing_WritesNothing()
        {
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");

            Assert.Empty(Service().TakeOverWindowsEntries(true));

            Assert.False(_sandbox.KeyExists(RegistrySandbox.ApprovedPath));
            Assert.Empty(AppValues());
        }

        // N3: the gate requires an own entry that starts this app with --launch
        [Theory]
        [InlineData("\"C:\\Other\\Tool.exe\" --launch")]
        [InlineData("\"C:\\x\\StartupController.exe\"")]
        [InlineData("\"C:\\x\\StartupController.exe\" --launchx")]
        [InlineData("\"C:\\x\\StartupController.exe\" --LAUNCH")]
        [InlineData("C:\\x\\Other.exe --launch \"C:\\x\\StartupController.exe\"")]
        public void Gate_OwnEntryThatDoesNotLaunchThisApp_WritesNothing(string ownCommand)
        {
            _sandbox.SeedRun("StartupController", ownCommand);
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");

            Assert.Empty(Service().TakeOverWindowsEntries(true));

            Assert.Null(ApprovedOf("N"));
            Assert.Empty(AppValues());
        }

        [Fact]
        public void Gate_OwnEntryNotAString_WritesNothing()
        {
            _sandbox.SeedRunValue("StartupController", new byte[] { 1 }, RegistryValueKind.Binary);
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");

            Assert.Empty(Service().TakeOverWindowsEntries(true));

            Assert.Null(ApprovedOf("N"));
        }

        [Theory]
        [InlineData("\"C:\\x\\StartupController.exe\" --launch", RegistryValueKind.String)]
        [InlineData("C:\\x\\StartupController.exe --launch", RegistryValueKind.String)]
        [InlineData("\"C:\\x\\StartupController.exe\" --minimized --launch", RegistryValueKind.ExpandString)]
        public void Gate_ValidOwnEntry_OpensTheGate(string ownCommand, RegistryValueKind kind)
        {
            _sandbox.SeedRunValue("StartupController", ownCommand, kind);
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");

            Assert.Equal(new[] { "N" }, Service().TakeOverWindowsEntries(true));
        }

        [Fact]
        public void Gate_DefaultAppCheck_RejectsAnotherExecutable()
        {
            Assert.False(StartupRegistryService.IsThisAppExe(@"C:\x\StartupController.exe"));
            Assert.False(StartupRegistryService.IsThisAppExe("StartupController.exe"));
            Assert.False(StartupRegistryService.IsThisAppExe(""));
            Assert.True(StartupRegistryService.IsThisAppExe(Environment.ProcessPath!));
        }

        [Theory]
        [InlineData(RegistryValueKind.DWord)]
        [InlineData(RegistryValueKind.Binary)]
        [InlineData(RegistryValueKind.MultiString)]
        public void NonStringRunValue_IsNotTakenOver(RegistryValueKind kind)
        {
            SeedOwnEntry();
            object value = kind switch
            {
                RegistryValueKind.DWord => 1,
                RegistryValueKind.Binary => new byte[] { 1, 2 },
                _ => new[] { @"C:\Apps\X.exe" }
            };
            _sandbox.SeedRunValue("X", value, kind);
            var service = Service();

            Assert.Empty(service.TakeOverWindowsEntries(true));

            Assert.Null(_sandbox.ReadKind(RegistrySandbox.ApprovedPath, "X"));
            Assert.Empty(service.GetStartupPrograms());
            Assert.Empty(AppValues());
        }

        [Fact]
        public void QuotedAndExpandStringCommands_AreFingerprintedRaw_AndRunIsUnchanged()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("B", "\"C:\\Apps\\B b.exe\" --min");
            _sandbox.SeedRunValue("C", @"%ProgramFiles%\C\C.exe", RegistryValueKind.ExpandString);
            var runBefore = _sandbox.Dump(RegistrySandbox.RunPath);

            Assert.Equal(new[] { "B", "C" }, Service().TakeOverWindowsEntries(true).OrderBy(n => n, StringComparer.Ordinal));

            var fingerprints = Multi(RegistrySandbox.EnabledFingerprintsValue)!;
            Assert.Contains("B|" + Fp("\"C:\\Apps\\B b.exe\" --min"), fingerprints);
            Assert.Contains("C|" + Fp(@"%ProgramFiles%\C\C.exe", RegistryValueKind.ExpandString), fingerprints);
            Assert.Equal(runBefore, _sandbox.Dump(RegistrySandbox.RunPath));
        }

        [Fact]
        public void MissingApprovedKey_IsCreated()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            Assert.False(_sandbox.KeyExists(RegistrySandbox.ApprovedPath));

            Assert.Single(Service().TakeOverWindowsEntries(true));

            Assert.Equal(DisabledBytes, ApprovedOf("N"));
        }

        [Fact]
        public void MissingRunKey_ReturnsEmpty_AndCreatesNothing()
        {
            Assert.Empty(Service().TakeOverWindowsEntries(true));

            Assert.False(_sandbox.KeyExists(RegistrySandbox.RunPath));
            Assert.False(_sandbox.KeyExists(RegistrySandbox.ApprovedPath));
            Assert.False(_sandbox.KeyExists(RegistrySandbox.AppPath));
        }

        [Fact]
        public void OverLongName_IsNotTakenOver_AndWarns()
        {
            SeedOwnEntry();
            var name = new string('L', StartupRegistryService.MAX_NAME_LENGTH + 1);
            _sandbox.SeedRun(name, @"C:\Apps\L.exe");
            _sandbox.SeedApproved(name, Approved(0x02));

            Assert.Empty(Service().TakeOverWindowsEntries(true));

            Assert.Equal(Approved(0x02), ApprovedOf(name));
            Assert.Empty(AppValues());
            Assert.NotEmpty(TestLog.LinesContaining("Not taking over '" + name + "'"));
        }

        [Fact]
        public void StoreAtMaxNames_NewNamesAreNotTakenOver_AndWarn()
        {
            SeedOwnEntry();
            var stored = Enumerable.Range(0, StartupRegistryService.MAX_NAMES).Select(i => "S" + i).ToArray();
            _sandbox.SeedStoredOrder(stored, Array.Empty<string>());
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");

            Assert.Empty(Service().TakeOverWindowsEntries(true));

            Assert.Null(ApprovedOf("N"));
            Assert.Null(Multi(RegistrySandbox.TakenOverValue));
            Assert.Equal(stored, Multi(RegistrySandbox.ProgramOrderValue));
        }

        [Fact]
        public void NoCandidates_LeavesTheAppKeyByteIdentical()
        {
            SeedOwnEntry();
            SeedDisabled("A");
            _sandbox.SeedStoredOrder(new[] { "A", "Gone" }, new[] { "A" });
            _sandbox.SeedAppValue(RegistrySandbox.OrderValue, "A", RegistryValueKind.String);
            var before = AppValues();

            Assert.Empty(Service().TakeOverWindowsEntries(true));

            Assert.Equal(before, AppValues());
        }

        [Fact]
        public void SecondCall_TakesNothing_AndWritesNothing()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = Service();
            Assert.Single(service.TakeOverWindowsEntries(true));
            var app = AppValues();
            var approved = _sandbox.Dump(RegistrySandbox.ApprovedPath);

            var later = new StartupRegistryService(_sandbox.Root, () => Clock.AddHours(1), IsTestApp);
            Assert.Empty(later.TakeOverWindowsEntries(true));

            Assert.Equal(app, AppValues());
            Assert.Equal(approved, _sandbox.Dump(RegistrySandbox.ApprovedPath));
        }

        // ---------- order and fingerprints ----------

        [Fact]
        public void NewEntry_IsAppended_OtherFlagsAndFingerprintsKept()
        {
            SeedOwnEntry();
            SeedDisabled("A");
            SeedDisabled("B");
            _sandbox.SeedStoredOrder(new[] { "A", "B" }, new[] { "A" });
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue, new[] { "A|" + Fp(@"C:\Apps\A.exe") }, RegistryValueKind.MultiString);
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");

            Service().TakeOverWindowsEntries(true);

            Assert.Equal(new[] { "A", "B", "N" }, Multi(RegistrySandbox.ProgramOrderValue));
            Assert.Equal(new[] { "A", "N" }, Multi(RegistrySandbox.EnabledProgramsValue));
            Assert.Equal(new[] { "A|" + Fp(@"C:\Apps\A.exe"), "N|" + Fp(@"C:\Apps\N.exe") }, Multi(RegistrySandbox.EnabledFingerprintsValue));
        }

        [Fact]
        public void HiddenStoredDisabledName_KeepsItsPosition_BecomesEnabled_WithTheCurrentFingerprint()
        {
            SeedOwnEntry();
            SeedDisabled("A");
            SeedDisabled("B");
            _sandbox.SeedRun("X", @"C:\Apps\X2.exe");
            _sandbox.SeedApproved("X", Approved(0x02));
            _sandbox.SeedStoredOrder(new[] { "A", "X", "B" }, Array.Empty<string>());
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue, Array.Empty<string>(), RegistryValueKind.MultiString);
            var service = Service();

            Assert.Equal(new[] { "X" }, service.TakeOverWindowsEntries(true));

            Assert.Equal(new[] { "A", "X", "B" }, Multi(RegistrySandbox.ProgramOrderValue));
            Assert.Equal("A(off),X(on),B(off)", Describe(service.GetStartupPrograms()));
            Assert.Equal(new[] { "X|" + Fp(@"C:\Apps\X2.exe") }, Multi(RegistrySandbox.EnabledFingerprintsValue));
        }

        // Security L3: a name the user enabled for another command is not re-approved by the takeover (D7 holds)
        [Fact]
        public void HiddenStoredEnabledName_WithAnOldFingerprint_IsTakenOverButStaysChanged()
        {
            SeedOwnEntry();
            SeedDisabled("A");
            var marker = TestLog.Unique("L3x");
            _sandbox.SeedRun(marker, @"C:\Apps\X2.exe");
            _sandbox.SeedApproved(marker, Approved(0x02));
            _sandbox.SeedStoredOrder(new[] { "A", marker }, new[] { marker });
            var old = marker + "|" + Fp(@"C:\Apps\X1.exe");
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue, new[] { old }, RegistryValueKind.MultiString);
            var service = Service();

            Assert.Equal(new[] { marker }, service.TakeOverWindowsEntries(true));

            Assert.Equal(DisabledBytes, ApprovedOf(marker));
            Assert.Equal(new[] { old }, Multi(RegistrySandbox.EnabledFingerprintsValue));
            Assert.Equal($"A(off),{marker}(changed)", Describe(service.GetStartupPrograms()));
            Assert.Contains(marker, Multi(RegistrySandbox.TakenOverValue)!);
            var warning = Assert.Single(TestLog.LinesContaining(marker), l => l.Contains("\tWARN\t", StringComparison.Ordinal) && l.Contains("taken over but not launched", StringComparison.Ordinal));
            Assert.DoesNotContain(@"C:\Apps", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void HiddenStoredEnabledName_WithoutAFingerprint_OnceFingerprintsExist_StaysChanged()
        {
            SeedOwnEntry();
            SeedDisabled("A");
            _sandbox.SeedRun("X", @"C:\Apps\X.exe");
            _sandbox.SeedStoredOrder(new[] { "A", "X" }, new[] { "A", "X" });
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue, new[] { "A|" + Fp(@"C:\Apps\A.exe") }, RegistryValueKind.MultiString);
            var service = Service();

            Assert.Equal(new[] { "X" }, service.TakeOverWindowsEntries(true));

            Assert.Equal("A(on),X(changed)", Describe(service.GetStartupPrograms()));
        }

        [Fact]
        public void ChangedRow_StaysChanged_WhenAnotherEntryIsTakenOver()
        {
            SeedOwnEntry();
            SeedDisabled("C", @"C:\Apps\C-new.exe");
            _sandbox.SeedStoredOrder(new[] { "C" }, new[] { "C" });
            var old = "C|" + Fp(@"C:\Apps\C-old.exe");
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue, new[] { old }, RegistryValueKind.MultiString);
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = Service();

            service.TakeOverWindowsEntries(true);

            Assert.Equal(new[] { "C", "N" }, Multi(RegistrySandbox.EnabledProgramsValue));
            Assert.Contains(old, Multi(RegistrySandbox.EnabledFingerprintsValue)!);
            Assert.Equal("C(changed),N(on)", Describe(service.GetStartupPrograms()));
        }

        [Fact]
        public void LegacyStore_GetsItsFingerprintsRecorded_SoNothingBecomesChanged()
        {
            SeedOwnEntry();
            SeedDisabled("A");
            SeedDisabled("B");
            _sandbox.SeedOrder("A", "B");
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = Service();

            service.TakeOverWindowsEntries(true);

            Assert.Equal(
                new[] { "A|" + Fp(@"C:\Apps\A.exe"), "B|" + Fp(@"C:\Apps\B.exe"), "N|" + Fp(@"C:\Apps\N.exe") },
                Multi(RegistrySandbox.EnabledFingerprintsValue));
            Assert.Equal("A(on),B(on),N(on)", Describe(service.GetStartupPrograms()));
            Assert.Equal("A;B", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
        }

        [Fact]
        public void PreD7Store_GetsItsFingerprintsRecorded_SoNothingBecomesChanged()
        {
            SeedOwnEntry();
            SeedDisabled("A");
            _sandbox.SeedStoredOrder(new[] { "A" }, new[] { "A" });
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = Service();

            service.TakeOverWindowsEntries(true);

            Assert.Equal(new[] { "A|" + Fp(@"C:\Apps\A.exe"), "N|" + Fp(@"C:\Apps\N.exe") }, Multi(RegistrySandbox.EnabledFingerprintsValue));
            Assert.Equal("A(on),N(on)", Describe(service.GetStartupPrograms()));
        }

        [Fact]
        public void BuildTakeoverOrder_IsPure_AndCapsNewNames()
        {
            var previous = StoredOrder.Create(new[] { "A" }, new[] { "A" }, fingerprintsKnown: false);
            var candidates = new List<KeyValuePair<string, string>> { new("N", "nn"), new("a", "aa") };

            var (next, accepted, keptChanged, overCap) = StartupRegistryService.BuildTakeoverOrder(previous, candidates,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["A"] = "old" });

            Assert.Equal(new[] { "A", "N" }, next.Order);
            Assert.Equal(new[] { "N", "a" }, accepted);
            Assert.Empty(keptChanged); // migration: no stored fingerprint yet, so not Changed
            Assert.Equal(0, overCap);
            Assert.Equal("aa", next.Fingerprints["A"]); // the candidate's current fingerprint wins over migration
            Assert.True(next.FingerprintsKnown);
            Assert.Equal(new[] { "A" }, previous.Order);
        }

        // ---------- failures ----------

        private sealed class FailingService : StartupRegistryService
        {
            public FailingService(RegistryKey root) : base(root, () => Clock, IsTestApp) { }

            public HashSet<string> FailValues { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> FailApproved { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public Action<string>? BeforeApproved { get; set; }

            internal override void WriteMultiString(RegistryKey key, string name, string[] values)
            {
                if (FailValues.Contains(name)) throw new UnauthorizedAccessException("denied " + name);
                base.WriteMultiString(key, name, values);
            }

            internal override void WriteApprovedDisabled(RegistryKey approvedKey, string name, byte[] value)
            {
                BeforeApproved?.Invoke(name);
                if (FailApproved.Contains(name)) throw new UnauthorizedAccessException("denied");
                base.WriteApprovedDisabled(approvedKey, name, value);
            }
        }

        [Fact]
        public void EnabledProgramsWriteFails_NothingIsDisabledInWindows()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N1", @"C:\Apps\N1.exe");
            _sandbox.SeedRun("N2", @"C:\Apps\N2.exe");
            var service = new FailingService(_sandbox.Root);
            service.FailValues.Add(RegistrySandbox.EnabledProgramsValue);

            Assert.Empty(service.TakeOverWindowsEntries(true));

            Assert.Null(ApprovedOf("N1"));
            Assert.Null(ApprovedOf("N2"));
            Assert.Empty(service.GetStartupPrograms());
            Assert.NotEmpty(TestLog.LinesContaining("Takeover skipped: the order could not be saved; Windows keeps starting 2 program(s)"));
        }

        [Fact]
        public void FingerprintsWriteFailsOnFirstSave_ProgramOrderIsNotWritten_NothingDisabled()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = new FailingService(_sandbox.Root);
            service.FailValues.Add(RegistrySandbox.EnabledFingerprintsValue);

            Assert.Empty(service.TakeOverWindowsEntries(true));

            Assert.Null(Multi(RegistrySandbox.ProgramOrderValue));
            Assert.Null(ApprovedOf("N"));
        }

        [Fact]
        public void RecordWriteFails_NothingIsDisabledInWindows()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = new FailingService(_sandbox.Root);
            service.FailValues.Add(RegistrySandbox.TakenOverValue);

            Assert.Empty(service.TakeOverWindowsEntries(true));

            Assert.Null(ApprovedOf("N"));
            Assert.Empty(service.GetStartupPrograms()); // stored Enabled but hidden: Windows keeps starting it
        }

        [Fact]
        public void ApprovedWriteFailsForOneEntry_TheOthersAreTakenOver_AndTheNextCallRetries()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N1", @"C:\Apps\N1.exe");
            _sandbox.SeedRun("N2", @"C:\Apps\N2.exe");
            var service = new FailingService(_sandbox.Root);
            service.FailApproved.Add("N2");

            Assert.Equal(new[] { "N1" }, service.TakeOverWindowsEntries(true));

            Assert.Equal(DisabledBytes, ApprovedOf("N1"));
            Assert.Null(ApprovedOf("N2"));
            Assert.Equal(new[] { "N1", "N2" }, Multi(RegistrySandbox.EnabledProgramsValue));
            Assert.Equal("N1(on)", Describe(service.GetStartupPrograms()));
            Assert.NotEmpty(TestLog.LinesContaining("Could not take over 'N2'"));

            service.FailApproved.Clear();
            Assert.Equal(new[] { "N2" }, service.TakeOverWindowsEntries(true));
            Assert.Equal(new[] { "N1", "N2" }, Multi(RegistrySandbox.ProgramOrderValue));
            Assert.Equal("N1(on),N2(on)", Describe(service.GetStartupPrograms()));
        }

        [Fact]
        public void EntryRemovedBeforeTheListing_IsHidden_AndTheOrphanApprovedValueStays()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = Service();
            service.TakeOverWindowsEntries(true);

            _sandbox.DeleteRunValue("N");

            Assert.Empty(service.GetStartupPrograms());
            Assert.Equal(new[] { "N" }, Multi(RegistrySandbox.ProgramOrderValue));
            Assert.Equal(DisabledBytes, ApprovedOf("N"));
        }

        [Fact]
        public void RunDataChangedBeforeTheListing_IsListedChanged()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = Service();
            service.TakeOverWindowsEntries(true);

            _sandbox.SeedRun("N", @"C:\Evil\N.exe");

            Assert.Equal("N(changed)", Describe(service.GetStartupPrograms()));
        }

        // ---------- takeover record ----------

        [Fact]
        public void Record_HoldsOnlyTakenOverNames()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N1", @"C:\Apps\N1.exe");
            _sandbox.SeedRun("N2", @"C:\Apps\N2.exe");
            SeedDisabled("D");
            _sandbox.SeedRunValue("W", 1, RegistryValueKind.DWord);

            Service().TakeOverWindowsEntries(true);

            Assert.Equal(new[] { "N1", "N2" }, Multi(RegistrySandbox.TakenOverValue));
        }

        [Fact]
        public void RetakeOver_OfARecordedName_IsRecordedOnce()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = Service();
            service.TakeOverWindowsEntries(true);
            _sandbox.SeedApprovedValue("N", Approved(0x02), RegistryValueKind.Binary);
            _sandbox.SeedAppValue(RegistrySandbox.TakenOverValue, new[] { "n" }, RegistryValueKind.MultiString);

            Assert.Equal(new[] { "N" }, service.TakeOverWindowsEntries(true));

            Assert.Equal(new[] { "n" }, Multi(RegistrySandbox.TakenOverValue));
            Assert.Equal(DisabledBytes, ApprovedOf("N"));
        }

        [Fact]
        public void WrongKindRecord_AbortsTheTakeover_WritesNothing()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            _sandbox.SeedAppValue(RegistrySandbox.TakenOverValue, "N", RegistryValueKind.String);
            var before = AppValues();

            Assert.Empty(Service().TakeOverWindowsEntries(true));

            Assert.Equal(before, AppValues());
            Assert.Null(ApprovedOf("N"));
            Assert.NotEmpty(TestLog.LinesContaining("Takeover skipped: " + RegistrySandbox.TakenOverValue + " under"));
        }

        [Fact]
        public void UiSave_LeavesTheRecordByteIdentical()
        {
            SeedOwnEntry();
            _sandbox.SeedRun("N", @"C:\Apps\N.exe");
            var service = Service();
            service.TakeOverWindowsEntries(true);
            var before = Multi(RegistrySandbox.TakenOverValue);
            var model = new StartupListModel();
            model.Load(service.GetStartupPrograms());
            model.Toggle(model.Programs[0]);

            service.SaveStartupOrder(model.Snapshot());

            Assert.Equal(before, Multi(RegistrySandbox.TakenOverValue));
            Assert.Equal(RegistryValueKind.MultiString, _sandbox.ReadKind(RegistrySandbox.AppPath, RegistrySandbox.TakenOverValue));
        }

        [Fact]
        public void Record_SharesItsFormatWithTheHelper()
        {
            Assert.Equal(StartupRegistryService.TAKEN_OVER_VALUE, RegistrySandbox.TakenOverValue);
            Assert.Equal(StartupRegistryService.MAX_NAMES, TakeoverRecord.MaxNames);
            Assert.Equal(StartupRegistryService.MAX_NAME_LENGTH, TakeoverRecord.MaxNameLength);
        }

        // ---------- logging ----------

        [Fact]
        public void EachTakenOverEntry_LogsOneEscapedInfoLine_WithoutItsCommand()
        {
            SeedOwnEntry();
            var marker = TestLog.Unique("Tk");
            var name = marker + "\r\nFAKE|x";
            var command = @"C:\Secret\" + marker + ".exe --token hunter2";
            _sandbox.SeedRun(name, command);

            Assert.Single(Service().TakeOverWindowsEntries(true));

            var lines = TestLog.LinesContaining(marker);
            var line = Assert.Single(lines);
            Assert.Contains("\tINFO\t", line, StringComparison.Ordinal);
            Assert.Contains("Took over '" + marker + @"\r\nFAKE|x'", line, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\Secret", line, StringComparison.Ordinal);
        }
    }
}
