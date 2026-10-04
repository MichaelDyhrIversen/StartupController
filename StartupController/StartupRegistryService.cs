using Microsoft.Win32;

namespace StartupController
{
    public class StartupRegistryService : IStartupRegistry
    {
        // Registry paths
        private const string RUN_KEY = AppRegistryPaths.RunKey;
        private const string STARTUP_APPROVED_KEY = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string APP_ORDER_KEY = AppRegistryPaths.AppKey;
        private const string STARTUP_CONTROLLER_NAME = AppRegistryPaths.AppRunValueName;

        // Order storage (REG_MULTI_SZ). EnabledPrograms and EnabledFingerprints are written first and
        // ProgramOrder last: migration counts as done only once ProgramOrder exists.
        internal const string PROGRAM_ORDER_VALUE = "ProgramOrder";
        internal const string ENABLED_PROGRAMS_VALUE = "EnabledPrograms";
        internal const string ENABLED_FINGERPRINTS_VALUE = "EnabledFingerprints"; // "name|sha256hex" (D7)

        // Legacy REG_SZ, ';'-joined enabled names (701437a). Read for migration only; never written or deleted.
        internal const string LEGACY_ORDER_VALUE = "StartupOrder";

        // REG_MULTI_SZ: names the takeover disabled in Windows, for the uninstaller (D-T3). A UI save never touches it.
        internal const string TAKEN_OVER_VALUE = TakeoverRecord.ValueName;

        // Caps for stored lists, applied on read and on write
        internal const int MAX_NAMES = TakeoverRecord.MaxNames;
        internal const int MAX_NAME_LENGTH = TakeoverRecord.MaxNameLength;

        // StartupApproved first bytes seen from Windows; anything else is logged
        private static readonly byte[] KnownApprovedStates = { 0x01, 0x02, 0x03, 0x06, 0x07 };

        // All key paths are relative to this root (HKCU in the app, a sandbox key in tests)
        private readonly RegistryKey _root;

        // Clock for the FILETIME in the StartupApproved values the takeover writes (tests pin it)
        private readonly Func<DateTime> _utcNow;

        // True when a parsed executable path is this running StartupController (tests substitute a fake app path)
        private readonly Func<string, bool> _isThisApp;

        public StartupRegistryService() : this(Registry.CurrentUser)
        {
        }

        public StartupRegistryService(RegistryKey root) : this(root, () => DateTime.UtcNow)
        {
        }

        public StartupRegistryService(RegistryKey root, Func<DateTime> utcNow) : this(root, utcNow, IsThisAppExe)
        {
        }

        public StartupRegistryService(RegistryKey root, Func<DateTime> utcNow, Func<string, bool> isThisApp)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _isThisApp = isThisApp ?? throw new ArgumentNullException(nameof(isThisApp));
        }

