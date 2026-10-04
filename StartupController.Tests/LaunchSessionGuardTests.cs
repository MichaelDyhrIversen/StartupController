using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // 4.D8: at most one --launch sequence per Windows logon session. Fake key provider (never the real WTS API)
    // and RegistryLaunchSessionStore on the sandbox root. Numbers refer to the plan's test list.
    [Collection(LaunchSessionLogCollection.Name)]
    public sealed class LaunchSessionGuardTests : IDisposable
    {
        private const string Key1 = "v1:2:133700000000000000";
        private const string Key2 = "v1:2:133800000000000000";
        private static readonly string ValueName = AppRegistryPaths.LaunchSessionValueName(2); // Key1 and Key2 are session 2

        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        private LaunchSessionGuard Guard(string key) =>
            new LaunchSessionGuard(new FakeKeyProvider(() => key), new RegistryLaunchSessionStore(_sandbox.Root));

        private object? StoredValue() => _sandbox.ReadValue(RegistrySandbox.AppPath, ValueName);

        private RegistryValueKind? StoredKind() => _sandbox.ReadKind(RegistrySandbox.AppPath, ValueName);

        // Only this class logs the malformed-value warning; its tests run one at a time
        private static int MalformedWarnings() =>
            TestLog.LinesContaining("Ignoring malformed registry value " + ValueName).Count(l => l.Contains("\tWARN\t", StringComparison.Ordinal));

        // 1
        [Fact]
        public void NoValue_Claims_AndWritesTheKeyAsRegSz()
        {
            _sandbox.SeedAppValue("StartToTray", 0, RegistryValueKind.DWord);

            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());

            Assert.Equal(RegistryValueKind.String, StoredKind());
            Assert.Equal(Key1, StoredValue());
        }

        // 2
        [Fact]
        public void SameKey_IsAlreadyLaunched_AndTheValueIsUnchanged()
        {
            _sandbox.SeedAppValue(ValueName, Key1, RegistryValueKind.String);
            var store = new CountingStore(new RegistryLaunchSessionStore(_sandbox.Root));

            var claim = new LaunchSessionGuard(new FakeKeyProvider(() => Key1), store).TryClaim();

            Assert.Equal(LaunchClaim.AlreadyLaunched, claim);
            Assert.Equal(0, store.Writes);
            Assert.Equal(RegistryValueKind.String, StoredKind());
            Assert.Equal(Key1, StoredValue());
        }

        // 3
        [Fact]
        public void EarlierLogon_Claims_AndOverwrites()
        {
            _sandbox.SeedAppValue(ValueName, Key1, RegistryValueKind.String);

            Assert.Equal(LaunchClaim.Claimed, Guard(Key2).TryClaim());

            Assert.Equal(Key2, StoredValue());
        }

        // 4: the parent, then a relaunch-loop child in the same logon session
        [Fact]
        public void TwoClaims_SameKey_ClaimedThenAlreadyLaunched()
        {
            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());
            Assert.Equal(LaunchClaim.AlreadyLaunched, Guard(Key1).TryClaim());
            Assert.Equal(LaunchClaim.AlreadyLaunched, Guard(Key1).TryClaim());
        }

        // 5: sign-out and sign-in, or a reboot
        [Fact]
        public void KeyChangesBetweenClaims_ClaimsBothTimes()
        {
            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());
            Assert.Equal(LaunchClaim.Claimed, Guard(Key2).TryClaim());
            Assert.Equal(Key2, StoredValue());
        }

        // 6
        public static TheoryData<string> MalformedKinds => new TheoryData<string> { "dword", "multi", "expand", "garbage", "long", "trailing-newline", "binary", "other-session" };

        [Theory]
        [MemberData(nameof(MalformedKinds))]
        public void MalformedValue_Claims_RewritesAsRegSz_AndWarnsWithTheNameOnly(string kind)
        {
            var marker = TestLog.Unique("bad");
            switch (kind)
            {
                case "dword": _sandbox.SeedAppValue(ValueName, 7, RegistryValueKind.DWord); break;
                case "multi": _sandbox.SeedAppValue(ValueName, new[] { Key1, marker }, RegistryValueKind.MultiString); break;
                case "expand": _sandbox.SeedAppValue(ValueName, Key1, RegistryValueKind.ExpandString); break;
                case "garbage": _sandbox.SeedAppValue(ValueName, "garbage" + marker, RegistryValueKind.String); break;
                case "long": _sandbox.SeedAppValue(ValueName, new string('9', 490) + marker, RegistryValueKind.String); break;
                case "trailing-newline": _sandbox.SeedAppValue(ValueName, Key1 + "\n", RegistryValueKind.String); break;
                case "binary": _sandbox.SeedAppValue(ValueName, new byte[] { 1, 2, 3 }, RegistryValueKind.Binary); break;
                case "other-session": _sandbox.SeedAppValue(ValueName, "v1:3:133700000000000000", RegistryValueKind.String); break;
            }
            int warningsBefore = MalformedWarnings();

            // Same key as a malformed copy of it: it must not count as "already launched"
            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());

            Assert.Equal(RegistryValueKind.String, StoredKind());
            Assert.Equal(Key1, StoredValue());
            Assert.Equal(warningsBefore + 1, MalformedWarnings());
            Assert.Empty(TestLog.LinesContaining(marker));
        }

        // 7
        public static TheoryData<string> BadProviderKeys => new TheoryData<string>
        {
            "", "garbage", "v2:1:2", "v1:1", "v1::2", "v1:1:2\n", " v1:1:2", "v1:12345678901:2", "v1:1:123456789012345678901", "v1:\u0661:2",
            "v1:4294967296:2" // ten digits, but the session id doesn't fit a DWORD
        };

        [Theory]
        [MemberData(nameof(BadProviderKeys))]
        public void ProviderReturnsInvalidKey_Unavailable_AndNothingIsWritten(string key)
        {
            Assert.Equal(LaunchClaim.Unavailable, Guard(key).TryClaim());
            Assert.Null(StoredValue());
        }

        [Fact]
        public void ProviderReturnsNull_Unavailable()
        {
            var guard = new LaunchSessionGuard(new FakeKeyProvider(() => null!), new RegistryLaunchSessionStore(_sandbox.Root));

            Assert.Equal(LaunchClaim.Unavailable, guard.TryClaim());
            Assert.Null(StoredValue());
        }

        [Fact]
        public void ProviderThrows_Unavailable_LogsTheExceptionType_AndNothingIsWritten()
        {
            var marker = TestLog.Unique("wts");
            var guard = new LaunchSessionGuard(
                new FakeKeyProvider(() => throw new System.ComponentModel.Win32Exception(5, marker)),
                new RegistryLaunchSessionStore(_sandbox.Root));

            Assert.Equal(LaunchClaim.Unavailable, guard.TryClaim());

            Assert.Null(StoredValue());
            Assert.False(_sandbox.KeyExists(RegistrySandbox.AppPath));
            var line = Assert.Single(TestLog.LinesContaining(marker));
            Assert.Contains("\tERROR\t", line, StringComparison.Ordinal);
            Assert.Contains("Win32Exception", line, StringComparison.Ordinal);
        }

        // 8
        [Fact]
        public void StoreReadThrows_Unavailable_AndWriteIsNotCalled()
        {
            var store = new FakeStore { OnRead = () => throw new System.Security.SecurityException("denied") };

            var claim = new LaunchSessionGuard(new FakeKeyProvider(() => Key1), store).TryClaim();

            Assert.Equal(LaunchClaim.Unavailable, claim);
            Assert.Equal(1, store.Reads);
            Assert.Equal(0, store.Writes);
        }

        // 9
        [Fact]
        public void StoreWriteThrows_Unavailable()
        {
            var store = new FakeStore { OnWrite = _ => throw new IOException("disk full") };

            var claim = new LaunchSessionGuard(new FakeKeyProvider(() => Key1), store).TryClaim();

            Assert.Equal(LaunchClaim.Unavailable, claim);
            Assert.Equal(1, store.Writes);
        }

        [Fact]
        public void ReadOnlyRoot_WriteFails_Unavailable_AndNothingIsWritten()
        {
            _sandbox.SeedAppValue("StartToTray", 1, RegistryValueKind.DWord);
            using var readOnly = _sandbox.OpenReadOnlyRoot();

            var claim = new LaunchSessionGuard(new FakeKeyProvider(() => Key1), new RegistryLaunchSessionStore(readOnly)).TryClaim();

            Assert.Equal(LaunchClaim.Unavailable, claim);
            Assert.Null(StoredValue());
        }

        [Fact]
        public void DisposedRoot_ReadFails_Unavailable()
        {
            var root = _sandbox.OpenReadOnlyRoot();
            root.Dispose(); // every access now throws ObjectDisposedException

            Assert.Equal(LaunchClaim.Unavailable, new LaunchSessionGuard(new FakeKeyProvider(() => Key1), new RegistryLaunchSessionStore(root)).TryClaim());
        }

        // 10
        [Fact]
        public void AppKeyMissing_Claims_AndCreatesTheKey()
        {
            Assert.False(_sandbox.KeyExists(RegistrySandbox.AppPath));

            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());

            Assert.True(_sandbox.KeyExists(RegistrySandbox.AppPath));
            Assert.Equal(Key1, StoredValue());
        }

        // 11
        [Fact]
        public void Claim_LeavesEveryOtherAppValueByteIdentical()
        {
            _sandbox.SeedStoredOrder(new[] { "A", "B", "C" }, new[] { "B" });
            _sandbox.SeedAppValue(RegistrySandbox.EnabledFingerprintsValue, new[] { "B|" + new string('a', 64) }, RegistryValueKind.MultiString);
            _sandbox.SeedAppValue(RegistrySandbox.OrderValue, "A;B;C", RegistryValueKind.String);
            _sandbox.SeedAppValue("SilenceNotifications", 1, RegistryValueKind.DWord);
            _sandbox.SeedAppValue("StartToTray", 0, RegistryValueKind.DWord);
            _sandbox.SeedAppValue("LaunchProgramsOnStartup", 1, RegistryValueKind.DWord);
            _sandbox.SeedAppValue("AutoSaveOnChange", 1, RegistryValueKind.DWord);
            _sandbox.SeedAppValue(ValueName, "garbage", RegistryValueKind.String);
            _sandbox.SeedAppValue(AppRegistryPaths.LaunchSessionValueName(3), "v1:3:5", RegistryValueKind.String); // another session
            _sandbox.SeedAppValue("LaunchSession", Key2, RegistryValueKind.String);                             // pre-release single value
            var before = DumpWithout(ValueName);

            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());

            Assert.Equal(before, DumpWithout(ValueName));
            Assert.Equal(10, before.Count);
        }

        // The store itself refuses to write something that isn't a key
        [Fact]
        public void StoreWrite_RejectsAnInvalidKey()
        {
            Assert.Throws<ArgumentException>(() => new RegistryLaunchSessionStore(_sandbox.Root).Write("garbage"));
            Assert.Null(StoredValue());
        }

        // Security L1: console plus RDP of the same user. Each session has its own value, so alternating claims never
        // overwrite each other's record: each session gets exactly one Claimed, then AlreadyLaunched forever.
        [Fact]
        public void TwoSessions_AlternatingClaims_EachClaimsOnce_ThenAlreadyLaunchedForever()
        {
            const string sessionA = "v1:1:133700000000000000";
            const string sessionB = "v1:3:133700000000000555";
            var results = new List<(string Session, LaunchClaim Claim)>();

            for (int round = 0; round < 5; round++)
            {
                results.Add(("A", Guard(sessionA).TryClaim()));
                results.Add(("B", Guard(sessionB).TryClaim()));
            }

            foreach (var session in new[] { "A", "B" })
            {
                var claims = results.Where(r => r.Session == session).Select(r => r.Claim).ToList();
                Assert.Equal(LaunchClaim.Claimed, claims[0]);
                Assert.All(claims.Skip(1), c => Assert.Equal(LaunchClaim.AlreadyLaunched, c));
            }
            Assert.Equal(sessionA, _sandbox.ReadValue(RegistrySandbox.AppPath, AppRegistryPaths.LaunchSessionValueName(1)));
            Assert.Equal(sessionB, _sandbox.ReadValue(RegistrySandbox.AppPath, AppRegistryPaths.LaunchSessionValueName(3)));
        }

        // A write for one session never touches another session's value, even a malformed one
        [Fact]
        public void Claim_NeverTouchesAnotherSessionsValue()
        {
            var other = AppRegistryPaths.LaunchSessionValueName(7);
            _sandbox.SeedAppValue(other, new byte[] { 9, 9, 9 }, RegistryValueKind.Binary);

            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());

            Assert.Equal(RegistryValueKind.Binary, _sandbox.ReadKind(RegistrySandbox.AppPath, other));
            Assert.Equal(new byte[] { 9, 9, 9 }, _sandbox.ReadValue(RegistrySandbox.AppPath, other));
            Assert.Equal(Key1, StoredValue());
        }

        // The pre-release single "LaunchSession" value is never read as a record (and is left alone)
        [Fact]
        public void LegacySingleValue_IsNotARecord_AndIsLeftAlone()
        {
            _sandbox.SeedAppValue("LaunchSession", Key1, RegistryValueKind.String);

            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());

            Assert.Equal(Key1, _sandbox.ReadValue(RegistrySandbox.AppPath, "LaunchSession"));
            Assert.Equal(Key1, StoredValue());
        }

        // The guard reads the record of the session id inside the key, and only that one
        [Fact]
        public void Guard_ReadsTheSessionIdFromTheKey()
        {
            var store = new FakeStore();

            new LaunchSessionGuard(new FakeKeyProvider(() => "v1:42:5"), store).TryClaim();

            Assert.Equal(42u, Assert.Single(store.ReadSessionIds));
        }

        // Security L3: the kind is checked before the data is read, so a large planted REG_BINARY is just malformed
        [Fact]
        public void LargePlantedBinary_IsMalformed_Claims_AndIsRewrittenAsRegSz()
        {
            var big = new byte[1024 * 1024];
            Array.Fill(big, (byte)'v');
            _sandbox.SeedAppValue(ValueName, big, RegistryValueKind.Binary);
            int warningsBefore = MalformedWarnings();

            Assert.Null(new RegistryLaunchSessionStore(_sandbox.Root).Read(2));
            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());

            Assert.Equal(RegistryValueKind.String, StoredKind());
            Assert.Equal(Key1, StoredValue());
            Assert.Equal(warningsBefore + 2, MalformedWarnings()); // the direct Read and the claim's Read
        }

        // The store reads the kind before the data (source order in Read)
        [Fact]
        public void StoreRead_ChecksTheKindBeforeReadingTheData()
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "RegistryLaunchSessionStore.cs"));
            int kind = code.IndexOf("GetValueKind(", StringComparison.Ordinal);
            int data = code.IndexOf(".GetValue(", StringComparison.Ordinal);

            Assert.True(kind >= 0 && data > kind, "GetValueKind must come before GetValue in RegistryLaunchSessionStore");
        }

        // 18: the Launch button never consults the guard
        [Fact]
        public async Task ManualLaunch_WhileTheSessionIsRecorded_StartsOnce()
        {
            _sandbox.SeedAppValue(ValueName, Key1, RegistryValueKind.String);
            var starter = FakeProcessStarter.AllExesExist();
            var runner = new LaunchRunner(new ProgramLauncher(starter, TestHostExe), new FakeNotifier(), new FakeDialog(), launch => Task.FromResult(launch()));

            var result = await runner.LaunchManualAsync(P("Manual", path: @"C:\Apps\manual.exe"));

            Assert.True(result.Success);
            Assert.Single(starter.Started);
            Assert.Equal(Key1, StoredValue());
        }

        private List<string> DumpWithout(string excluded) =>
            _sandbox.ReadAllValues(RegistrySandbox.AppPath)
                .Where(v => !string.Equals(v.Name, excluded, StringComparison.OrdinalIgnoreCase))
                .Select(v => $"{v.Name}|{v.Kind}|{Format(v.Data)}")
                .ToList();

        private static string Format(object? data) => data switch
        {
            byte[] bytes => Convert.ToHexString(bytes),
            string[] strings => string.Join("\u0001", strings),
            null => "<null>",
            _ => data.ToString() ?? ""
        };
    }

    // 4.D8: key text, the WTS key rules and the WTSINFOW layout, without calling the WTS API
    public class LaunchSessionKeyTests
    {
        [Fact]
        public void Format_IsDecimalAndCultureInvariant()
        {
            Assert.Equal("v1:4294967295:9223372036854775807", LaunchSessionKey.Format(uint.MaxValue, long.MaxValue));
            Assert.True(LaunchSessionKey.IsValid(LaunchSessionKey.Format(uint.MaxValue, long.MaxValue)));
        }

        [Theory]
        [InlineData("v1:0:1", true)]
        [InlineData("v1:2:133700000000000000", true)]
        [InlineData("v1:1:2 ", false)]
        [InlineData("V1:1:2", false)]
        [InlineData("v1:-1:2", false)]
        [InlineData("v1:1:-2", false)]
        [InlineData("v1:1:2:3", false)]
        public void IsValid(string key, bool expected)
        {
            Assert.Equal(expected, LaunchSessionKey.IsValid(key));
        }

        [Fact]
        public void IsValid_RejectsNullTrailingNewlineAndNonAsciiDigits()
        {
            Assert.False(LaunchSessionKey.IsValid(null));
            Assert.False(LaunchSessionKey.IsValid("v1:1:2\n"));
            Assert.False(LaunchSessionKey.IsValid("v1:\u0661:2"));   // Arabic-Indic digit one
            Assert.False(LaunchSessionKey.IsValid("v1:1:\uFF12"));   // fullwidth digit two
        }

        [Fact]
        public void KeyFrom_MatchingSessionAndLogonTime_GivesTheKey()
        {
            Assert.Equal("v1:3:133700000000000000", WtsLogonSessionKeyProvider.KeyFrom(3, 133700000000000000, 3));
        }

        [Theory]
        [InlineData(3u, 133700000000000000L, 2)] // another session
        [InlineData(3u, 133700000000000000L, -1)]
        [InlineData(0u, 0L, 0)]                  // session 0 (services) has no logon time
        [InlineData(1u, -5L, 1)]
        public void KeyFrom_WrongSessionOrNoLogonTime_Throws(uint sessionId, long logonTime, int processSessionId)
        {
            Assert.Throws<InvalidOperationException>(() => WtsLogonSessionKeyProvider.KeyFrom(sessionId, logonTime, processSessionId));
        }

        // Checked against WtsApi32.h (SDK 10.0.19041): 8 DWORDs, WCHAR[32], WCHAR[17], WCHAR[21], 5 LARGE_INTEGERs
        [Fact]
        public void WtsInfoLayout_MatchesTheHeader()
        {
            Assert.Equal(216, Marshal.SizeOf<NativeMethods.WTSINFOW>());
            Assert.Equal(4, Marshal.OffsetOf<NativeMethods.WTSINFOW>(nameof(NativeMethods.WTSINFOW.SessionId)).ToInt32());
            Assert.Equal(32, Marshal.OffsetOf<NativeMethods.WTSINFOW>(nameof(NativeMethods.WTSINFOW.WinStationName)).ToInt32());
            Assert.Equal(176, Marshal.OffsetOf<NativeMethods.WTSINFOW>(nameof(NativeMethods.WTSINFOW.ConnectTime)).ToInt32());
            Assert.Equal(200, Marshal.OffsetOf<NativeMethods.WTSINFOW>(nameof(NativeMethods.WTSINFOW.LogonTime)).ToInt32());
        }
    }

    // 4.D8: the pure start decision (12-16)
    public class LaunchGateTests
    {
        private sealed class ClaimSpy
        {
            private readonly LaunchClaim _result;
            public int Calls { get; private set; }

            public ClaimSpy(LaunchClaim result) => _result = result;

            public LaunchClaim Claim()
            {
                Calls++;
                return _result;
            }
        }

        // 12, 13
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void NoLaunchArgOrSettingOff_Normal_AndTheClaimIsNotCalled(bool hasLaunchArg, bool settingOn)
        {
            var spy = new ClaimSpy(LaunchClaim.Claimed);

            Assert.Equal(StartupAction.Normal, LaunchGate.Decide(hasLaunchArg, settingOn, spy.Claim));
            Assert.Equal(0, spy.Calls);
        }

        // 14, 15, 16
        [Theory]
        [InlineData(nameof(LaunchClaim.Claimed), nameof(StartupAction.LaunchAndExit))]
        [InlineData(nameof(LaunchClaim.AlreadyLaunched), nameof(StartupAction.ExitAlreadyLaunched))]
        [InlineData(nameof(LaunchClaim.Unavailable), nameof(StartupAction.BlockedAndExit))]
        public void LaunchWithSettingOn_MapsTheClaim(string claim, string expected)
        {
            // Names, not the enums: they are internal, and a public test method can't take them as parameters
            var spy = new ClaimSpy(Enum.Parse<LaunchClaim>(claim));

            Assert.Equal(Enum.Parse<StartupAction>(expected), LaunchGate.Decide(true, true, spy.Claim));
            Assert.Equal(1, spy.Calls);
        }

        [Fact]
        public void ClaimThrows_FailsClosed()
        {
            Assert.Equal(StartupAction.BlockedAndExit, LaunchGate.Decide(true, true, () => throw new InvalidOperationException("boom")));
        }
    }

    // 4.D8: --launch mode after the load, through StartupSession.RunLaunchModeAsync (17). FakeProcessStarter only.
    [Collection(LaunchSessionLogCollection.Name)]
    public class LaunchModeTests
    {
        // Program.DecideStartup logs the blocked decision; the launch-mode tail must not log it a second time
        private const string BlockedError = Program.BlockedMessage;

        private static LaunchRunner Runner(IProcessStarter starter, INotifier notifier) =>
            new LaunchRunner(new ProgramLauncher(starter, TestHostExe), notifier, new FakeDialog(), launch => Task.FromResult(launch()));

        private static readonly List<StartupProgram> Enabled = new List<StartupProgram>
        {
            P("A", enabled: true, path: @"C:\Apps\a.exe"),
            P("B", enabled: true, path: @"C:\Apps\b.exe")
        };

        private static int BlockedErrors() =>
            TestLog.LinesContaining(BlockedError).Count(l => l.Contains("\tERROR\t", StringComparison.Ordinal));

        // 17
        [Theory]
        [InlineData(false)]
        [InlineData(true)] // silenced notifications still wait, so the blocked balloon is seen
        public async Task Blocked_StartsNothing_NotifiesOnce_WaitsTheNotificationDelay_AndDoesNotRelogTheError(bool silenced)
        {
            var starter = FakeProcessStarter.AllExesExist();
            var notifier = new FakeNotifier();
            var delays = new List<TimeSpan>();
            int errorsBefore = BlockedErrors();

            await StartupSession.RunLaunchModeAsync(Runner(starter, notifier), Enabled, loaded: true, blocked: true, silenced, notifier,
                d => { delays.Add(d); return Task.CompletedTask; });

            Assert.Empty(starter.Started);
            Assert.Equal(StartupSession.LaunchBlockedNotification, Assert.Single(notifier.Messages));
            Assert.Equal(StartupSession.NotificationExitDelay, Assert.Single(delays));
            Assert.Equal(errorsBefore, BlockedErrors());
        }

        [Fact]
        public async Task NotBlocked_LaunchesTheEnabledProgramsInOrder()
        {
            var starter = FakeProcessStarter.AllExesExist();
            var notifier = new FakeNotifier();
            var delays = new List<TimeSpan>();

            await StartupSession.RunLaunchModeAsync(Runner(starter, notifier), Enabled, loaded: true, blocked: false, notificationsSilenced: true, notifier,
                d => { delays.Add(d); return Task.CompletedTask; });

            Assert.Equal(new[] { @"C:\Apps\a.exe", @"C:\Apps\b.exe" }, starter.Started.Select(s => s.FileName));
            Assert.Empty(delays); // silenced and loaded: exits at once, as before 4.D8
        }

        [Fact]
        public async Task LoadFailed_StartsNothing_AndWaits()
        {
            var starter = FakeProcessStarter.AllExesExist();
            var notifier = new FakeNotifier();
            var delays = new List<TimeSpan>();

            await StartupSession.RunLaunchModeAsync(Runner(starter, notifier), Enabled, loaded: false, blocked: false, notificationsSilenced: true, notifier,
                d => { delays.Add(d); return Task.CompletedTask; });

            Assert.Empty(starter.Started);
            Assert.Empty(notifier.Messages); // LoadProgramsAsync already notified the load failure
            Assert.Equal(StartupSession.NotificationExitDelay, Assert.Single(delays));
        }

        // Should 3: blocked and the load failed too. The load doesn't notify (Form1 passes notifyFailure: false for a
        // blocked start), so the user sees exactly one balloon: the blocked one. The load failure is still logged.
        [Fact]
        public async Task BlockedAndLoadFailed_ShowsOnlyTheBlockedBalloon()
        {
            using var sandbox = new RegistrySandbox();
            var root = sandbox.OpenReadOnlyRoot();
            root.Dispose(); // every access throws: the load fails
            var starter = FakeProcessStarter.AllExesExist();
            var notifier = new FakeNotifier();
            var delays = new List<TimeSpan>();
            var model = new StartupListModel();

            bool loaded = await StartupSession.LoadProgramsAsync(new StartupRegistryService(root), model, notifier, takeOver: false,
                read => Task.FromResult(read()), notifyFailure: false);
            await StartupSession.RunLaunchModeAsync(Runner(starter, notifier), model.EnabledPrograms(), loaded, blocked: true,
                notificationsSilenced: false, notifier, d => { delays.Add(d); return Task.CompletedTask; });

            Assert.False(loaded);
            Assert.Empty(starter.Started);
            Assert.Equal(StartupSession.LaunchBlockedNotification, Assert.Single(notifier.Messages));
            Assert.Equal(StartupSession.NotificationExitDelay, Assert.Single(delays));
        }

        // Form1 wires the flag from LaunchBlocked, so the rule above holds in the app
        [Fact]
        public void Form1_SuppressesTheLoadBalloonOnlyForABlockedStart()
        {
            var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "Form1.cs"));

            Assert.Matches(@"LoadProgramsAsync\([^;]*notifyFailure:\s*!LaunchBlocked\s*\)", code);
        }

        [Fact]
        public async Task Blocked_NotifierThrows_StillCompletes()
        {
            var starter = FakeProcessStarter.AllExesExist();

            await StartupSession.RunLaunchModeAsync(Runner(starter, new FakeNotifier()), Enabled, loaded: true, blocked: true, notificationsSilenced: false,
                new ThrowingNotifier(), _ => Task.CompletedTask);

            Assert.Empty(starter.Started);
        }

        private sealed class ThrowingNotifier : INotifier
        {
            public void Notify(string message) => throw new InvalidOperationException("tray gone");
        }
    }

    // 4.D8 source guards (21; 20 is in SandboxGuardTests)
    public class LaunchSessionSourceGuardTests
    {
        [Fact]
        public void Guard_IsUsedOnlyByProgram()
        {
            var offenders = SourceScan.FilesMatching(
                SourceScan.ProductionSourceDirectory(),
                @"\b(LaunchSessionGuard|TryClaim)\b",
                "Program.cs", "LaunchSessionGuard.cs");

            Assert.True(offenders.Count == 0, "LaunchSessionGuard used outside Program.cs: " + string.Join(", ", offenders));
        }

        [Fact]
        public void LaunchRunnerAndForm1_NeverConsultTheGuardOrTheStore()
        {
            foreach (var file in new[] { "LaunchRunner.cs", "Form1.cs" })
            {
                var code = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), file));
                Assert.DoesNotMatch(@"\b(LaunchSessionGuard|TryClaim|ILaunchSessionStore|RegistryLaunchSessionStore|LaunchSessionValueName|LaunchSessionValuePrefix)\b", code);
            }
        }

        // Should 2: the session header is logged by Program right after the mutex is acquired (only in the new
        // instance branch) and before the --launch decision, so an early exit has a header. Form1 no longer logs it.
        [Fact]
        public void SessionHeader_IsLoggedByProgramBeforeTheDecision_AndNotByForm1()
        {
            var program = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "Program.cs"));
            var form = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "Form1.cs"));

            int newInstance = program.IndexOf("if (isNewInstance)", StringComparison.Ordinal);
            int header = program.IndexOf("LoggingService.StartSession(args)", StringComparison.Ordinal);
            int decision = program.IndexOf("DecideStartup(args.Contains(", StringComparison.Ordinal);
            int secondInstance = program.IndexOf("else if (args.Contains(\"--launch\"))", StringComparison.Ordinal);

            Assert.True(newInstance >= 0 && header > newInstance && decision > header && secondInstance > decision,
                "Program.Main must log the header inside the new-instance branch, before DecideStartup");
            Assert.Single(Regex.Matches(program, @"\bStartSession\s*\("));
            Assert.DoesNotMatch(@"\bStartSession\s*\(", form);
        }

        // Should 1: launch mode comes from Program's decision only; Form1 reads the launch setting just for the checkbox
        [Fact]
        public void Form1_DerivesLaunchModeFromTheDecision_NotFromTheSetting()
        {
            var form = SourceScan.ReadCode(Path.Combine(SourceScan.ProductionSourceDirectory(), "Form1.cs"));

            Assert.Single(Regex.Matches(form, @"\bGetLaunchProgramsOnStartup\s*\("));
            Assert.Matches(@"chkLaunchProgramsOnStartup\.Checked\s*=\s*_settings\.GetLaunchProgramsOnStartup\(\)", form);
            Assert.Matches(@"LaunchGate\.FormFlagsFor\(StartupAction\)", form);
        }

        [Fact]
        public void RealWtsProvider_IsCreatedOnlyByProgram_AndInTestsOnlyByTheSmokeTest()
        {
            const string pattern = @"new\s+(?:\w+\s*\.\s*)*" + "WtsLogonSessionKeyProvider" + @"\s*\(";

            var production = SourceScan.FilesMatching(SourceScan.ProductionSourceDirectory(), pattern, "Program.cs");
            var tests = SourceScan.FilesMatching(SourceScan.TestSourceDirectory(), pattern, "WtsLogonSessionKeyProviderTests.cs");

            Assert.True(production.Count == 0, "WtsLogonSessionKeyProvider created outside Program.cs: " + string.Join(", ", production));
            Assert.True(tests.Count == 0, "Real WTS provider used in tests outside the smoke test: " + string.Join(", ", tests));
        }
    }

    internal sealed class FakeKeyProvider : ILogonSessionKeyProvider
    {
        private readonly Func<string> _key;

        public FakeKeyProvider(Func<string> key) => _key = key;

        public string GetCurrentKey() => _key();
    }

    internal sealed class FakeStore : ILaunchSessionStore
    {
        public Func<string?> OnRead { get; set; } = () => null;
        public List<uint> ReadSessionIds { get; } = new List<uint>();
        public Action<string> OnWrite { get; set; } = _ => { };
        public int Reads { get; private set; }
        public int Writes { get; private set; }

        public string? Read(uint sessionId)
        {
            Reads++;
            ReadSessionIds.Add(sessionId);
            return OnRead();
        }

        public void Write(string key)
        {
            Writes++;
            OnWrite(key);
        }
    }

    internal sealed class CountingStore : ILaunchSessionStore
    {
        private readonly ILaunchSessionStore _inner;

        public CountingStore(ILaunchSessionStore inner) => _inner = inner;

        public int Writes { get; private set; }

        public string? Read(uint sessionId) => _inner.Read(sessionId);

        public void Write(string key)
        {
            Writes++;
            _inner.Write(key);
        }
    }
}
