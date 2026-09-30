using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StartupController
{
    public class StartupRegistryService : IStartupRegistry
    {
        // Registry paths
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string STARTUP_APPROVED_KEY = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string APP_ORDER_KEY = @"Software\StartupController";
        private const string STARTUP_CONTROLLER_NAME = "StartupController";

        // Order storage (REG_MULTI_SZ). EnabledPrograms and EnabledFingerprints are written first and
        // ProgramOrder last: migration counts as done only once ProgramOrder exists.
        internal const string PROGRAM_ORDER_VALUE = "ProgramOrder";
        internal const string ENABLED_PROGRAMS_VALUE = "EnabledPrograms";
        internal const string ENABLED_FINGERPRINTS_VALUE = "EnabledFingerprints"; // "name|sha256hex" (D7)

        // Legacy REG_SZ, ';'-joined enabled names (701437a). Read for migration only; never written or deleted.
        internal const string LEGACY_ORDER_VALUE = "StartupOrder";

        // Caps for stored lists, applied on read and on write
        internal const int MAX_NAMES = 1024;
        internal const int MAX_NAME_LENGTH = 260;

        // StartupApproved first bytes seen from Windows; anything else is logged
        private static readonly byte[] KnownApprovedStates = { 0x01, 0x02, 0x03, 0x06, 0x07 };

        // All key paths are relative to this root (HKCU in the app, a sandbox key in tests)
        private readonly RegistryKey _root;

        public StartupRegistryService() : this(Registry.CurrentUser)
        {
        }

        public StartupRegistryService(RegistryKey root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        // Run entries that Windows has disabled (the ones StartupController may launch), merged with the stored order.
        // Entries Windows runs itself (no StartupApproved value, or an even first byte), the app's own entry and
        // non-string Run values (which can't be fingerprinted) are never listed, so they can't be launched.
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
                        Fingerprint = fingerprint
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
            var toWrite = Cap(OrderMerger.MergeForSave(displayed, LoadStoredOrder()));
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
            // Registry key for current user startup
            using (var key = _root.OpenSubKey(RUN_KEY, true))
            {
                if (key != null)
                {
                    // Always quote the path in case it contains spaces
                    key.SetValue(STARTUP_CONTROLLER_NAME, $"\"{exePath}\" --launch", RegistryValueKind.String);
                }
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
