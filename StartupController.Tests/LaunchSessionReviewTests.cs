using System.Runtime.InteropServices;
using Microsoft.Win32;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // The log lines these tests count (the malformed-value Warning, the blocked Error) carry no per-test marker, so
    // every test class that can produce them is in this one collection and runs one test at a time.
    internal static class LaunchSessionLogCollection
    {
        public const string Name = "LaunchSessionLog";
    }

    // 4.D8 review (tester): edge cases of the guard, the key rules and the layout, and the fail-closed chain
    // from Program.DecideStartup down to the launch-mode tail. Sandbox registry and fakes only.
    [Collection(LaunchSessionLogCollection.Name)]
    public sealed class LaunchSessionEdgeCaseTests : IDisposable
    {
        private const string Key1 = "v1:2:133700000000000000";
        private static readonly string ValueName = AppRegistryPaths.LaunchSessionValueName(2); // the keys below are session 2

        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        private LaunchSessionGuard Guard(string key) =>
            new LaunchSessionGuard(new FakeKeyProvider(() => key), new RegistryLaunchSessionStore(_sandbox.Root));

        private static int MalformedWarnings() =>
            TestLog.LinesContaining("Ignoring malformed registry value " + ValueName).Count(l => l.Contains("\tWARN\t", StringComparison.Ordinal));

        // --- key rules ---

        [Theory]
        [InlineData(1u, 0L)]
        [InlineData(1u, -1L)]
        [InlineData(1u, long.MinValue)]
        public void KeyFrom_ZeroOrNegativeLogonTime_Throws(uint session, long logonTime)
        {
            Assert.Throws<InvalidOperationException>(() => WtsLogonSessionKeyProvider.KeyFrom(session, logonTime, (int)session));
        }

        [Fact]
        public void KeyFrom_LargestValues_StayInsideTheKeyRules()
        {
            // uint.MaxValue as a process session id arrives as -1 (int): refused; the largest real id is int.MaxValue
            var key = WtsLogonSessionKeyProvider.KeyFrom(int.MaxValue, long.MaxValue, int.MaxValue);

            Assert.Equal("v1:2147483647:9223372036854775807", key);
            Assert.True(LaunchSessionKey.IsValid(key));
            Assert.Throws<InvalidOperationException>(() => WtsLogonSessionKeyProvider.KeyFrom(uint.MaxValue, 5, -1));
        }

        [Fact]
        public void KeyFrom_SessionIdMismatch_Throws_EvenWithAValidLogonTime()
        {
            Assert.Throws<InvalidOperationException>(() => WtsLogonSessionKeyProvider.KeyFrom(2, 5, 3));
            Assert.Throws<InvalidOperationException>(() => WtsLogonSessionKeyProvider.KeyFrom(3, 5, 2));
        }

        [Theory]
        [InlineData("v1:1:2:3")]       // extra colon
        [InlineData("v1::")]
        [InlineData("v1:1:")]
        [InlineData("v1:1:2\0")]       // embedded NUL at the end
        [InlineData("v1:1\0:2")]       // embedded NUL in the middle
        [InlineData("v1:1:2\r\n")]
        [InlineData("\0v1:1:2")]
        [InlineData("v2:1:2")]         // a newer format
        [InlineData("v0:1:2")]         // an older format
        [InlineData("v1:1:2;v1:1:3")]
        public void IsValid_RejectsOddShapes(string key)
        {
            Assert.False(LaunchSessionKey.IsValid(key));
        }

        [Fact]
        public void IsValid_Accepts64Max_AndRejectsLongerEvenIfDigits()
        {
            Assert.True(LaunchSessionKey.IsValid("v1:" + new string('1', 10) + ":" + new string('2', 20)));
            Assert.False(LaunchSessionKey.IsValid("v1:" + new string('1', 11) + ":" + new string('2', 20)));
            Assert.False(LaunchSessionKey.IsValid("v1:" + new string('1', 10) + ":" + new string('2', 21)));
            Assert.False(LaunchSessionKey.IsValid("v1:1:" + new string('2', 70))); // also over MaxLength
        }

        // A leading zero is valid text but a different key: comparison is Ordinal, not numeric. Windows never
        // formats ids with leading zeros, so this only means one extra run if someone hand-edits the value.
        [Fact]
        public void LeadingZero_IsAValidButDifferentKey()
        {
            Assert.True(LaunchSessionKey.IsValid("v1:02:5"));
            _sandbox.SeedAppValue(ValueName, "v1:02:5", RegistryValueKind.String);

            Assert.Equal(LaunchClaim.Claimed, Guard("v1:2:5").TryClaim());
            Assert.Equal("v1:2:5", _sandbox.ReadValue(RegistrySandbox.AppPath, ValueName));
        }

        // --- stored value edge cases ---

        [Theory]
        [InlineData("v2:2:133700000000000000")] // written by a newer build
        [InlineData("v1:2:133700000000000000:x")]
        [InlineData("v1:1:2\n")]
        public void StoredValueOfAnotherFormat_IsTreatedAsNoRecord_AndRewrittenAsV1(string stored)
        {
            _sandbox.SeedAppValue(ValueName, stored, RegistryValueKind.String);
            int before = MalformedWarnings();

            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());

            Assert.Equal(Key1, _sandbox.ReadValue(RegistrySandbox.AppPath, ValueName));
            Assert.Equal(RegistryValueKind.String, _sandbox.ReadKind(RegistrySandbox.AppPath, ValueName));
            Assert.Equal(before + 1, MalformedWarnings());
        }

        // The runtime may cut a REG_SZ at the first NUL or hand it over whole; either way the guard must not
        // throw, must not treat junk as "already launched", and must leave a valid REG_SZ behind.
        [Fact]
        public void StoredValueWithEmbeddedNul_NeverThrows_AndEndsWithAValidRecord()
        {
            _sandbox.SeedAppValue(ValueName, "v1:9:9\0junk", RegistryValueKind.String);

            var claim = Guard(Key1).TryClaim(); // Key1 differs from both "v1:9:9" and the whole text

            Assert.Equal(LaunchClaim.Claimed, claim);
            Assert.Equal(Key1, _sandbox.ReadValue(RegistrySandbox.AppPath, ValueName));
        }

        [Fact]
        public void StoredValueWithEmbeddedNul_AfterTheKey_IsNotAlreadyLaunchedUnlessTheRuntimeTruncatesIt()
        {
            _sandbox.SeedAppValue(ValueName, Key1 + "\0junk", RegistryValueKind.String);

            var claim = Guard(Key1).TryClaim();

            // Truncated to exactly the key: same session. Whole text: malformed, so Claimed. Both are safe.
            Assert.True(claim is LaunchClaim.AlreadyLaunched or LaunchClaim.Claimed, claim.ToString());
            Assert.Equal(Key1, _sandbox.ReadValue(RegistrySandbox.AppPath, ValueName));
        }

        [Fact]
        public void ProviderKeyWithEmbeddedNul_Unavailable_NothingWritten()
        {
            Assert.Equal(LaunchClaim.Unavailable, Guard(Key1 + "\0").TryClaim());
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, ValueName));
        }

        [Fact]
        public void EmptyStoredString_IsNoRecord()
        {
            _sandbox.SeedAppValue(ValueName, "", RegistryValueKind.String);

            Assert.Equal(LaunchClaim.Claimed, Guard(Key1).TryClaim());
        }

        // --- atomicity of the guard itself ---

        // Documents a limit: TryClaim is check-then-write, not atomic. Both threads read "no record" before either
        // writes, so both get Claimed. In the app, Program.Main holds StartupControllerSingletonMutex around the claim.
        [Fact]
        public void TwoConcurrentClaims_WithoutAnExternalLock_CanBothBeClaimed()
        {
            using var barrier = new Barrier(2);
            var inner = new RegistryLaunchSessionStore(_sandbox.Root);
            var store = new BarrierStore(inner, barrier);

            var results = RunTwo(() => new LaunchSessionGuard(new FakeKeyProvider(() => Key1), store).TryClaim());

            Assert.Equal(new[] { LaunchClaim.Claimed, LaunchClaim.Claimed }, results);
        }

        // What Program.Main does: serialize the claims. Exactly one wins, however the threads interleave.
        [Fact]
        public void TwoConcurrentClaims_UnderTheCallersLock_ExactlyOneIsClaimed()
        {
            var gate = new object();
            for (int i = 0; i < 25; i++)
            {
                var key = $"v1:2:{1337 + i}";
                var store = new RegistryLaunchSessionStore(_sandbox.Root);

                var results = RunTwo(() =>
                {
                    lock (gate) return new LaunchSessionGuard(new FakeKeyProvider(() => key), store).TryClaim();
                });

                Assert.Equal(1, results.Count(r => r == LaunchClaim.Claimed));
                Assert.Equal(1, results.Count(r => r == LaunchClaim.AlreadyLaunched));
            }
        }

        // Without a lock and without forced interleaving: never throws, at least one claim, valid record
        [Fact]
        public void ManyConcurrentClaims_NeverThrow_AndLeaveAValidRecord()
        {
            var store = new RegistryLaunchSessionStore(_sandbox.Root);
            var results = new LaunchClaim[8];
            var threads = Enumerable.Range(0, results.Length).Select(i => new Thread(() =>
                results[i] = new LaunchSessionGuard(new FakeKeyProvider(() => Key1), store).TryClaim())).ToList();

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Contains(LaunchClaim.Claimed, results);
            Assert.DoesNotContain(LaunchClaim.Unavailable, results);
            Assert.Equal(Key1, _sandbox.ReadValue(RegistrySandbox.AppPath, ValueName));
        }

        private static LaunchClaim[] RunTwo(Func<LaunchClaim> claim)
        {
            var results = new LaunchClaim[2];
            var threads = new[]
            {
                new Thread(() => results[0] = claim()),
                new Thread(() => results[1] = claim())
            };
            foreach (var t in threads) t.Start();
            foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromSeconds(30)), "claim thread hung");
            return results;
        }

        // Both threads finish their read before either writes
        private sealed class BarrierStore : ILaunchSessionStore
        {
            private readonly ILaunchSessionStore _inner;
            private readonly Barrier _barrier;

            public BarrierStore(ILaunchSessionStore inner, Barrier barrier)
            {
                _inner = inner;
                _barrier = barrier;
            }

            public string? Read(uint sessionId)
            {
                var value = _inner.Read(sessionId);
                Assert.True(_barrier.SignalAndWait(TimeSpan.FromSeconds(30)), "barrier timed out");
                return value;
            }

            public void Write(string key) => _inner.Write(key);
        }

        // --- WTSINFOW layout against WtsApi32.h (SDK 10.0.19041, read from the header) ---

        [Theory]
        [InlineData(nameof(NativeMethods.WTSINFOW.State), 0)]
        [InlineData(nameof(NativeMethods.WTSINFOW.SessionId), 4)]
        [InlineData(nameof(NativeMethods.WTSINFOW.IncomingBytes), 8)]
        [InlineData(nameof(NativeMethods.WTSINFOW.OutgoingBytes), 12)]
        [InlineData(nameof(NativeMethods.WTSINFOW.IncomingFrames), 16)]
        [InlineData(nameof(NativeMethods.WTSINFOW.OutgoingFrames), 20)]
        [InlineData(nameof(NativeMethods.WTSINFOW.IncomingCompressedBytes), 24)]
        [InlineData(nameof(NativeMethods.WTSINFOW.OutgoingCompressedBytes), 28)]
        [InlineData(nameof(NativeMethods.WTSINFOW.WinStationName), 32)]  // WCHAR[32]  -> 64 bytes
        [InlineData(nameof(NativeMethods.WTSINFOW.Domain), 96)]          // WCHAR[17]  -> 34 bytes
        [InlineData(nameof(NativeMethods.WTSINFOW.UserName), 130)]       // WCHAR[21]  -> 42 bytes, ends at 172
        [InlineData(nameof(NativeMethods.WTSINFOW.ConnectTime), 176)]    // LARGE_INTEGER: 8-aligned
        [InlineData(nameof(NativeMethods.WTSINFOW.DisconnectTime), 184)]
        [InlineData(nameof(NativeMethods.WTSINFOW.LastInputTime), 192)]
        [InlineData(nameof(NativeMethods.WTSINFOW.LogonTime), 200)]
        [InlineData(nameof(NativeMethods.WTSINFOW.CurrentTime), 208)]
        public void WtsInfoLayout_EveryFieldOffset(string field, int offset)
        {
            Assert.Equal(offset, Marshal.OffsetOf<NativeMethods.WTSINFOW>(field).ToInt32());
        }

        [Fact]
        public void WtsConstants_MatchTheHeader()
        {
            Assert.Equal(32, NativeMethods.WINSTATIONNAME_LENGTH);
            Assert.Equal(17, NativeMethods.DOMAIN_LENGTH);
            Assert.Equal(20, NativeMethods.USERNAME_LENGTH);
            Assert.Equal(24, NativeMethods.WTSSessionInfo);        // WTS_INFO_CLASS: WTSSessionInfo is the 25th enumerator
            Assert.Equal(uint.MaxValue, NativeMethods.WTS_CURRENT_SESSION);
            Assert.Equal(IntPtr.Zero, NativeMethods.WTS_CURRENT_SERVER_HANDLE);
        }

        // PtrToStructure on a hand-built unmanaged buffer: the logon time is read from byte 200, the id from byte 4
        [Fact]
        public void WtsInfo_ReadsLogonTimeAndSessionIdFromTheDocumentedOffsets()
        {
            int size = Marshal.SizeOf<NativeMethods.WTSINFOW>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, 4, 7);
                Marshal.WriteInt64(buffer, 200, 133700000000000123);
                Marshal.WriteInt64(buffer, 208, 42);

                var info = Marshal.PtrToStructure<NativeMethods.WTSINFOW>(buffer);

                Assert.Equal(7u, info.SessionId);
                Assert.Equal(133700000000000123, info.LogonTime);
                Assert.Equal(42, info.CurrentTime);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // 4.D8 review: Program.DecideStartup through its test seam (no real WTS, no real HKCU), and the whole
    // fail-closed chain down to StartupSession.RunLaunchModeAsync.
    [Collection(LaunchSessionLogCollection.Name)]
    public sealed class LaunchDecisionWiringTests : IDisposable
    {
        private const string Key1 = "v1:2:133700000000000000";

        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        private sealed class StubSettings : IUserSettings
        {
            public Func<bool> LaunchOnStartup { get; set; } = () => true;
            public int LaunchReads { get; private set; }
            public bool GetLaunchProgramsOnStartup() { LaunchReads++; return LaunchOnStartup(); }

            public bool GetSilenceNotifications() => false;
            public void SetSilenceNotifications(bool value) { }
            public bool GetStartToTray() => false;
            public void SetStartToTray(bool value) { }
            public void SetLaunchProgramsOnStartup(bool value) { }
            public bool GetAutoSaveOnChange() => false;
            public void SetAutoSaveOnChange(bool value) { }
        }

        private sealed class ClaimSpy
        {
            private readonly Func<LaunchClaim> _claim;
            public int Calls { get; private set; }
            public ClaimSpy(LaunchClaim result) : this(() => result) { }
            public ClaimSpy(Func<LaunchClaim> claim) => _claim = claim;
            public LaunchClaim Claim() { Calls++; return _claim(); }
        }

        // The mapping Form1 applies to Program.Main's decision (the production one, not a copy)
        private static (bool LaunchMode, bool LaunchBlocked) FormFlags(StartupAction action) => LaunchGate.FormFlagsFor(action);

        private static int ErrorLines(string text) =>
            TestLog.LinesContaining(text).Count(l => l.Contains("\tERROR\t", StringComparison.Ordinal));

        [Fact]
        public void NoLaunchArg_NeverReadsTheSetting_NorClaims()
        {
            var settings = new StubSettings();
            var spy = new ClaimSpy(LaunchClaim.Claimed);

            Assert.Equal(StartupAction.Normal, Program.DecideStartup(false, settings, spy.Claim));

            Assert.Equal(0, settings.LaunchReads);
            Assert.Equal(0, spy.Calls);
        }

        [Fact]
        public void SettingOff_Normal_NoClaim()
        {
            var settings = new StubSettings { LaunchOnStartup = () => false };
            var spy = new ClaimSpy(LaunchClaim.Claimed);

            Assert.Equal(StartupAction.Normal, Program.DecideStartup(true, settings, spy.Claim));
            Assert.Equal(0, spy.Calls);
        }

        // An unreadable setting fails closed like every other failure (Q-D8a): blocked, so nothing is launched, the
        // blocked balloon shows and the app exits after the delay. No window pops up at login. The claim isn't made.
        [Fact]
        public void SettingReadThrows_IsBlocked_NoClaim_LogsTheCauseAndTheDecision_AndTheFormIsInBlockedLaunchMode()
        {
            var marker = TestLog.Unique("setting");
            var settings = new StubSettings { LaunchOnStartup = () => throw new IOException(marker) };
            var spy = new ClaimSpy(LaunchClaim.Claimed);
            int blockedBefore = ErrorLines(Program.BlockedMessage);

            var action = Program.DecideStartup(true, settings, spy.Claim);

            Assert.Equal(StartupAction.BlockedAndExit, action);
            Assert.Equal(0, spy.Calls);
            Assert.Equal((true, true), FormFlags(action));
            var line = Assert.Single(TestLog.LinesContaining(marker));
            Assert.Contains("\tERROR\t", line, StringComparison.Ordinal);
            Assert.Equal(blockedBefore + 1, ErrorLines(Program.BlockedMessage));
        }

        // Should 4: every --launch start logs its outcome in exactly one line
        [Theory]
        [InlineData(nameof(LaunchClaim.Claimed), "--launch: launching the startup programs")]
        [InlineData(nameof(LaunchClaim.AlreadyLaunched), Program.AlreadyLaunchedMessage)]
        [InlineData(nameof(LaunchClaim.Unavailable), Program.BlockedMessage)]
        public void LaunchWithSettingOn_LogsTheDecisionOnce(string claim, string decisionLine)
        {
            int before = TestLog.LinesContaining(decisionLine).Count;

            Program.DecideStartup(true, new StubSettings(), new ClaimSpy(Enum.Parse<LaunchClaim>(claim)).Claim);

            Assert.Equal(before + 1, TestLog.LinesContaining(decisionLine).Count);
        }

        [Fact]
        public void SettingOff_LogsThatNothingIsLaunched_NoLaunchArg_LogsNothing()
        {
            const string off = "--launch: \"Launch programs on startup\" is off; nothing is launched";
            int before = TestLog.LinesContaining(off).Count;

            Program.DecideStartup(true, new StubSettings { LaunchOnStartup = () => false }, new ClaimSpy(LaunchClaim.Claimed).Claim);
            Program.DecideStartup(false, new StubSettings(), new ClaimSpy(LaunchClaim.Claimed).Claim);

            Assert.Equal(before + 1, TestLog.LinesContaining(off).Count);
        }

        [Theory]
        [InlineData(nameof(LaunchClaim.Claimed), nameof(StartupAction.LaunchAndExit), true, false)]
        [InlineData(nameof(LaunchClaim.AlreadyLaunched), nameof(StartupAction.ExitAlreadyLaunched), false, false)]
        [InlineData(nameof(LaunchClaim.Unavailable), nameof(StartupAction.BlockedAndExit), true, true)]
        public void LaunchWithSettingOn_MapsToTheFormFlags(string claim, string expected, bool launchMode, bool blocked)
        {
            var action = Program.DecideStartup(true, new StubSettings(), new ClaimSpy(Enum.Parse<LaunchClaim>(claim)).Claim);

            Assert.Equal(Enum.Parse<StartupAction>(expected), action);
            Assert.Equal((launchMode, blocked), FormFlags(action));
        }

        // Every way the claim can fail ends the same way: nothing started, the blocked balloon once, the exit delay.
        public static TheoryData<string> Failures => new TheoryData<string>
        {
            "provider-throws", "provider-empty", "provider-invalid", "store-read-throws", "store-write-throws",
            "claim-throws", "readonly-registry", "disposed-registry"
        };

        [Theory]
        [MemberData(nameof(Failures))]
        public async Task EveryClaimFailure_LaunchesNothing_ShowsTheBalloon_AndAppliesTheDelay(string failure)
        {
            Func<LaunchClaim> claim = failure switch
            {
                "provider-throws" => () => Guard(() => throw new System.ComponentModel.Win32Exception(5), new FakeStore()),
                "provider-empty" => () => Guard(() => "", new FakeStore()),
                "provider-invalid" => () => Guard(() => "v2:1:1", new FakeStore()),
                "store-read-throws" => () => Guard(() => Key1, new FakeStore { OnRead = () => throw new IOException() }),
                "store-write-throws" => () => Guard(() => Key1, new FakeStore { OnWrite = _ => throw new UnauthorizedAccessException() }),
                "claim-throws" => () => throw new InvalidOperationException("backstop"),
                "readonly-registry" => () => Guard(() => Key1, new RegistryLaunchSessionStore(_sandbox.OpenReadOnlyRoot())),
                "disposed-registry" => () =>
                {
                    var root = _sandbox.OpenReadOnlyRoot();
                    root.Dispose();
                    return Guard(() => Key1, new RegistryLaunchSessionStore(root));
                }
                ,
                _ => throw new ArgumentException(failure)
            };

            var action = Program.DecideStartup(true, new StubSettings(), claim);
            Assert.Equal(StartupAction.BlockedAndExit, action);
            var flags = FormFlags(action);
            Assert.True(flags.LaunchMode);
            Assert.True(flags.LaunchBlocked);

            var starter = FakeProcessStarter.AllExesExist();
            var notifier = new FakeNotifier();
            var delays = new List<TimeSpan>();
            var runner = new LaunchRunner(new ProgramLauncher(starter, TestHostExe), notifier, new FakeDialog(), launch => Task.FromResult(launch()));
            var enabled = new List<StartupProgram> { P("A", enabled: true), P("B", enabled: true) };

            await StartupSession.RunLaunchModeAsync(runner, enabled, loaded: true, flags.LaunchBlocked, notificationsSilenced: true, notifier,
                d => { delays.Add(d); return Task.CompletedTask; });

            Assert.Empty(starter.Started);
            Assert.Equal(StartupSession.LaunchBlockedNotification, Assert.Single(notifier.Messages));
            Assert.Equal(StartupSession.NotificationExitDelay, Assert.Single(delays));
        }

        [Fact]
        public void NoFailurePath_WritesTheRecord_WhenTheKeyIsUnavailable()
        {
            var store = new RegistryLaunchSessionStore(_sandbox.Root);

            var action = Program.DecideStartup(true, new StubSettings(),
                () => new LaunchSessionGuard(new FakeKeyProvider(() => throw new InvalidOperationException("no WTS")), store).TryClaim());

            Assert.Equal(StartupAction.BlockedAndExit, action);
            Assert.Null(_sandbox.ReadValue(RegistrySandbox.AppPath, AppRegistryPaths.LaunchSessionValueName(2)));
        }

        private static LaunchClaim Guard(Func<string> key, ILaunchSessionStore store) =>
            new LaunchSessionGuard(new FakeKeyProvider(key), store).TryClaim();
    }
}