        // The default for _isThisApp: the path is this process's executable, after normalization (device prefixes,
        // relative segments, 8.3 names) or as the same file on disk (hardlinks, subst, UNC aliases), the same path
        // logic the self-launch guard uses (PathHelper). Never throws.
        internal static bool IsThisAppExe(string exePath)
        {
            try
            {
                var self = Environment.ProcessPath;
                if (string.IsNullOrEmpty(self) || string.IsNullOrWhiteSpace(exePath)) return false;
                var candidate = PathHelper.StripDevicePrefix(exePath.Trim());
                if (!CommandLineParser.IsFullyQualified(candidate)) return false;
                return string.Equals(PathHelper.NormalizePath(candidate), PathHelper.NormalizePath(self), StringComparison.OrdinalIgnoreCase)
                    || PathHelper.IsSameFile(candidate, self);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Why StartupController's own Run entry can't start the taken-over programs at logon, or null when it can.
        // Shared by the takeover gate (D-T1) and the stranded warning (D-T6): the value must be REG_SZ or
        // REG_EXPAND_SZ, its parsed executable must be this app, its arguments must include --launch, and it must be
        // enabled in Windows. Anything else counts as missing.
        internal string? OwnEntryProblem(RegistryKey? runKey, RegistryKey? approvedKey)
        {
            if (runKey == null) return "missing";
            var (data, kind) = ReadRunValue(runKey, STARTUP_CONTROLLER_NAME);
            if (data == null) return "missing";
            if (data is not string raw || (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString))
                return "not a string value";

            CommandLineParser.Parsed parsed;
            try
            {
                parsed = CommandLineParser.Parse(raw, File.Exists, expandEnvironment: kind == RegistryValueKind.ExpandString);
            }
            catch (Exception)
            {
                return "not a valid command";
            }
            if (string.IsNullOrEmpty(parsed.ExePath) || !_isThisApp(parsed.ExePath))
                return "not this StartupController";
            if (!parsed.Arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("--launch", StringComparer.Ordinal))
                return "without --launch";
            if (!StartupApprovedState.IsEnabled(approvedKey?.GetValue(STARTUP_CONTROLLER_NAME) as byte[]))
                return "disabled in Windows";
            return null;
        }

        // Run entries that Windows has disabled (the ones StartupController may launch), merged with the stored order.
        // Entries Windows runs itself are listed only once TakeOverWindowsEntries has disabled them in Windows; until
        // then (takeover gate closed, or a takeover that failed) they are skipped, like the app's own entry and
        // non-string Run values (which can't be fingerprinted), so nothing starts twice. This never writes.
        // Stored-enabled entries whose Run data changed come back as Changed and are not launched (D7).
        public List<StartupProgram> GetStartupPrograms()
        {
            var programs = new List<StartupProgram>();

            using (var runKey = _root.OpenSubKey(RUN_KEY, false))
            using (var approvedKey = _root.OpenSubKey(STARTUP_APPROVED_KEY, false))
            {
                if (runKey == null)
                    return programs;

                foreach (var name in runKey.GetValueNames())
                {
                    if (string.Equals(name, STARTUP_CONTROLLER_NAME, StringComparison.OrdinalIgnoreCase))
                    {
                        LoggingService.LogInfo($"Skipping '{name}': StartupController's own entry");
                        continue;
                    }

                    if (name.IndexOf('\0') >= 0)
                    {
                        // GetValue would read a different value (the name cut at the NUL), so it can't be fingerprinted
                        LoggingService.LogWarning($"Skipping '{name}': the Run value name contains a NUL");
                        continue;
                    }

                    var approved = approvedKey?.GetValue(name) as byte[];
                    if (approved == null || approved.Length == 0)
                    {
                        LoggingService.LogInfo($"Skipping '{name}': no StartupApproved value, Windows runs it");
                        continue;
                    }

                    if (!KnownApprovedStates.Contains(approved[0]) && !approved.All(b => b == 0))
                        LoggingService.LogWarning($"Unexpected StartupApproved state 0x{approved[0]:X2} for '{name}'");

                    if (StartupApprovedState.IsEnabled(approved))
                    {
                        LoggingService.LogInfo($"Skipping '{name}': enabled in Windows, Windows runs it");
                        continue;
                    }

                    // One raw read: Path and fingerprint both come from it, so what is launched is what was hashed
                    var (data, kind) = ReadRunValue(runKey, name);
                    if (data is not string raw || (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString))
                    {
                        LoggingService.LogWarning($"Skipping '{name}': Run value is not a string and can't be fingerprinted");
                        continue;
                    }

                    var (path, fingerprint) = RunFingerprint.FromRunValue(kind, raw);
                    programs.Add(new StartupProgram
                    {
                        Name = name,
                        Path = path,
                        Enabled = false,
                        Description = "", // Optionally fetch description from file or elsewhere
                        Fingerprint = fingerprint,
                        PathExpanded = kind == RegistryValueKind.ExpandString
                    });
                }
            }

            var merged = OrderMerger.Merge(programs, LoadStoredOrder());
            foreach (var changed in merged.Where(p => p.Changed))
                LoggingService.LogWarning($"'{changed.Name}' changed since it was enabled; not launched until re-enabled");
            return merged;
        }

        // Save the launch list: EnabledPrograms, EnabledFingerprints, then ProgramOrder.
        // The legacy StartupOrder value is left untouched.
        public void SaveStartupOrder(StoredOrder displayed)
        {
            WriteStoredOrder(OrderMerger.MergeForSave(displayed, LoadStoredOrder()));
        }

        // Caps the order, then writes EnabledPrograms, EnabledFingerprints and ProgramOrder (last). Shared by the UI
        // save and the takeover.
        private void WriteStoredOrder(StoredOrder order)
        {
            var toWrite = Cap(order);
            var enabled = toWrite.EnabledInOrder();
            var fingerprints = enabled
                .Where(toWrite.Fingerprints.ContainsKey)
                .Select(n => RunFingerprint.FormatLine(n, toWrite.Fingerprints[n]))
                .ToArray();

            using (var appKey = _root.CreateSubKey(APP_ORDER_KEY, writable: true))
            {
                WriteMultiString(appKey, ENABLED_PROGRAMS_VALUE, enabled.ToArray());
                WriteMultiString(appKey, ENABLED_FINGERPRINTS_VALUE, fingerprints);
                WriteMultiString(appKey, PROGRAM_ORDER_VALUE, toWrite.Order.ToArray());
            }
        }

        // See IStartupRegistry.TakeOverWindowsEntries. Every failure is logged and leaves Windows starting the entry:
        // the order and the takeover record are written before StartupApproved, a failed order or record write stops
        // before any StartupApproved write, and right before those writes the stored order is read again, so a name
        // another save dropped meanwhile is not disabled. There is no rollback: the only partial state left behind
        // (stored Enabled but still run by Windows) is hidden from the list and never launched by the app, and the
        // next load retries the takeover.
        public IReadOnlySet<string> TakeOverWindowsEntries(bool launchSettingOn)
        {
            var takenOver = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!launchSettingOn)
            {
                LoggingService.LogInfo("Takeover skipped: \"Launch Enabled Programs On System Startup\" is off, so Windows keeps starting its entries");
                return takenOver;
            }

            var scan = ScanForTakeover();
            if (scan == null || scan.Candidates.Count == 0)
                return takenOver; // gate closed (logged) or nothing to take over: nothing is written

            // Everything that can fail without writing comes first: the clock, the record and the order
            byte[] disabledValue;
            try
            {
                disabledValue = StartupApprovedState.Disabled(_utcNow());
            }
            catch (ArgumentOutOfRangeException ex)
            {
                LoggingService.LogError($"Takeover skipped: the system clock is before 1601; Windows keeps starting {scan.Candidates.Count} program(s)", ex);
                return takenOver;
            }

            var (record, recordOk) = LoadTakenOver();
            if (!recordOk)
            {
                // Never disable an entry the uninstaller can't find again
                LoggingService.LogWarning($"Takeover skipped: {TAKEN_OVER_VALUE} under HKCU\\{APP_ORDER_KEY} is not REG_MULTI_SZ; Windows keeps starting {scan.Candidates.Count} program(s)");
                return takenOver;
            }

            var plan = BuildTakeoverOrder(LoadStoredOrder(), FilterByRecordRoom(scan.Candidates, record), scan.CurrentFingerprints);
            if (plan.OverCap > 0)
                LoggingService.LogWarning($"Not taking over {plan.OverCap} program(s): the stored order already holds {MAX_NAMES} names; Windows keeps starting them");
            foreach (var name in plan.KeptChanged)
                LoggingService.LogWarning($"'{name}' changed since it was enabled; taken over but not launched until re-enabled");
            if (plan.Accepted.Count == 0)
                return takenOver;

            if (!WriteTakeoverStore(plan.Next, record, plan.Accepted))
                return takenOver;

            var toDisable = StillStoredEnabled(plan.Accepted);
            if (toDisable.Count == 0)
                return takenOver;

            RegistryKey approvedWrite;
            try
            {
                // Created if missing (it can be absent on a fresh profile)
                approvedWrite = _root.CreateSubKey(STARTUP_APPROVED_KEY, writable: true);
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"Takeover skipped: HKCU\\{STARTUP_APPROVED_KEY} could not be opened for writing ({ex.GetType().Name}: {ex.Message}); Windows keeps starting {toDisable.Count} program(s)");
                return takenOver;
            }

            using (approvedWrite)
            {
                foreach (var name in toDisable)
                {
                    try
                    {
                        WriteApprovedDisabled(approvedWrite, name, disabledValue);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"Could not take over '{name}' ({ex.GetType().Name}: {ex.Message}); Windows keeps starting it");
                        continue;
                    }
                    takenOver.Add(name);
                    LoggingService.LogInfo($"Took over '{name}' from Windows startup; StartupController launches it from the next logon");
                }
            }
            return takenOver;
        }

        private sealed class TakeoverScan
        {
            // Windows-run string entries that can be stored: name -> current fingerprint, in Run order
            public List<KeyValuePair<string, string>> Candidates { get; } = new List<KeyValuePair<string, string>>();

            // Current fingerprints of the Windows-disabled string entries (for the migration in BuildTakeoverOrder)
            public Dictionary<string, string> CurrentFingerprints { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Gate (D-T1) and one read-only pass over Run. Null when the gate is closed (logged): without the app's own
        // enabled Run entry nothing would launch the entries at logon.
        private TakeoverScan? ScanForTakeover()
        {
            var scan = new TakeoverScan();
            using (var runKey = _root.OpenSubKey(RUN_KEY, false))
            using (var approvedKey = _root.OpenSubKey(STARTUP_APPROVED_KEY, false))
            {
                var problem = OwnEntryProblem(runKey, approvedKey);
                if (problem != null || runKey == null)
                {
                    LoggingService.LogInfo($"Takeover skipped: StartupController's own Run entry is {problem ?? "missing"}, so Windows keeps starting its entries");
                    return null;
                }

                foreach (var name in runKey.GetValueNames())
                {
                    if (string.Equals(name, STARTUP_CONTROLLER_NAME, StringComparison.OrdinalIgnoreCase))
                        continue; // never written

                    if (!TakeoverRecord.IsStorable(name))
                    {
                        // Empty, over-long or with a NUL: it could not be stored (or read back by name), so it would end
                        // up disabled in Windows and never launched
                        LoggingService.LogWarning($"Not taking over '{name}': the name is empty, longer than {MAX_NAME_LENGTH} characters or contains a NUL; Windows keeps starting it");
                        continue;
                    }

                    // One raw read, as in GetStartupPrograms. A non-string value can't be fingerprinted, so it would
                    // never be launched: Windows keeps it.
                    var (data, kind) = ReadRunValue(runKey, name);
                    if (data is not string raw || (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString))
                        continue;

                    var fingerprint = RunFingerprint.Compute(kind, raw);

                    // A wrong-kind approved value reads as null: Windows runs the entry (as in GetStartupPrograms)
                    if (StartupApprovedState.IsEnabled(approvedKey?.GetValue(name) as byte[]))
                        scan.Candidates.Add(new KeyValuePair<string, string>(name, fingerprint));
                    else
                        scan.CurrentFingerprints[name] = fingerprint;
                }
            }
            return scan;
        }

        // Candidates that fit in TakenOverPrograms (already recorded, or room left). The rest are not taken over: the
        // uninstaller could never give them back.
        private static List<KeyValuePair<string, string>> FilterByRecordRoom(List<KeyValuePair<string, string>> candidates, List<string> record)
        {
            var recorded = new HashSet<string>(record, StringComparer.OrdinalIgnoreCase);
            int room = TakeoverRecord.MaxNames - record.Count;
            var result = new List<KeyValuePair<string, string>>();
            int unrecordable = 0;
            foreach (var candidate in candidates)
            {
                if (recorded.Contains(candidate.Key))
                    result.Add(candidate);
                else if (room-- > 0)
                    result.Add(candidate);
                else
                    unrecordable++;
            }
            if (unrecordable > 0)
                LoggingService.LogWarning($"Not taking over {unrecordable} program(s): {TAKEN_OVER_VALUE} already holds {TakeoverRecord.MaxNames} names; Windows keeps starting them");
            return result;
        }

        // Writes the order, then the record (only when a name is new). False after logging when either fails: then
        // nothing may be written to StartupApproved.
        private bool WriteTakeoverStore(StoredOrder next, List<string> record, List<string> accepted)
        {
            try
            {
                WriteStoredOrder(next);
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Takeover skipped: the order could not be saved; Windows keeps starting {accepted.Count} program(s)", ex);
                return false;
            }

            var recorded = new HashSet<string>(record, StringComparer.OrdinalIgnoreCase);
            var newlyRecorded = accepted.Where(n => !recorded.Contains(n)).ToList();
            if (newlyRecorded.Count == 0)
                return true;
            try
            {
                using (var appKey = _root.CreateSubKey(APP_ORDER_KEY, writable: true))
                {
                    WriteMultiString(appKey, TAKEN_OVER_VALUE, record.Concat(newlyRecorded).ToArray());
                }
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Takeover skipped: {TAKEN_OVER_VALUE} could not be saved; Windows keeps starting {accepted.Count} program(s)", ex);
                return false;
            }
        }

        // Re-reads the stored order right before the StartupApproved writes: a save by another writer between the
        // takeover's own writes can have dropped a name from EnabledPrograms. Such a name is not disabled in Windows
        // (it would start nowhere); Windows keeps starting it and the next load retries.
        private List<string> StillStoredEnabled(List<string> accepted)
        {
            var stored = LoadStoredOrder();
            var result = new List<string>();
            foreach (var name in accepted)
            {
                if (stored.Enabled.Contains(name))
                    result.Add(name);
                else
                    LoggingService.LogWarning($"Not taking over '{name}': another save removed it from the enabled programs; Windows keeps starting it");
            }
            return result;
        }

        // The result of BuildTakeoverOrder. Accepted: candidates to disable in Windows (KeptChanged included).
        // KeptChanged: candidates already stored Enabled with another fingerprint; they stay Changed (D7).
        internal sealed record TakeoverPlan(StoredOrder Next, List<string> Accepted, List<string> KeptChanged, int OverCap);

        // The stored order after a takeover, built from the stored data only (not through MergeForSave), so names that
        // aren't candidates keep exactly their stored flag and fingerprint (a Changed row stays Changed).
        // - A candidate already in the order (hidden because Windows ran it) keeps its position (D-T2). New ones are
        //   appended in candidate order while the order holds fewer than MAX_NAMES names; the rest are not taken
        //   over (OverCap).
        // - An accepted candidate is Enabled with its current fingerprint, unless it is already stored Enabled with
        //   a different (or, once fingerprints exist, no) fingerprint: then its stored fingerprint is kept, so it
        //   lists as Changed and is not launched until the user re-enables it (D7 holds; KeptChanged).
        // - Migration: while no fingerprints are stored (legacy or pre-D7 store), stored-enabled names get their
        //   current fingerprint from currentFingerprints, the same set a UI save records, so the first
        //   EnabledFingerprints write doesn't turn them into Changed.
        internal static TakeoverPlan BuildTakeoverOrder(
            StoredOrder previous,
            IReadOnlyList<KeyValuePair<string, string>> candidates,
            IReadOnlyDictionary<string, string> currentFingerprints)
        {
            var order = new List<string>(previous.Order);
            var inOrder = new HashSet<string>(previous.Order, StringComparer.OrdinalIgnoreCase);
            var enabled = new HashSet<string>(previous.Enabled, StringComparer.OrdinalIgnoreCase);
            var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in previous.Fingerprints)
                fingerprints[pair.Key] = pair.Value;

            if (!previous.FingerprintsKnown)
            {
                foreach (var name in previous.Enabled)
                {
                    if (!fingerprints.ContainsKey(name) && currentFingerprints.TryGetValue(name, out var current))
                        fingerprints[name] = current;
                }
            }

            var accepted = new List<string>();
            var keptChanged = new List<string>();
            int overCap = 0;
            foreach (var candidate in candidates)
            {
                if (!inOrder.Contains(candidate.Key))
                {
                    if (order.Count >= MAX_NAMES) { overCap++; continue; }
                    order.Add(candidate.Key);
                    inOrder.Add(candidate.Key);
                }

                accepted.Add(candidate.Key);
                if (previous.Enabled.Contains(candidate.Key) && IsChanged(previous, candidate.Key, candidate.Value))
                {
                    keptChanged.Add(candidate.Key);
                    continue;
                }
                enabled.Add(candidate.Key);
                fingerprints[candidate.Key] = candidate.Value;
            }

            return new TakeoverPlan(StoredOrder.Create(order, enabled, fingerprints, fingerprintsKnown: true), accepted, keptChanged, overCap);
        }

        // Stored Enabled, and its stored fingerprint differs from the current one (or is missing once fingerprints exist)
        private static bool IsChanged(StoredOrder previous, string name, string current)
        {
            if (previous.Fingerprints.TryGetValue(name, out var stored))
                return !RunFingerprint.Matches(stored, current);
            return previous.FingerprintsKnown;
        }

        // Writes one value built by StartupApprovedState.Disabled, as REG_BINARY. Virtual as a test seam.
        internal virtual void WriteApprovedDisabled(RegistryKey approvedKey, string name, byte[] value)
        {
            approvedKey.SetValue(name, value, RegistryValueKind.Binary);
        }

        // The stored takeover record (see TakeoverRecord), cleaned. Ok is false when the value has the wrong kind.
        internal (List<string> Names, bool Ok) LoadTakenOver()
        {
            using (var appKey = _root.OpenSubKey(APP_ORDER_KEY, false))
            {
                var ok = TakeoverRecord.TryRead(appKey?.GetValue(TAKEN_OVER_VALUE), out var names);
                return (names, ok);
            }
        }

        // D-T6: taken-over programs that start nowhere because StartupController's own Run entry is not valid
        // (OwnEntryProblem: missing, not this app, without --launch or disabled in Windows): recorded names that are
        // still string values in Run, disabled in Windows, and stored Enabled and not Changed (the ones the app would
        // otherwise launch). 0 while the own entry is valid, or when the record can't be read. Read-only; logs a
        // Warning when > 0.
        public int CountStrandedTakenOver()
        {
            var (record, ok) = LoadTakenOver();
            if (!ok || record.Count == 0) return 0;

            using (var runKey = _root.OpenSubKey(RUN_KEY, false))
            using (var approvedKey = _root.OpenSubKey(STARTUP_APPROVED_KEY, false))
            {
                if (runKey == null) return 0;
                var problem = OwnEntryProblem(runKey, approvedKey);
                if (problem == null) return 0;

                var stored = LoadStoredOrder();
                int stranded = 0;
                foreach (var name in record)
                {
                    if (string.Equals(name, STARTUP_CONTROLLER_NAME, StringComparison.OrdinalIgnoreCase)) continue;
                    var (data, kind) = ReadRunValue(runKey, name);
                    if (data is not string raw || (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)) continue;
                    if (StartupApprovedState.IsEnabled(approvedKey?.GetValue(name) as byte[])) continue; // Windows starts it
                    if (IsLaunchable(stored, name, RunFingerprint.Compute(kind, raw)))
                        stranded++;
                }

                if (stranded > 0)
                    LoggingService.LogWarning($"{stranded} taken-over program(s) will not start at logon: StartupController's own Run entry is {problem}");
                return stranded;
            }
        }

        // Stored Enabled and not Changed: the same rule OrderMerger.Merge applies when listing (D7)
        private static bool IsLaunchable(StoredOrder stored, string name, string currentFingerprint)
        {
            if (!stored.Enabled.Contains(name)) return false;
            if (stored.Fingerprints.TryGetValue(name, out var fingerprint))
                return RunFingerprint.Matches(fingerprint, currentFingerprint);
            return !stored.FingerprintsKnown;
        }

        // The only read of a Run value's data: raw (unexpanded) data plus its kind. A value deleted meanwhile
        // gives (null, None). Virtual as a test seam.
        internal virtual (object? Data, RegistryValueKind Kind) ReadRunValue(RegistryKey runKey, string name)
        {
            var data = runKey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (data == null) return (null, RegistryValueKind.None);
            try
            {
                return (data, runKey.GetValueKind(name));
            }
            catch (IOException)
            {
                return (null, RegistryValueKind.None); // deleted between the two calls
            }
        }

        // Seam for tests that record or fail writes
        internal virtual void WriteMultiString(RegistryKey key, string name, string[] values)
        {
            key.SetValue(name, values, RegistryValueKind.MultiString);
        }

        // Load the stored launch list.
        // - ProgramOrder present as REG_MULTI_SZ: use it with EnabledPrograms and EnabledFingerprints.
        // - ProgramOrder present with any other kind: nothing is enabled and the legacy value is NOT used.
        // - ProgramOrder absent: migrate the legacy StartupOrder value in memory (nothing is written on load).
        // Lists are capped (MAX_NAMES names of at most MAX_NAME_LENGTH characters) with one Warning per load.
        public StoredOrder LoadStoredOrder()
        {
            using (var appKey = _root.OpenSubKey(APP_ORDER_KEY, false))
            {
                if (appKey == null) return StoredOrder.Empty;

                var capped = new List<string>();
                var result = Load(appKey, capped);
                if (capped.Count > 0)
                    LoggingService.LogWarning($"Stored order under HKCU\\{APP_ORDER_KEY} exceeded limits ({string.Join(", ", capped)}); extra entries ignored");
                return result;
            }
        }

        private static StoredOrder Load(RegistryKey appKey, List<string> capped)
        {
            var programOrder = appKey.GetValue(PROGRAM_ORDER_VALUE);
            var (fingerprints, fingerprintsKnown) = LoadFingerprints(appKey, capped);

            if (programOrder is string[] order)
            {
                var names = CleanNames(order, PROGRAM_ORDER_VALUE, capped);
                var enabledValue = appKey.GetValue(ENABLED_PROGRAMS_VALUE);
                if (enabledValue is not string[] enabled)
                {
                    if (enabledValue != null)
                        LoggingService.LogWarning($"{ENABLED_PROGRAMS_VALUE} under HKCU\\{APP_ORDER_KEY} is not REG_MULTI_SZ; nothing is enabled");
                    enabled = Array.Empty<string>();
                }
                var enabledSet = new HashSet<string>(CleanNames(enabled, ENABLED_PROGRAMS_VALUE, capped), StringComparer.OrdinalIgnoreCase);
                return StoredOrder.Create(names, names.Where(enabledSet.Contains), fingerprints, fingerprintsKnown);
            }

            if (programOrder != null)
            {
                LoggingService.LogWarning($"{PROGRAM_ORDER_VALUE} under HKCU\\{APP_ORDER_KEY} is not REG_MULTI_SZ; nothing is enabled and {LEGACY_ORDER_VALUE} is not used");
                return StoredOrder.Empty;
            }

            var legacyValue = appKey.GetValue(LEGACY_ORDER_VALUE);
            if (legacyValue is string legacy)
            {
                // 701437a stored exactly the enabled names, in order
                var names = CleanNames(legacy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), LEGACY_ORDER_VALUE, capped);
                if (names.Count > 0)
                    LoggingService.LogInfo($"Migrated legacy {LEGACY_ORDER_VALUE} ({names.Count} names)");
                return StoredOrder.Create(names, names, fingerprints, fingerprintsKnown);
            }

            if (legacyValue != null)
                LoggingService.LogWarning($"{LEGACY_ORDER_VALUE} under HKCU\\{APP_ORDER_KEY} is not a string; ignoring it");

            return StoredOrder.Empty;
        }

        // Known = the EnabledFingerprints value exists (in any form). A malformed value gives no fingerprints,
        // so every stored-enabled name comes back Changed: nothing is enabled.
        private static (List<KeyValuePair<string, string>> Fingerprints, bool Known) LoadFingerprints(RegistryKey appKey, List<string> capped)
        {
            var result = new List<KeyValuePair<string, string>>();
            var value = appKey.GetValue(ENABLED_FINGERPRINTS_VALUE);
            if (value == null) return (result, false);

            if (value is not string[] lines)
            {
                LoggingService.LogWarning($"{ENABLED_FINGERPRINTS_VALUE} under HKCU\\{APP_ORDER_KEY} is not REG_MULTI_SZ; nothing is enabled");
                return (result, true);
            }

            int malformed = 0;
            foreach (var line in lines.Take(MAX_NAMES))
            {
                if (RunFingerprint.TryParseLine(line, out var name, out var hash) && name.Length <= MAX_NAME_LENGTH)
                    result.Add(new KeyValuePair<string, string>(name, hash));
                else
                    malformed++;
            }
            if (lines.Length > MAX_NAMES)
                capped.Add($"{ENABLED_FINGERPRINTS_VALUE}: {lines.Length} lines");
            if (malformed > 0)
                LoggingService.LogWarning($"{ENABLED_FINGERPRINTS_VALUE}: ignored {malformed} malformed line(s)");
            return (result, true);
        }

        // Drop empty/whitespace and over-long names and case-insensitive duplicates (first one wins),
        // then keep at most MAX_NAMES. Truncations are reported through 'capped'.
        private static List<string> CleanNames(IEnumerable<string> names, string source, List<string> capped)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            int tooLong = 0, overCap = 0;
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.Length > MAX_NAME_LENGTH) { tooLong++; continue; }
                if (!seen.Add(name)) continue;
                if (result.Count >= MAX_NAMES) { overCap++; continue; }
                result.Add(name);
            }
            if (tooLong > 0) capped.Add($"{source}: {tooLong} name(s) over {MAX_NAME_LENGTH} characters");
            if (overCap > 0) capped.Add($"{source}: {overCap} name(s) past {MAX_NAMES}");
            return result;
        }

        // Applies the caps to what is about to be written
        private static StoredOrder Cap(StoredOrder order)
        {
            var capped = new List<string>();
            var names = CleanNames(order.Order, PROGRAM_ORDER_VALUE, capped);
            if (capped.Count == 0) return order;

            LoggingService.LogWarning($"Order to save exceeded limits ({string.Join(", ", capped)}); extra entries not written");
            var kept = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            return StoredOrder.Create(
                names,
                order.Enabled.Where(kept.Contains),
                order.Fingerprints.Where(f => kept.Contains(f.Key)),
                fingerprintsKnown: true);
        }

        public void AddThisApplicationToStartup(string exePath)
        {
            // Registry key for current user startup; created if missing (it can be absent on a fresh profile)
            using (var key = _root.CreateSubKey(RUN_KEY, writable: true))
            {
                // Always quote the path in case it contains spaces
                key.SetValue(STARTUP_CONTROLLER_NAME, $"\"{exePath}\" --launch", RegistryValueKind.String);
            }
        }

        public void RemoveThisApplicationFromStartup()
        {
            // Registry key for current user startup
            using (var key = _root.OpenSubKey(RUN_KEY, true))
            {
                if (key != null)
                {
                    key.DeleteValue(STARTUP_CONTROLLER_NAME, false);
                }
            }
        }
    }
}
