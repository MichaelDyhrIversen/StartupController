using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // D7: enabled programs are bound to a SHA-256 fingerprint of their raw Run data (EnabledFingerprints),
    // plus the review fixes that shipped with it (caps, logging of skipped entries).
    // Sandbox only; launches go through FakeProcessStarter.
    public sealed class FingerprintTests : IDisposable
    {
        private readonly RegistrySandbox _sandbox = new RegistrySandbox();
        private readonly StartupRegistryService _service;

        public FingerprintTests()
        {
            _service = new StartupRegistryService(_sandbox.Root);
        }

        public void Dispose() => _sandbox.Dispose();

        private static string Describe(IEnumerable<StartupProgram> programs) =>
            string.Join(",", programs.Select(p => p.Name + (p.Changed ? "(changed)" : p.Enabled ? "(on)" : "(off)")));

        // Independent re-implementation of the D7 hash input: UTF-16LE of "{kind}\0{raw}"
        private static string Sha(string raw, RegistryValueKind kind = RegistryValueKind.String) =>
            Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(kind + "\0" + raw))).ToLowerInvariant();

        private void SeedDisabled(string name, string command)
        {
            _sandbox.SeedRun(name, command);
            _sandbox.SeedApproved(name, Approved(0x03));
        }

        private StartupListModel LoadModel()
        {
            var model = new StartupListModel();
            model.Load(_service.GetStartupPrograms());
            return model;
        }

        private static StartupProgram Get(StartupListModel model, string name) => model.Programs.Single(p => p.Name == name);

        private string[] Fingerprints() =>
            _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.EnabledFingerprintsValue) as string[] ?? Array.Empty<string>();

        private string[]? ReadMulti(string name) => _sandbox.ReadValue(RegistrySandbox.AppPath, name) as string[];

        // What --launch mode would hand to the process starter
        private static List<string> Launched(StartupListModel model)
        {
            var starter = new FakeProcessStarter();
            var launcher = new ProgramLauncher(starter);
            foreach (var program in model.EnabledPrograms())
                launcher.Launch(program);
            return starter.Started.Select(s => s.FileName).ToList();
        }

        // Enables the names in a fresh load and saves, as the UI would
        private void EnableAndSave(params string[] names)
        {
            var model = LoadModel();
            foreach (var name in names)
                model.Enable(Get(model, name));
            _service.SaveStartupOrder(model.Snapshot());
        }

        private static string ReadLog()
        {
            using var stream = new FileStream(LoggingService.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N").Substring(0, 8);

        // --- Pure helpers ---

        [Fact]
        public void Compute_IsLowercaseSha256OfUtf16LeBytes()
        {
            // Known vector: SHA-256 of the UTF-16LE bytes of "String\0a" (kind name, NUL, raw)
            var bytes = new byte[] { 0x53, 0, 0x74, 0, 0x72, 0, 0x69, 0, 0x6E, 0, 0x67, 0, 0x00, 0, 0x61, 0 };
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), RunFingerprint.Compute(RegistryValueKind.String, "a"));
            Assert.Equal(Sha(@"C:\Apps\A.exe"), RunFingerprint.Compute(RegistryValueKind.String, @"C:\Apps\A.exe"));
            Assert.Matches("^[0-9a-f]{64}$", RunFingerprint.Compute(RegistryValueKind.String, @"C:\Apps\A.exe"));
        }

        [Fact]
        public void Compute_IncludesTheKind()
        {
            Assert.NotEqual(
                RunFingerprint.Compute(RegistryValueKind.String, @"%X%\a.exe"),
                RunFingerprint.Compute(RegistryValueKind.ExpandString, @"%X%\a.exe"));
        }

        [Fact]
        public void FromRunValue_ExpandsExpandString_LeavesStringLiteral()
        {
            var variable = "SC_TEST_" + Guid.NewGuid().ToString("N");
            var raw = "%" + variable + @"%\x.exe";
            try
            {
                Environment.SetEnvironmentVariable(variable, @"C:\Expanded");

                Assert.Equal(@"C:\Expanded\x.exe", RunFingerprint.FromRunValue(RegistryValueKind.ExpandString, raw).Path);
                Assert.Equal(raw, RunFingerprint.FromRunValue(RegistryValueKind.String, raw).Path);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        [Theory]
        [InlineData("a|b|", false)]
        [InlineData("|", false)]
        [InlineData("noseparator", false)]
        [InlineData("a|abc", false)]
        public void TryParseLine_RejectsMalformedLines(string line, bool ok)
        {
            Assert.Equal(ok, RunFingerprint.TryParseLine(line, out _, out _));
        }

        [Fact]
        public void TryParseLine_SplitsOnTheLastBar()
        {
            var hash = Sha("x");

            Assert.True(RunFingerprint.TryParseLine("a|b|" + hash.ToUpperInvariant(), out var name, out var parsed));

            Assert.Equal("a|b", name);
            Assert.Equal(hash, parsed);
        }

        [Fact]
        public void StatusText_ShowsChanged()
        {
            var program = P("A");
            program.Changed = true;

            Assert.Equal("Changed – re-enable to launch", Form1.StatusText(program));
            Assert.Equal("Disabled", Form1.StatusText(P("B")));
            Assert.Equal("Enabled", Form1.StatusText(P("C", enabled: true)));
        }

        // --- Match / mismatch / re-enable / reinstall ---

        [Fact]
        public void Match_EnabledAndLaunched()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            EnableAndSave("A");

            var model = LoadModel();

            Assert.Equal("A(on)", Describe(model.Programs));
            Assert.Equal(new[] { @"C:\Apps\A.exe" }, Launched(model));
        }

        [Fact]
        public void Mismatch_OnVisibleEntry_IsChanged_NotLaunched_AndLogsNameOnly()
        {
            var name = Unique("Visible");
            SeedDisabled(name, @"C:\OldSecret\a.exe --old-arg");
            EnableAndSave(name);
            _sandbox.SeedRun(name, @"C:\NewSecret\a.exe --new-arg");

            var model = LoadModel();

            Assert.Equal($"{name}(changed)", Describe(model.Programs));
            Assert.Empty(Launched(model));
            var warning = Assert.Single(ReadLog().Split('\n'), l => l.Contains("WARN") && l.Contains(name));
            Assert.DoesNotContain("Secret", warning);
            Assert.DoesNotContain("-arg", warning);
        }

        [Fact]
        public void ReEnable_RecordsTheNewFingerprint_AndLaunches()
        {
            SeedDisabled("A", @"C:\Old\a.exe");
            EnableAndSave("A");
            _sandbox.SeedRun("A", @"C:\New\a.exe");

            var model = LoadModel();
            Assert.True(Get(model, "A").Changed);
            model.Enable(Get(model, "A"));
            Assert.False(Get(model, "A").Changed);
            _service.SaveStartupOrder(model.Snapshot());

            Assert.Equal(new[] { "A|" + Sha(@"C:\New\a.exe") }, Fingerprints());
            Assert.Equal(new[] { @"C:\New\a.exe" }, Launched(LoadModel()));
        }

        [Fact]
        public void Reinstall_WithIdenticalData_KeepsLaunching()
        {
            SeedDisabled("A", "\"C:\\Program Files\\A\\a.exe\" --tray");
            EnableAndSave("A");

            _sandbox.DeleteRunValue("A");
            _service.SaveStartupOrder(LoadModel().Snapshot()); // saved while A is gone: A is hidden, keeps its hash
            SeedDisabled("A", "\"C:\\Program Files\\A\\a.exe\" --tray");

            var model = LoadModel();
            Assert.Equal("A(on)", Describe(model.Programs));
            Assert.Single(Launched(model));
        }

        [Fact]
        public void NameReuse_DifferentProgramUnderTheSameName_IsNotLaunched()
        {
            SeedDisabled("A", @"C:\Apps\Original.exe");
            EnableAndSave("A");
            _sandbox.DeleteRunValue("A");
            _service.SaveStartupOrder(LoadModel().Snapshot());

            SeedDisabled("A", @"C:\Other\Impostor.exe");

            var model = LoadModel();
            Assert.Equal("A(changed)", Describe(model.Programs));
            Assert.Empty(Launched(model));
        }

        [Fact]
        public void HiddenWithFingerprint_ReappearsInPlace_SameDataEnabled_DifferentDataChanged()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            SeedDisabled("X", @"C:\Apps\X.exe");
            EnableAndSave("A", "X");
            _sandbox.DeleteRunValue("X");

            var model = LoadModel();
            Assert.Equal("A(on)", Describe(model.Programs));
            Assert.Equal(new[] { @"C:\Apps\A.exe" }, Launched(model));

            SeedDisabled("X", @"C:\Apps\X.exe");
            Assert.Equal("A(on),X(on)", Describe(_service.GetStartupPrograms()));

            SeedDisabled("X", @"C:\Apps\X2.exe");
            Assert.Equal("A(on),X(changed)", Describe(_service.GetStartupPrograms()));
        }

        [Fact]
        public void EntryRemovedExternally_KeepsFlagAndFingerprintInStorage()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            SeedDisabled("B", @"C:\Apps\B.exe");
            var model = LoadModel();
            model.Enable(Get(model, "A"));
            model.Enable(Get(model, "B"));

            _sandbox.DeleteRunValue("B");
            _service.SaveStartupOrder(model.Snapshot());

            Assert.Contains("B|" + Sha(@"C:\Apps\B.exe"), Fingerprints());
            Assert.Equal("A(on)", Describe(_service.GetStartupPrograms()));
        }

        // --- Raw data and storage ---

        [Fact]
        public void ExpandString_IsFingerprintedUnexpanded()
        {
            var variable = "SC_TEST_" + Guid.NewGuid().ToString("N");
            var raw = "%" + variable + @"%\x.exe";
            _sandbox.SeedRunValue("E", raw, RegistryValueKind.ExpandString);
            _sandbox.SeedApproved("E", Approved(0x03));
            try
            {
                Environment.SetEnvironmentVariable(variable, @"C:\One");
                var first = Assert.Single(_service.GetStartupPrograms());
                Environment.SetEnvironmentVariable(variable, @"C:\Two");
                var second = Assert.Single(_service.GetStartupPrograms());

                Assert.Equal(Sha(raw, RegistryValueKind.ExpandString), first.Fingerprint);
                Assert.Equal(first.Fingerprint, second.Fingerprint);
                Assert.Equal(@"C:\One\x.exe", first.Path); // Path is still expanded for launching
                Assert.Equal(@"C:\Two\x.exe", second.Path);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        [Fact]
        public void KindSwitch_StringToExpandString_SameText_IsChanged()
        {
            var raw = @"%SystemRoot%\notepad.exe";
            _sandbox.SeedRunValue("K", raw, RegistryValueKind.String);
            _sandbox.SeedApproved("K", Approved(0x03));
            EnableAndSave("K");
            Assert.Equal("K(on)", Describe(_service.GetStartupPrograms()));

            _sandbox.SeedRunValue("K", raw, RegistryValueKind.ExpandString);

            var model = LoadModel();
            Assert.Equal("K(changed)", Describe(model.Programs));
            Assert.Empty(Launched(model));
        }

        [Fact]
        public void Path_ExpandStringIsExpanded_StringIsLiteral_ThroughTheService()
        {
            var variable = "SC_TEST_" + Guid.NewGuid().ToString("N");
            var raw = "%" + variable + @"%\x.exe";
            _sandbox.SeedRunValue("E", raw, RegistryValueKind.ExpandString);
            _sandbox.SeedRunValue("S", raw, RegistryValueKind.String);
            _sandbox.SeedApproved("E", Approved(0x03));
            _sandbox.SeedApproved("S", Approved(0x03));
            try
            {
                Environment.SetEnvironmentVariable(variable, @"C:\Expanded");
                var listed = _service.GetStartupPrograms();

                Assert.Equal(@"C:\Expanded\x.exe", listed.Single(p => p.Name == "E").Path);
                Assert.Equal(raw, listed.Single(p => p.Name == "S").Path);
                Assert.Equal(Sha(raw, RegistryValueKind.ExpandString), listed.Single(p => p.Name == "E").Fingerprint);
                Assert.Equal(Sha(raw), listed.Single(p => p.Name == "S").Fingerprint);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        [Fact]
        public void PathAndFingerprint_ComeFromOneRead()
        {
            // The registry holds one command, but the seam returns another on the single read: both Path and
            // Fingerprint must reflect what the read returned, and the value is read exactly once
            SeedDisabled("A", @"C:\Registry\a.exe");
            var service = new ReadOnceService(_sandbox.Root, @"C:\FromRead\a.exe");

            var program = Assert.Single(service.GetStartupPrograms());

            Assert.Equal(1, service.Reads["A"]);
            Assert.Equal(@"C:\FromRead\a.exe", program.Path);
            Assert.Equal(Sha(@"C:\FromRead\a.exe"), program.Fingerprint);
        }

        [Fact]
        public void RunValueDeletedBetweenReads_IsSkipped()
        {
            SeedDisabled("A", @"C:\Apps\a.exe");
            var service = new ReadOnceService(_sandbox.Root, null);

            Assert.Empty(service.GetStartupPrograms());
        }

        // Returns a fixed read result (or "deleted") and counts reads per name
        private sealed class ReadOnceService : StartupRegistryService
        {
            private readonly string? _data;

            public ReadOnceService(RegistryKey root, string? data) : base(root)
            {
                _data = data;
            }

            public Dictionary<string, int> Reads { get; } = new Dictionary<string, int>();

            internal override (object? Data, RegistryValueKind Kind) ReadRunValue(RegistryKey runKey, string name)
            {
                Reads[name] = Reads.GetValueOrDefault(name) + 1;
                return _data == null ? (null, RegistryValueKind.None) : (_data, RegistryValueKind.String);
            }
        }

        [Fact]
        public void Storage_HoldsOnlyNamesAndHashes_NeverTheCommand()
        {
            SeedDisabled("A", @"C:\VerySecretPath\a.exe --token=abc");
            SeedDisabled("B", @"C:\VerySecretPath\b.exe");
            EnableAndSave("A", "B");

            Assert.All(Fingerprints(), line => Assert.Matches(@"^[AB]\|[0-9a-f]{64}$", line));
            foreach (var value in _sandbox.ReadAllValues(RegistrySandbox.AppPath))
            {
                var text = value.Data is string[] lines ? string.Join("\n", lines) : value.Data?.ToString() ?? "";
                Assert.DoesNotContain("VerySecretPath", text);
                Assert.DoesNotContain("token", text);
            }
        }

        [Fact]
        public void NameContainingBar_RoundTrips()
        {
            SeedDisabled("a|b", @"C:\Apps\ab.exe");
            EnableAndSave("a|b");

            Assert.Equal(new[] { "a|b|" + Sha(@"C:\Apps\ab.exe") }, Fingerprints());
            Assert.Equal("a|b(on)", Describe(_service.GetStartupPrograms()));
        }

        [Fact]
        public void Disable_DropsTheFingerprint()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            SeedDisabled("B", @"C:\Apps\B.exe");
            EnableAndSave("A", "B");

            var model = LoadModel();
            model.Disable(Get(model, "A"));
            _service.SaveStartupOrder(model.Snapshot());

            Assert.DoesNotContain(Fingerprints(), l => l.StartsWith("A|", StringComparison.Ordinal));
            Assert.Contains(Fingerprints(), l => l.StartsWith("B|", StringComparison.Ordinal));
        }

        [Fact]
        public void ChangedEntry_IsSavedAsDisabled_WithoutFingerprint()
        {
            SeedDisabled("A", @"C:\Old\a.exe");
            EnableAndSave("A");
            _sandbox.SeedRun("A", @"C:\New\a.exe");

            _service.SaveStartupOrder(LoadModel().Snapshot());

            Assert.Empty(ReadMulti(RegistrySandbox.EnabledProgramsValue)!);
            Assert.Empty(Fingerprints());
            Assert.Empty(Launched(LoadModel()));
        }

        [Theory]
        [InlineData(RegistryValueKind.Binary)]
        [InlineData(RegistryValueKind.MultiString)]
        [InlineData(RegistryValueKind.DWord)]
        public void NonStringRunValue_IsNeverListedOrLaunched_EvenWhenMigrating(RegistryValueKind kind)
        {
            object value = kind switch
            {
                RegistryValueKind.Binary => new byte[] { 1, 2, 3 },
                RegistryValueKind.MultiString => new[] { @"C:\Apps\N.exe" },
                _ => 5
            };
            _sandbox.SeedRunValue("N", value, kind);
            _sandbox.SeedApproved("N", Approved(0x03));
            _sandbox.SeedOrder("N");

            Assert.Empty(_service.GetStartupPrograms());
        }

        // --- Migration ---

        [Fact]
        public void Migration_Legacy_ListedNamesAccepted_NothingWrittenOnLoad_HashesRecordedOnSave()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            SeedDisabled("B", @"C:\Apps\B.exe");
            _sandbox.SeedOrder("A", "B");
            var before = _sandbox.ReadAllValues(RegistrySandbox.AppPath).Select(v => v.Name).ToList();

            var model = LoadModel();

            Assert.Equal("A(on),B(on)", Describe(model.Programs));
            Assert.Equal(2, Launched(model).Count);
            Assert.Equal(before, _sandbox.ReadAllValues(RegistrySandbox.AppPath).Select(v => v.Name).ToList());

            _service.SaveStartupOrder(model.Snapshot());
            Assert.Equal(new[] { "A|" + Sha(@"C:\Apps\A.exe"), "B|" + Sha(@"C:\Apps\B.exe") }, Fingerprints());
        }

        [Fact]
        public void Migration_PreD7V2Store_ListedNamesAccepted_AndRecordedAtNextSave()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            _sandbox.SeedStoredOrder(new[] { "A" }, new[] { "A" });

            var model = LoadModel();
            Assert.Equal("A(on)", Describe(model.Programs));
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.EnabledFingerprintsValue));

            _service.SaveStartupOrder(model.Snapshot());
            Assert.Equal(new[] { "A|" + Sha(@"C:\Apps\A.exe") }, Fingerprints());
        }

        [Fact]
        public void HiddenNameWithoutFingerprint_Reappearing_IsChanged_NotLaunched()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            _sandbox.SeedOrder("A", "X");                     // X was enabled but isn't in Run
            _service.SaveStartupOrder(LoadModel().Snapshot()); // first save: A gets a hash, X keeps none

            Assert.Contains("X", ReadMulti(RegistrySandbox.EnabledProgramsValue)!);
            Assert.DoesNotContain(Fingerprints(), l => l.StartsWith("X|", StringComparison.Ordinal));

            SeedDisabled("X", @"C:\Apps\X.exe");
            var model = LoadModel();

            Assert.Equal("A(on),X(changed)", Describe(model.Programs));
            Assert.Equal(new[] { @"C:\Apps\A.exe" }, Launched(model));
        }

        [Fact]
        public void FailedFirstSave_OnFingerprints_ProgramOrderNotWritten_NextLoadMigratesAgain()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            SeedDisabled("B", @"C:\Apps\B.exe");
            _sandbox.SeedOrder("A", "B");
            var failing = new FailingWriteService(_sandbox.Root, RegistrySandbox.EnabledFingerprintsValue);
            var model = new StartupListModel();
            model.Load(failing.GetStartupPrograms());

            Assert.Throws<IOException>(() => failing.SaveStartupOrder(model.Snapshot()));

            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.ProgramOrderValue));
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.EnabledFingerprintsValue));
            Assert.Equal("A(on),B(on)", Describe(_service.GetStartupPrograms()));
            Assert.Equal("A;B", _sandbox.ReadValue(RegistrySandbox.AppPath, RegistrySandbox.OrderValue));
        }

        [Fact]
        public void TornLaterSave_NewEnabledWithOldFingerprints_EnablesNothingUnexpected()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            SeedDisabled("B", @"C:\Apps\B.exe");
            EnableAndSave("A");                              // stored: A enabled with hash, B disabled
            var failing = new FailingWriteService(_sandbox.Root, RegistrySandbox.EnabledFingerprintsValue);
            var model = new StartupListModel();
            model.Load(failing.GetStartupPrograms());
            model.Disable(Get(model, "A"));
            model.Enable(Get(model, "B"));

            Assert.Throws<IOException>(() => failing.SaveStartupOrder(model.Snapshot()));

            // EnabledPrograms = {B} landed, EnabledFingerprints still = {A}: B has no hash -> Changed, A is off
            var reloaded = LoadModel();
            Assert.Equal("A(off),B(changed)", Describe(reloaded.Programs));
            Assert.Empty(Launched(reloaded));
        }

        // --- Malformed EnabledFingerprints ---

        [Fact]
        public void MalformedFingerprints_WrongKind_EnablesNothing()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            _sandbox.SeedStoredOrder(new[] { "A" }, new[] { "A" });
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue, "A|" + Sha(@"C:\Apps\A.exe"), RegistryValueKind.String);

            var model = LoadModel();

            Assert.Equal("A(changed)", Describe(model.Programs));
            Assert.Empty(Launched(model));
        }

        [Fact]
        public void MalformedFingerprintLines_AreIgnored_AndTheirNamesAreNotLaunched()
        {
            SeedDisabled("A", @"C:\Apps\A.exe");
            SeedDisabled("B", @"C:\Apps\B.exe");
            SeedDisabled("C", @"C:\Apps\C.exe");
            _sandbox.SeedStoredOrder(new[] { "A", "B", "C" }, new[] { "A", "B", "C" });
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue, new[]
            {
                "A",                                   // no separator
                "B|1234",                              // hash too short
                "C|" + Sha(@"C:\Apps\C.exe"),          // valid
            }, RegistryValueKind.MultiString);

            Assert.Equal("A(changed),B(changed),C(on)", Describe(_service.GetStartupPrograms()));
        }

        // --- Caps (security Low 3) ---

        [Fact]
        public void Caps_1500Names_ReadAs1024_And300CharNameDropped()
        {
            var longName = new string('L', 300);
            var names = new[] { longName }.Concat(Enumerable.Range(0, 1500).Select(i => "N" + i)).ToArray();
            _sandbox.SeedAppValue(RegistrySandbox.ProgramOrderValue, names, RegistryValueKind.MultiString);
            _sandbox.SeedAppValue(RegistrySandbox.EnabledProgramsValue, names, RegistryValueKind.MultiString);

            var loaded = _service.LoadStoredOrder();

            Assert.Equal(1024, loaded.Order.Count);
            Assert.DoesNotContain(longName, loaded.Order);
            Assert.Equal("N0", loaded.Order[0]);
            Assert.All(loaded.Order, n => Assert.True(n.Length <= 260));
        }

        [Fact]
        public void Caps_SaveWritesAtMost1024Names_AndNeverAnOverLongName()
        {
            var longName = new string('L', 300);
            var names = Enumerable.Range(0, 1100).Select(i => "N" + i).Append(longName).ToArray();

            _service.SaveStartupOrder(StoredOrder.Create(names, names));

            var written = ReadMulti(RegistrySandbox.ProgramOrderValue)!;
            Assert.Equal(1024, written.Length);
            Assert.DoesNotContain(longName, written);
            Assert.True(ReadMulti(RegistrySandbox.EnabledProgramsValue)!.Length <= 1024);
        }

        [Fact]
        public void Caps_HugeCraftedProgramOrder_LoadsAndSavesQuickly()
        {
            var names = Enumerable.Range(0, 30_000).Select(i => "Name" + i).ToArray();
            _sandbox.SeedAppValue(RegistrySandbox.ProgramOrderValue, names, RegistryValueKind.MultiString);
            SeedDisabled("Name5", @"C:\Apps\5.exe");

            var watch = Stopwatch.StartNew();
            var listed = _service.GetStartupPrograms();
            _service.SaveStartupOrder(StoredOrder.Create(new[] { "Name5" }, Array.Empty<string>()));
            watch.Stop();

            Assert.Equal("Name5", Assert.Single(listed).Name);
            Assert.Equal(1024, ReadMulti(RegistrySandbox.ProgramOrderValue)!.Length);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        }

        [Fact]
        public void MergeForSave_IsLinear_ForManyHiddenNames()
        {
            var previous = StoredOrder.Create(Enumerable.Range(0, 100_000).Select(i => "H" + i), Array.Empty<string>());
            var displayed = StoredOrder.Create(new[] { "H50000", "Shown" }, Array.Empty<string>());

            var watch = Stopwatch.StartNew();
            var result = OrderMerger.MergeForSave(displayed, previous);
            watch.Stop();

            Assert.Equal(100_001, result.Order.Count);
            Assert.Equal("H0", result.Order[0]);
            Assert.Equal("H50000", result.Order[50_000]);
            Assert.Equal("H50001", result.Order[50_001]);
            Assert.Equal("Shown", result.Order[^1]);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        }

        // --- Logging of skipped entries (review item 7) ---

        [Fact]
        public void SkippedEntries_AreLoggedByName_AndUnusualStatesWarn()
        {
            var even = Unique("Even");
            var odd = Unique("Odd05");
            _sandbox.SeedRun(even, @"C:\Apps\e.exe");
            _sandbox.SeedApproved(even, Approved(0x06));
            _sandbox.SeedRun(odd, @"C:\Apps\o.exe");
            _sandbox.SeedApproved(odd, Approved(0x05));
            _sandbox.SeedRun("StartupController", "\"C:\\x\\StartupController.exe\" --launch");

            var listed = _service.GetStartupPrograms();

            Assert.Equal(odd, Assert.Single(listed).Name); // 0x05 is odd: disabled in Windows
            var log = ReadLog().Split('\n');
            Assert.Contains(log, l => l.Contains("INFO") && l.Contains($"'{even}'") && l.Contains("enabled in Windows"));
            Assert.Contains(log, l => l.Contains("WARN") && l.Contains($"'{odd}'") && l.Contains("0x05"));
            Assert.Contains(log, l => l.Contains("INFO") && l.Contains("'StartupController'") && l.Contains("own entry"));
            Assert.DoesNotContain(log, l => l.Contains($"'{even}'") && l.Contains("e.exe"));
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
