using Microsoft.Win32;

namespace StartupController
{
    public interface IUserSettings
    {
        bool GetSilenceNotifications();
        void SetSilenceNotifications(bool value);
        bool GetStartToTray();
        void SetStartToTray(bool value);
        bool GetLaunchProgramsOnStartup();
        void SetLaunchProgramsOnStartup(bool value);
        bool GetAutoSaveOnChange();
        void SetAutoSaveOnChange(bool value);
    }

    // App settings stored as DWORDs under <root>\Software\StartupController.
    // Values are cached after the first read; Set writes through and then updates the cache.
    public sealed class UserSettings : IUserSettings
    {
        private const string SETTINGS_KEY = AppRegistryPaths.AppKey;
        private const string SILENCE_NOTIFICATIONS = "SilenceNotifications";
        private const string START_TO_TRAY = "StartToTray";
        private const string LAUNCH_PROGRAMS_ON_STARTUP = "LaunchProgramsOnStartup";
        private const string AUTOSAVE_ON_CHANGE = "AutoSaveOnChange";

        private readonly RegistryKey _root;
        private readonly object _lock = new object();
        private readonly Dictionary<string, bool> _cache = new Dictionary<string, bool>();

        public UserSettings(RegistryKey root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public bool GetSilenceNotifications() => GetFlag(SILENCE_NOTIFICATIONS);
        public void SetSilenceNotifications(bool value) => SetFlag(SILENCE_NOTIFICATIONS, value);

        // --- Start to Tray setting ---
        public bool GetStartToTray() => GetFlag(START_TO_TRAY);
        public void SetStartToTray(bool value) => SetFlag(START_TO_TRAY, value);

        // --- Launch Programs On Startup setting ---
        public bool GetLaunchProgramsOnStartup() => GetFlag(LAUNCH_PROGRAMS_ON_STARTUP);
        public void SetLaunchProgramsOnStartup(bool value) => SetFlag(LAUNCH_PROGRAMS_ON_STARTUP, value);

        // --- Auto-save on change setting ---
        public bool GetAutoSaveOnChange() => GetFlag(AUTOSAVE_ON_CHANGE);
        public void SetAutoSaveOnChange(bool value) => SetFlag(AUTOSAVE_ON_CHANGE, value);

        private bool GetFlag(string name)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(name, out var cached))
                    return cached;

                using var key = _root.OpenSubKey(SETTINGS_KEY, false);
                var value = key?.GetValue(name, 0) is int v && v == 1;
                _cache[name] = value;
                return value;
            }
        }

        private void SetFlag(string name, bool value)
        {
            lock (_lock)
            {
                using var key = _root.CreateSubKey(SETTINGS_KEY);
                if (key == null) return;
                key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
                _cache[name] = value;
            }
        }
    }
}
