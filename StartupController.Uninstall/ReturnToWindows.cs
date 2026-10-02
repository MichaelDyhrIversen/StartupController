using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace StartupController.Uninstall
{
    // Gives the taken-over entries back to Windows (D-T3, D-T4): for every name in TakenOverPrograms that still is a
    // string value (REG_SZ or REG_EXPAND_SZ) in Run, StartupApproved\Run\<name> is set to 02 + 11 zero bytes (enabled)
    // and the name is removed from TakenOverPrograms. Deliberately no check of the app's enabled flag, the D7
    // fingerprint or the current StartupApproved bytes (D-T4); the app's state is only logged (by name).
    // Never writes Run, ProgramOrder, EnabledPrograms or EnabledFingerprints, never deletes a value or key, and never
    // touches the app's own entry. Every registry operation is caught; nothing here throws for registry errors.
    internal class ReturnToWindows
    {
        private const string APPROVED_KEY = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ENABLED_PROGRAMS_VALUE = "EnabledPrograms";
        private const string ENABLED_FINGERPRINTS_VALUE = "EnabledFingerprints";

        internal sealed class Result
        {
            internal Result(int returned, int recorded)
            {
                Returned = returned;
                Recorded = recorded;
            }

            internal int Returned { get; }

            // Recorded names after cleaning (empty, over-long and duplicate names and the own entry dropped)
            internal int Recorded { get; }
        }

        // root is HKCU in the helper, a sandbox key in tests
        internal static Result Run(RegistryKey root, IUninstallLog log) => new ReturnToWindows().Execute(root, log);

        // The recorded names after cleaning (a hand-edited own entry included; Execute keeps it). Never throws.
        internal static List<string> RecordedNames(RegistryKey root, IUninstallLog log)
        {
            try
            {
                using (var appKey = root.OpenSubKey(AppRegistryPaths.AppKey, writable: false))
                {
                    if (appKey == null)
                    {
                        log.Info(@"HKCU\" + AppRegistryPaths.AppKey + " is missing: nothing to return");
                        return new List<string>();
                    }
                    var value = appKey.GetValue(TakeoverRecord.ValueName);
                    if (value == null)
                    {
                        log.Info(TakeoverRecord.ValueName + " is missing: nothing to return");
                        return new List<string>();
                    }
                    if (!TakeoverRecord.TryRead(value, out var names))
                    {
                        log.Warning(TakeoverRecord.ValueName + " is not REG_MULTI_SZ: nothing to return");
                        return new List<string>();
                    }
                    if (value is string[] raw && raw.Length != names.Count)
                        log.Warning(TakeoverRecord.ValueName + ": ignored " + (raw.Length - names.Count).ToString(CultureInfo.InvariantCulture)
                            + " empty, over-long or duplicate name(s)");
                    return names;
                }
            }
            catch (Exception ex)
            {
                log.Error("Could not read " + TakeoverRecord.ValueName + " (" + ex.GetType().Name + "): nothing to return");
                return new List<string>();
            }
        }

        // How many recorded names Execute would return: not the own entry, and still a string value in Run.
        // This is the number the prompt shows. Never throws (0 when Run can't be read).
        internal static int ReturnableCount(RegistryKey root, IEnumerable<string> names)
        {
            try
            {
                using (var runKey = root.OpenSubKey(AppRegistryPaths.RunKey, writable: false))
                {
                    if (runKey == null) return 0;
                    return names.Count(n => !IsOwnEntry(n) && IsStringValue(runKey, n));
                }
            }
            catch
            {
                return 0;
            }
        }

        // names: the result of RecordedNames when the caller already read it, otherwise null
        internal Result Execute(RegistryKey root, IUninstallLog log, IReadOnlyList<string>? names = null)
        {
            names ??= RecordedNames(root, log);
            if (names.Count == 0)
                return new Result(0, 0);

            var appState = AppState.Read(root);
            var returned = new List<string>();
            try
            {
                using (var runKey = root.OpenSubKey(AppRegistryPaths.RunKey, writable: false))
                using (var approvedKey = root.OpenSubKey(APPROVED_KEY, writable: true))
                {
                    if (runKey == null || approvedKey == null)
                    {
                        log.Info((runKey == null ? "Run" : @"StartupApproved\Run") + " key is missing: nothing to return");
                        return new Result(0, RecordedCount(names));
                    }

                    foreach (var name in names)
                    {
                        var reason = ReturnOne(runKey, approvedKey, name);
                        if (reason == null)
                        {
                            returned.Add(name);
                            log.Info("Returned '" + name + "' to Windows" + appState.Describe(runKey, name));
                        }
                        else
                        {
                            log.Info("Kept '" + name + "': " + reason);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Opening the keys failed; whatever was written so far stays, and the record is still pruned below
                log.Error("Could not open the startup keys (" + ex.GetType().Name + ")");
            }

            if (returned.Count > 0)
                PruneRecord(root, returned, log);
            return new Result(returned.Count, RecordedCount(names));
        }

        private static int RecordedCount(IEnumerable<string> names) => names.Count(n => !IsOwnEntry(n));

        // Null when the name was returned, otherwise why it was kept
        private string? ReturnOne(RegistryKey runKey, RegistryKey approvedKey, string name)
        {
            try
            {
                if (IsOwnEntry(name))
                    return "StartupController's own entry";
                if (runKey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) == null)
                    return "not in Run";
                if (!IsStringValue(runKey, name))
                    return "not a string";

                WriteApprovedEnabled(approvedKey, name);
                return null;
            }
            catch (Exception ex)
            {
                return "could not be written (" + ex.GetType().Name + ")";
            }
        }

        private static bool IsStringValue(RegistryKey runKey, string name)
        {
            try
            {
                if (runKey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) == null) return false;
                var kind = runKey.GetValueKind(name);
                return kind == RegistryValueKind.String || kind == RegistryValueKind.ExpandString;
            }
            catch
            {
                return false;
            }
        }

        // Removes the returned names from TakenOverPrograms. Kept names stay. A failure is logged: a stale name is
        // harmless, a later run writes the same enabled value again.
        private void PruneRecord(RegistryKey root, List<string> returned, IUninstallLog log)
        {
            try
            {
                using (var appKey = root.OpenSubKey(AppRegistryPaths.AppKey, writable: true))
                {
                    if (appKey == null) return;
                    if (!TakeoverRecord.TryRead(appKey.GetValue(TakeoverRecord.ValueName), out var current))
                        return;
                    var gone = new HashSet<string>(returned, StringComparer.OrdinalIgnoreCase);
                    WriteRecord(appKey, current.Where(n => !gone.Contains(n)).ToArray());
                }
            }
            catch (Exception ex)
            {
                log.Error("Could not update " + TakeoverRecord.ValueName + " (" + ex.GetType().Name + "); the returned names stay listed");
            }
        }

        private static bool IsOwnEntry(string name) =>
            string.Equals(name, AppRegistryPaths.AppRunValueName, StringComparison.OrdinalIgnoreCase);

        // Seams for tests that fail writes. REG_BINARY only.
        internal virtual void WriteApprovedEnabled(RegistryKey approvedKey, string name)
        {
            approvedKey.SetValue(name, StartupApprovedState.Enabled(), RegistryValueKind.Binary);
        }

        internal virtual void WriteRecord(RegistryKey appKey, string[] names)
        {
            appKey.SetValue(TakeoverRecord.ValueName, names, RegistryValueKind.MultiString);
        }

        // The app's stored state, only to log what a return overrides (D-T4 returns regardless). Read-only.
        private sealed class AppState
        {
            private readonly HashSet<string> _enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private bool _known; // the app has read the store and EnabledFingerprints exists
            private bool _readable;

            internal static AppState Read(RegistryKey root)
            {
                var state = new AppState();
                try
                {
                    using (var appKey = root.OpenSubKey(AppRegistryPaths.AppKey, writable: false))
                    {
                        if (appKey?.GetValue(ENABLED_PROGRAMS_VALUE) is string[] enabled)
                        {
                            state._readable = true;
                            foreach (var name in enabled) state._enabled.Add(name);
                        }
                        var lines = appKey?.GetValue(ENABLED_FINGERPRINTS_VALUE);
                        state._known = lines != null;
                        if (lines is string[] fingerprints)
                        {
                            foreach (var line in fingerprints)
                            {
                                int bar = line?.LastIndexOf('|') ?? -1;
                                if (bar > 0) state._fingerprints[line!.Substring(0, bar)] = line.Substring(bar + 1);
                            }
                        }
                    }
                }
                catch
                {
                    state._readable = false;
                }
                return state;
            }

            // " (it was Disabled in StartupController)", " (it was Changed in StartupController)" or ""
            internal string Describe(RegistryKey runKey, string name)
            {
                if (!_readable) return "";
                if (!_enabled.Contains(name)) return " (it was Disabled in StartupController)";
                if (!_known) return "";
                if (!_fingerprints.TryGetValue(name, out var stored)) return " (it was Changed in StartupController)";
                try
                {
                    var raw = runKey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (raw == null) return "";
                    var current = FingerprintHash.Compute(runKey.GetValueKind(name), raw);
                    return string.Equals(stored, current, StringComparison.OrdinalIgnoreCase) ? "" : " (it was Changed in StartupController)";
                }
                catch
                {
                    return "";
                }
            }
        }
    }
}
