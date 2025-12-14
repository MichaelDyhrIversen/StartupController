using Microsoft.Win32;

namespace StartupController
{
    public static class UserSettingsService
    {
        private const string SETTINGS_KEY = @"Software\StartupController";
        private const string SILENCE_NOTIFICATIONS = "SilenceNotifications";
        private const string START_TO_TRAY = "StartToTray";
        private const string LAUNCH_PROGRAMS_ON_STARTUP = "LaunchProgramsOnStartup"; 

        public static bool GetSilenceNotifications()
        {
            using var key = Registry.CurrentUser.OpenSubKey(SETTINGS_KEY, false);
            return key?.GetValue(SILENCE_NOTIFICATIONS, 0) is int v && v == 1;
        }

        public static void SetSilenceNotifications(bool value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(SETTINGS_KEY);
            key?.SetValue(SILENCE_NOTIFICATIONS, value ? 1 : 0, RegistryValueKind.DWord);
        }

        // --- Start to Tray setting ---
        public static bool GetStartToTray()
        {
            using var key = Registry.CurrentUser.OpenSubKey(SETTINGS_KEY, false);
            return key?.GetValue(START_TO_TRAY, 0) is int v && v == 1;
        }

        public static void SetStartToTray(bool value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(SETTINGS_KEY);
            key?.SetValue(START_TO_TRAY, value ? 1 : 0, RegistryValueKind.DWord);
        }

        // --- Launch Programs On Startup setting ---
        public static bool GetLaunchProgramsOnStartup()
        {
            using var key = Registry.CurrentUser.OpenSubKey(SETTINGS_KEY, false);
            return key?.GetValue(LAUNCH_PROGRAMS_ON_STARTUP, 0) is int v && v == 1;
        }

        public static void SetLaunchProgramsOnStartup(bool value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(SETTINGS_KEY);
            key?.SetValue(LAUNCH_PROGRAMS_ON_STARTUP, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
