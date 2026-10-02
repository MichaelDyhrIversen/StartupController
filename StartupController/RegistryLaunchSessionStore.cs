using Microsoft.Win32;

namespace StartupController
{
    // LaunchSession.{session id} (REG_SZ) under <root>\Software\StartupController, one value per WTS session id. Each
    // holds only the session key text: no user data, command or path. Only Program.cs passes Registry.CurrentUser;
    // tests pass a sandbox root.
    internal sealed class RegistryLaunchSessionStore : ILaunchSessionStore
    {
        private readonly RegistryKey _root;

        internal RegistryLaunchSessionStore(RegistryKey root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        // Absent = no record. A wrong kind, an over-long value, text in the wrong format or a key for another session
        // also counts as no record, with a Warning that names the value only (it can cost at most one extra run, and
        // writing it needs the same access as writing Run). Registry errors propagate; the guard turns them into
        // Unavailable.
        public string? Read(uint sessionId)
        {
            var name = AppRegistryPaths.LaunchSessionValueName(sessionId);
            using var key = _root.OpenSubKey(AppRegistryPaths.AppKey, writable: false);
            if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;

            // The kind first: a planted REG_BINARY or REG_MULTI_SZ of any size is never read into memory
            if (key.GetValueKind(name) != RegistryValueKind.String)
                return Malformed(name);

            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) return null; // deleted between the two calls
            if (value is not string text
                || !LaunchSessionKey.TryGetSessionId(text, out var storedId) || storedId != sessionId)
                return Malformed(name);
            return text;
        }

        // Replaces this session's value (also a malformed one); nothing else under the key is touched, including the
        // values of other sessions
        public void Write(string key)
        {
            if (!LaunchSessionKey.TryGetSessionId(key, out var sessionId))
                throw new ArgumentException("Not a valid launch session key", nameof(key));

            using var appKey = _root.CreateSubKey(AppRegistryPaths.AppKey, writable: true)
                ?? throw new IOException($"Could not open {AppRegistryPaths.AppKey}");
            appKey.SetValue(AppRegistryPaths.LaunchSessionValueName(sessionId), key, RegistryValueKind.String);
        }

        private static string? Malformed(string name)
        {
            LoggingService.LogWarning($"Ignoring malformed registry value {name}");
            return null;
        }
    }
}
