namespace StartupController
{
    // Logic behind the settings checkboxes, without WinForms. Each Apply* method returns the value the checkbox
    // should show: the requested value on success, the previous one after a failure (already logged and notified).
    public sealed class SettingsController
    {
        private readonly IUserSettings _settings;
        private readonly IStartupRegistry _registry;
        private readonly INotifier _notifier;
        private readonly string _exePath;

        public SettingsController(IUserSettings settings, IStartupRegistry registry, INotifier notifier, string exePath)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
            _exePath = exePath ?? throw new ArgumentNullException(nameof(exePath));
        }

        public bool ApplyStartToTray(bool value) =>
            Apply("Start to tray", value, () => _settings.SetStartToTray(value));

        public bool ApplySilenceNotifications(bool value) =>
            Apply("Silence notifications", value, () => _settings.SetSilenceNotifications(value));

        public bool ApplyAutoSaveOnChange(bool value) =>
            Apply("AutoSave", value, () => _settings.SetAutoSaveOnChange(value));

        // The Run entry is written first, then the setting, so the setting never claims an entry that isn't there.
        // If the setting write fails, the Run change is undone (best effort) to keep the two in sync.
        public bool ApplyLaunchProgramsOnStartup(bool value) =>
            Apply("Launch programs on startup", value, () =>
            {
                SetRunEntry(value);
                try
                {
                    _settings.SetLaunchProgramsOnStartup(value);
                }
                catch
                {
                    try
                    {
                        SetRunEntry(!value);
                    }
                    catch (Exception undoEx)
                    {
                        LoggingService.LogError($"Failed to undo the change to HKCU\\{AppRegistryPaths.RunKey}\\{AppRegistryPaths.AppRunValueName}", undoEx);
                    }
                    throw;
                }
            });

        private void SetRunEntry(bool present)
        {
            try
            {
                if (present)
                    _registry.AddThisApplicationToStartup(_exePath);
                else
                    _registry.RemoveThisApplicationFromStartup();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not update HKCU\\{AppRegistryPaths.RunKey}\\{AppRegistryPaths.AppRunValueName}: {ex.Message}", ex);
            }
        }

        private bool Apply(string setting, bool value, Action write)
        {
            try
            {
                write();
                LoggingService.LogInfo($"{setting} {(value ? "on" : "off")}");
                return value;
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Failed to turn {setting} {(value ? "on" : "off")} (HKCU\\{AppRegistryPaths.AppKey})", ex);
                _notifier.SafeNotify($"Failed to change \"{setting}\": {ex.Message}");
                return !value;
            }
        }
    }
}
