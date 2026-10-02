using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StartupController.Tests.Infrastructure
{
    /// <summary>
    /// Throwaway registry root under HKCU\Software\StartupController.Tests\p{pid}-{guid}.
    /// Pass <see cref="Root"/> to the services: their relative paths (Software\Microsoft\...\Run,
    /// Software\StartupController) then resolve inside the sandbox, never in the real HKCU keys.
    /// The whole tree is deleted in Dispose.
    /// </summary>
    public sealed class RegistrySandbox : IDisposable
    {
        public const string ParentPath = @"Software\StartupController.Tests";
        public const string RequiredPrefix = @"HKEY_CURRENT_USER\Software\StartupController.Tests\";

        public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        public const string AppPath = @"Software\StartupController";
        public const string OrderValue = "StartupOrder"; // legacy REG_SZ
        public const string ProgramOrderValue = "ProgramOrder";
        public const string EnabledProgramsValue = "EnabledPrograms";
        public const string EnabledFingerprintsValue = "EnabledFingerprints";
        public const string TakenOverValue = "TakenOverPrograms";

        // Test classes run in parallel: creating a sandbox while another one deletes the shared parent
        // key fails with "marked for deletion", so both go through this lock.
        private static readonly object ParentLock = new object();

        private bool _disposed;

        // Sandbox key names: "p{owner pid}-{guid}". Plain "{guid}" names come from builds before the pid prefix.
        private static readonly Regex SandboxName = new Regex(@"^(?:p(?<pid>\d+)-)?[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public RegistrySandbox() : this(Environment.ProcessId)
        {
        }

        // ownerPid is only overridden by the sweep tests, to fake a sandbox left behind by a dead test run
        internal RegistrySandbox(int ownerPid)
        {
            RelativePath = ParentPath + @"\p" + ownerPid.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            lock (ParentLock)
            {
                Root = Registry.CurrentUser.CreateSubKey(RelativePath, writable: true)
                    ?? throw new InvalidOperationException("Could not create registry sandbox " + RelativePath);
            }
            Guard(Root);
        }

        /// <summary>Path of the sandbox relative to HKCU.</summary>
        public string RelativePath { get; }

        /// <summary>Root key to inject into the services.</summary>
        public RegistryKey Root { get; }

        /// <summary>Throws unless the key lives strictly below HKCU\Software\StartupController.Tests\.</summary>
        public static void Guard(RegistryKey key)
        {
            var name = key.Name;
            if (!name.StartsWith(RequiredPrefix, StringComparison.OrdinalIgnoreCase) || name.Length <= RequiredPrefix.Length)
                throw new InvalidOperationException($"Registry sandbox guard: '{name}' is not below {RequiredPrefix}");
        }

        /// <summary>True if a sandbox key with this HKCU-relative path still exists. Only answers for paths below the parent.</summary>
        public static bool Exists(string relativePath)
        {
            if (!relativePath.StartsWith(ParentPath + @"\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Registry sandbox guard: '{relativePath}' is not below {ParentPath}");
            using var key = Registry.CurrentUser.OpenSubKey(relativePath, writable: false);
            return key != null;
        }

        /// <summary>
        /// Best-effort removal of sandboxes left behind by crashed or killed test runs. Deletes only direct
        /// children of HKCU\Software\StartupController.Tests whose names match the sandbox pattern and whose
        /// owning process is gone. Sandboxes of this process and of other live test runs are kept.
        /// Returns the number of keys removed; never throws for registry errors.
        /// </summary>
        public static int SweepStale()
        {
            int removed = 0;
            lock (ParentLock)
            {
                using var parent = Registry.CurrentUser.OpenSubKey(ParentPath, writable: true);
                if (parent == null) return 0;
                if (!string.Equals(parent.Name, RequiredPrefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Registry sandbox guard: sweep parent is '{parent.Name}'");

                foreach (var child in parent.GetSubKeyNames())
                {
                    var match = SandboxName.Match(child);
                    if (!match.Success) continue; // not a sandbox: leave it alone
                    if (match.Groups["pid"].Success && IsAlive(match.Groups["pid"].Value)) continue;

                    try
                    {
                        using (var key = parent.OpenSubKey(child, writable: false))
                        {
                            if (key == null) continue;
                            Guard(key);
                        }
                        parent.DeleteSubKeyTree(child, throwOnMissingSubKey: false);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
                    {
                        // best effort: another run may be deleting it, or access is denied
                    }
                }
            }
            return removed;
        }

        private static bool IsAlive(string pidText)
        {
            if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                return false;
            if (pid == Environment.ProcessId) return true;
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false; // no such process
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true; // can't tell: keep the key
            }
        }

        public void SeedRun(string name, string command)
        {
            using var key = CreateChild(RunPath);
            key.SetValue(name, command, RegistryValueKind.String);
        }

        /// <summary>Run value of any kind (e.g. REG_EXPAND_SZ or a non-string value).</summary>
        public void SeedRunValue(string name, object value, RegistryValueKind kind)
        {
            using var key = CreateChild(RunPath);
            key.SetValue(name, value, kind);
        }

        public void SeedApproved(string name, byte[] value)
        {
            using var key = CreateChild(ApprovedPath);
            key.SetValue(name, value, RegistryValueKind.Binary);
        }

        /// <summary>StartupApproved value of any kind (e.g. a wrong-kind REG_SZ or REG_DWORD).</summary>
        public void SeedApprovedValue(string name, object value, RegistryValueKind kind)
        {
            using var key = CreateChild(ApprovedPath);
            key.SetValue(name, value, kind);
        }

        /// <summary>Creates the Run / StartupApproved\Run key without values.</summary>
        public void CreateRunKey() => CreateChild(RunPath).Dispose();

        public void CreateApprovedKey() => CreateChild(ApprovedPath).Dispose();

        /// <summary>Writes the current (legacy) order format: REG_SZ, names joined with ';'.</summary>
        public void SeedOrder(params string[] names)
        {
            using var key = CreateChild(AppPath);
            key.SetValue(OrderValue, string.Join(";", names), RegistryValueKind.String);
        }

        /// <summary>Writes the v2 order format: ProgramOrder and EnabledPrograms as REG_MULTI_SZ.</summary>
        public void SeedStoredOrder(string[] order, string[] enabled)
        {
            using var key = CreateChild(AppPath);
            key.SetValue(EnabledProgramsValue, enabled, RegistryValueKind.MultiString);
            key.SetValue(ProgramOrderValue, order, RegistryValueKind.MultiString);
        }

        public void DeleteRunValue(string name)
        {
            using var key = CreateChild(RunPath);
            key.DeleteValue(name, throwOnMissingValue: false);
        }

        /// <summary>Every value under the sub key (name, kind, data), for byte-identical comparisons. Empty if the key is missing.</summary>
        public List<(string Name, RegistryValueKind Kind, object? Data)> ReadAllValues(string subPath)
        {
            ThrowIfDisposed();
            Guard(Root);
            using var key = Root.OpenSubKey(subPath, writable: false);
            if (key == null) return new List<(string, RegistryValueKind, object?)>();
            return key.GetValueNames()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => (n, key.GetValueKind(n), key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)))
                .ToList();
        }

        /// <summary>ReadAllValues as "name|kind|data" lines (byte[] as hex, string[] joined), for comparisons by value.</summary>
        public List<string> Dump(string subPath)
        {
            return ReadAllValues(subPath)
                .Select(v => v.Name + "|" + v.Kind + "|" + (v.Data switch
                {
                    byte[] bytes => Convert.ToHexString(bytes),
                    string[] strings => "[" + string.Join("][", strings) + "]",
                    null => "<null>",
                    _ => v.Data.ToString()
                }))
                .ToList();
        }

        public void SeedAppValue(string name, object value, RegistryValueKind kind)
        {
            using var key = CreateChild(AppPath);
            key.SetValue(name, value, kind);
        }

        public object? ReadValue(string subPath, string name)
        {
            ThrowIfDisposed();
            Guard(Root);
            using var key = Root.OpenSubKey(subPath, writable: false);
            return key?.GetValue(name);
        }

        public RegistryValueKind? ReadKind(string subPath, string name)
        {
            ThrowIfDisposed();
            Guard(Root);
            using var key = Root.OpenSubKey(subPath, writable: false);
            if (key == null || key.GetValue(name) == null) return null;
            return key.GetValueKind(name);
        }

        /// <summary>A read-only handle to the sandbox root, for testing write failures. Guarded like the writable root.</summary>
        public RegistryKey OpenReadOnlyRoot()
        {
            ThrowIfDisposed();
            var key = Registry.CurrentUser.OpenSubKey(RelativePath, writable: false)
                ?? throw new InvalidOperationException("Sandbox root vanished: " + RelativePath);
            Guard(key);
            return key;
        }

        public bool KeyExists(string subPath)
        {
            ThrowIfDisposed();
            using var key = Root.OpenSubKey(subPath, writable: false);
            return key != null;
        }

        private RegistryKey CreateChild(string subPath)
        {
            ThrowIfDisposed();
            Guard(Root);
            var key = Root.CreateSubKey(subPath, writable: true)
                ?? throw new InvalidOperationException("Could not create " + subPath);
            Guard(key);
            return key;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RegistrySandbox));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Guard(Root);
            Root.Dispose();

            lock (ParentLock)
            {
                Registry.CurrentUser.DeleteSubKeyTree(RelativePath, throwOnMissingSubKey: false);

                // Remove the shared parent once the last sandbox is gone
                try
                {
                    Registry.CurrentUser.DeleteSubKey(ParentPath, throwOnMissingSubKey: false);
                }
                catch (InvalidOperationException)
                {
                    // parent still has other sandboxes
                }
            }
        }
    }
}
